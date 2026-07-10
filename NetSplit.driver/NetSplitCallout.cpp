#define INITGUID
#include <initguid.h>
#include "NetSplitCallout.h"

// Fixed, hardcoded identifiers for our sublayer and callout - POC, single instance only.
DEFINE_GUID(NETSPLIT_SUBLAYER_GUID,
    0x2f1a9b3c, 0x4d5e, 0x4a6b, 0x8c, 0x1d, 0x3e, 0x2f, 0x4a, 0x5b, 0x6c, 0x7d);

DEFINE_GUID(NETSPLIT_CALLOUT_GUID,
    0x7b6a5d4c, 0x3e2f, 0x4a1b, 0x9c, 0x8d, 0x1e, 0x2f, 0x3a, 0x4b, 0x5c, 0x6d);

// The one hardcoded rule: process name -> target local IPv4 address.
// Default matches Phase 1's example (chrome.exe). LocalAddressV4 starts at 0
// (disabled/no-op) because the Wi-Fi adapter's DHCP address isn't known at
// compile time; the debug UI (or any caller of IOCTL_NETSPLIT_SET_TARGET) sets it.
static WCHAR g_TargetProcessName[64] = L"chrome.exe";
static USHORT g_TargetProcessNameLenChars = 10;
static UINT32 g_TargetAddressV4 = 0;

static HANDLE g_EngineHandle = nullptr;
static BOOLEAN g_CalloutRegistered = FALSE;

static USHORT
NetSplitWideStrLen(
    _In_ PCWSTR String
)
{
    USHORT length = 0;
    while (String[length] != L'\0' && length < 63)
    {
        length++;
    }
    return length;
}

VOID
NetSplitSetTarget(
    _In_ PCWSTR ProcessName,
    _In_ UINT32 LocalAddressV4
)
{
    USHORT length = NetSplitWideStrLen(ProcessName);
    RtlCopyMemory(g_TargetProcessName, ProcessName, length * sizeof(WCHAR));
    g_TargetProcessName[length] = L'\0';
    g_TargetProcessNameLenChars = length;
    g_TargetAddressV4 = LocalAddressV4;
}

// Case-insensitive suffix match: does the process's full NT path end with
// "\<g_TargetProcessName>" (or equal it)? Good enough to recognize chrome.exe
// regardless of install directory.
static BOOLEAN
NetSplitPathMatchesTarget(
    _In_reads_bytes_(SizeBytes) PWCH Path,
    _In_ USHORT SizeBytes
)
{
    USHORT pathLenChars = (USHORT)(SizeBytes / sizeof(WCHAR));

    if (pathLenChars < g_TargetProcessNameLenChars)
    {
        return FALSE;
    }

    UNICODE_STRING tail;
    tail.Buffer = Path + (pathLenChars - g_TargetProcessNameLenChars);
    tail.Length = tail.MaximumLength = g_TargetProcessNameLenChars * sizeof(WCHAR);

    UNICODE_STRING target;
    RtlInitUnicodeString(&target, g_TargetProcessName);

    return RtlEqualUnicodeString(&tail, &target, TRUE);
}

static VOID NTAPI
NetSplitClassify(
    _In_ const FWPS_INCOMING_VALUES0* InFixedValues,
    _In_ const FWPS_INCOMING_METADATA_VALUES0* InMetaValues,
    _Inout_opt_ VOID* LayerData,
    _In_opt_ const VOID* ClassifyContext,
    _In_ const FWPS_FILTER1* Filter,
    _In_ UINT64 FlowContext,
    _Inout_ FWPS_CLASSIFY_OUT0* ClassifyOut
)
{
    UNREFERENCED_PARAMETER(InMetaValues);
    UNREFERENCED_PARAMETER(LayerData);
    UNREFERENCED_PARAMETER(FlowContext);

    // Default: leave everything untouched.
    ClassifyOut->actionType = FWP_ACTION_PERMIT;

    if (!(ClassifyOut->rights & FWPS_RIGHT_ACTION_WRITE))
    {
        return;
    }

    if (g_TargetAddressV4 == 0)
    {
        return;
    }

    if (InFixedValues->incomingValue[FWPS_FIELD_ALE_BIND_REDIRECT_V4_IP_PROTOCOL].value.uint8 != IPPROTO_TCP)
    {
        return;
    }

    FWP_BYTE_BLOB* appId =
        InFixedValues->incomingValue[FWPS_FIELD_ALE_BIND_REDIRECT_V4_ALE_APP_ID].value.byteBlob;

    if (appId == nullptr || appId->data == nullptr)
    {
        return;
    }

    if (!NetSplitPathMatchesTarget((PWCH)appId->data, (USHORT)appId->size))
    {
        return;
    }

    UINT64 classifyHandle = 0;
    if (!NT_SUCCESS(FwpsAcquireClassifyHandle0((PVOID)ClassifyContext, 0, &classifyHandle)))
    {
        return;
    }

    PVOID writableLayerData = nullptr;
    NTSTATUS status = FwpsAcquireWritableLayerDataPointer0(
        classifyHandle,
        Filter->filterId,
        0,
        &writableLayerData,
        ClassifyOut);

    if (NT_SUCCESS(status) && writableLayerData != nullptr)
    {
        FWPS_BIND_REQUEST0* bindRequest = (FWPS_BIND_REQUEST0*)writableLayerData;
        PSOCKADDR_IN localAddr = (PSOCKADDR_IN)&bindRequest->localAddressAndPort;

        if (localAddr->sin_family == AF_INET)
        {
            localAddr->sin_addr.S_un.S_addr = g_TargetAddressV4;
        }

        FwpsApplyModifiedLayerData0(classifyHandle, writableLayerData, 0);
    }

    FwpsReleaseClassifyHandle0(classifyHandle);
}

