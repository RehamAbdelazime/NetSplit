#define NDIS630
#include <ndis.h>

#define INITGUID
#include <initguid.h>
#include <fwpsk.h>
#include <fwpmk.h>

#include "NetSplitCallout.h"

// Fixed, hardcoded identifiers for our sublayer and callout - proof driver,
// single instance only. Deliberately different GUIDs from NetSplit.driver
// (the production driver) so the two can never collide if both happen to be
// registered on the same machine.
DEFINE_GUID(NETSPLIT_PROOF_SUBLAYER_GUID,
    0x9a1b2c3d, 0x4e5f, 0x4a1b, 0x8c, 0x2d, 0x1e, 0x2f, 0x3a, 0x4b, 0x5c, 0x6e);

DEFINE_GUID(NETSPLIT_PROOF_CALLOUT_GUID,
    0x8b2c3d4e, 0x5f6a, 0x4b1c, 0x9d, 0x3e, 0x2f, 0x3a, 0x4b, 0x5c, 0x6d, 0x7f);

// The one hardcoded rule: process name -> target local IPv4 address. Set
// once from DriverEntry (NetSplitSetTarget) before the callout is
// registered - there is no runtime rule engine, no PID lookup, no IOCTL
// that can change this after the driver loads.
static WCHAR g_TargetProcessName[64] = L"";
static USHORT g_TargetProcessNameLenChars = 0;
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
// "\<g_TargetProcessName>" (or equal it)? Good enough to recognize the
// hardcoded target regardless of install directory.
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

    // Default: leave everything untouched - behave exactly like Windows
    // would with no driver present, for every process except the one
    // hardcoded target.
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

        DbgPrintEx(
            DPFLTR_IHVNETWORK_ID,
            DPFLTR_INFO_LEVEL,
            "NetSplitProof: rebound to %u.%u.%u.%u\n",
            ((UCHAR*)&g_TargetAddressV4)[0], ((UCHAR*)&g_TargetAddressV4)[1],
            ((UCHAR*)&g_TargetAddressV4)[2], ((UCHAR*)&g_TargetAddressV4)[3]);
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
    sCallout.calloutKey = NETSPLIT_PROOF_CALLOUT_GUID;
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
    subLayer.subLayerKey = NETSPLIT_PROOF_SUBLAYER_GUID;
    subLayer.displayData.name = const_cast<wchar_t*>(L"NetSplitProof Sublayer");
    subLayer.displayData.description = const_cast<wchar_t*>(L"NetSplitProof isolated bind-redirect proof sublayer");
    subLayer.weight = 0x100;

    status = FwpmSubLayerAdd0(g_EngineHandle, &subLayer, nullptr);
    if (!NT_SUCCESS(status))
    {
        FwpmTransactionAbort0(g_EngineHandle);
        NetSplitUnregisterCallouts();
        return status;
    }

    FWPM_CALLOUT0 mCallout = {};
    mCallout.calloutKey = NETSPLIT_PROOF_CALLOUT_GUID;
    mCallout.displayData.name = const_cast<wchar_t*>(L"NetSplitProof Callout");
    mCallout.displayData.description = const_cast<wchar_t*>(L"NetSplitProof isolated bind-redirect proof callout");
    mCallout.applicableLayer = FWPM_LAYER_ALE_BIND_REDIRECT_V4;

    status = FwpmCalloutAdd0(g_EngineHandle, &mCallout, nullptr, nullptr);
    if (!NT_SUCCESS(status))
    {
        FwpmTransactionAbort0(g_EngineHandle);
        NetSplitUnregisterCallouts();
        return status;
    }

    FWPM_FILTER0 filter = {};
    filter.subLayerKey = NETSPLIT_PROOF_SUBLAYER_GUID;
    filter.layerKey = FWPM_LAYER_ALE_BIND_REDIRECT_V4;
    filter.displayData.name = const_cast<wchar_t*>(L"NetSplitProof Filter");
    filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
    filter.action.calloutKey = NETSPLIT_PROOF_CALLOUT_GUID;
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
        FwpsCalloutUnregisterByKey0(&NETSPLIT_PROOF_CALLOUT_GUID);
        g_CalloutRegistered = FALSE;
    }
}