static NTSTATUS NTAPI
NetSplitNotify(
    _In_ FWPS_CALLOUT_NOTIFY_TYPE NotifyType,
    _In_ const GUID* FilterKey,
    _Inout_ FWPS_FILTER1* Filter
)
{
    UNREFERENCED_PARAMETER(NotifyType);
    UNREFERENCED_PARAMETER(FilterKey);
    UNREFERENCED_PARAMETER(Filter);
    return STATUS_SUCCESS;
}

NTSTATUS
NetSplitRegisterCallouts(
    _In_ PDEVICE_OBJECT DeviceObject
)
{
    NTSTATUS status;

    // Dynamic session: everything we add through this engine handle
    // (sublayer, callout, filter) is automatically torn down when the
    // handle is closed in NetSplitUnregisterCallouts. No manual cleanup.
    FWPM_SESSION0 session = {};
    session.flags = FWPM_SESSION_FLAG_DYNAMIC;

    status = FwpmEngineOpen0(
        nullptr,
        RPC_C_AUTHN_DEFAULT,
        nullptr,
        &session,
        &g_EngineHandle);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    FWPS_CALLOUT1 sCallout = {};
    sCallout.calloutKey = NETSPLIT_CALLOUT_GUID;
    sCallout.classifyFn = NetSplitClassify;
    sCallout.notifyFn = NetSplitNotify;
    sCallout.flowDeleteFn = nullptr;

    UINT32 calloutId = 0;
    status = FwpsCalloutRegister1(DeviceObject, &sCallout, &calloutId);
    if (!NT_SUCCESS(status))
    {
        FwpmEngineClose0(g_EngineHandle);
        g_EngineHandle = nullptr;
        return status;
    }
    g_CalloutRegistered = TRUE;

    status = FwpmTransactionBegin0(g_EngineHandle, 0);
    if (!NT_SUCCESS(status))
    {
        NetSplitUnregisterCallouts();
        return status;
    }

    FWPM_SUBLAYER0 subLayer = {};
    subLayer.subLayerKey = NETSPLIT_SUBLAYER_GUID;
    subLayer.displayData.name = const_cast<wchar_t*>(L"NetSplit Sublayer");
    subLayer.displayData.description = const_cast<wchar_t*>(L"NetSplit POC sublayer");
    subLayer.weight = 0x100;

    status = FwpmSubLayerAdd0(g_EngineHandle, &subLayer, nullptr);
    if (!NT_SUCCESS(status))
    {
        FwpmTransactionAbort0(g_EngineHandle);
        NetSplitUnregisterCallouts();
        return status;
    }

    FWPM_CALLOUT0 mCallout = {};
    mCallout.calloutKey = NETSPLIT_CALLOUT_GUID;
    mCallout.displayData.name = const_cast<wchar_t*>(L"NetSplit Callout");
    mCallout.displayData.description = const_cast<wchar_t*>(L"NetSplit POC bind-redirect callout");
    mCallout.applicableLayer = FWPM_LAYER_ALE_BIND_REDIRECT_V4;

    status = FwpmCalloutAdd0(g_EngineHandle, &mCallout, nullptr, nullptr);
    if (!NT_SUCCESS(status))
    {
        FwpmTransactionAbort0(g_EngineHandle);
        NetSplitUnregisterCallouts();
        return status;
    }

    FWPM_FILTER0 filter = {};
    filter.subLayerKey = NETSPLIT_SUBLAYER_GUID;
    filter.layerKey = FWPM_LAYER_ALE_BIND_REDIRECT_V4;
    filter.displayData.name = const_cast<wchar_t*>(L"NetSplit Filter");
    filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
    filter.action.calloutKey = NETSPLIT_CALLOUT_GUID;
    filter.weight.type = FWP_EMPTY;
    filter.numFilterConditions = 0;
    filter.filterCondition = nullptr;

    UINT64 filterId = 0;
    status = FwpmFilterAdd0(g_EngineHandle, &filter, nullptr, &filterId);
    if (!NT_SUCCESS(status))
    {
        FwpmTransactionAbort0(g_EngineHandle);
        NetSplitUnregisterCallouts();
        return status;
    }

    status = FwpmTransactionCommit0(g_EngineHandle);
    if (!NT_SUCCESS(status))
    {
        NetSplitUnregisterCallouts();
        return status;
    }

    return STATUS_SUCCESS;
}

VOID
NetSplitUnregisterCallouts()
{
    // Closing the dynamic-session engine handle removes our filter,
    // callout (management object) and sublayer automatically.
    if (g_EngineHandle != nullptr)
    {
        FwpmEngineClose0(g_EngineHandle);
        g_EngineHandle = nullptr;
    }

    if (g_CalloutRegistered)
    {
        FwpsCalloutUnregisterByKey0(&NETSPLIT_CALLOUT_GUID);
        g_CalloutRegistered = FALSE;
    }
}
