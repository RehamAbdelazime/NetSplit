#include <ntddk.h>

#define NDIS630
#include <ndis.h>

#define INITGUID
#include <initguid.h>
#include <fwpsk.h>
#include <fwpmk.h>

#include <wdmsec.h>

#include "Public.h"
#include "RuleEngine.h"

#pragma comment(lib, "Fwpkclnt.lib")
#pragma comment(lib, "wdmsec.lib")

DEFINE_GUID(NETSPLIT_PROVIDER_GUID,
    0x1c2b3a4d, 0x5e6f, 0x4a1b, 0x8c, 0x2d, 0x3e, 0x4f, 0x5a, 0x6b, 0x7c, 0x8d);

DEFINE_GUID(NETSPLIT_SUBLAYER_GUID,
    0x2f1a9b3c, 0x4d5e, 0x4a6b, 0x8c, 0x1d, 0x3e, 0x2f, 0x4a, 0x5b, 0x6c, 0x7d);

DEFINE_GUID(NETSPLIT_CALLOUT_GUID,
    0x7b6a5d4c, 0x3e2f, 0x4a1b, 0x9c, 0x8d, 0x1e, 0x2f, 0x3a, 0x4b, 0x5c, 0x6d);

HANDLE gEngineHandle = nullptr;
static BOOLEAN g_CalloutRegistered = FALSE;
static PDEVICE_OBJECT g_DeviceObject = nullptr;

// Statistics: monotonic counters for IOCTL_NETSPLIT_GET_STATISTICS.
// Incremented via Interlocked* since classify runs concurrently across
// processors; read via plain reads in the IOCTL handler (a torn read of a
// 64-bit counter on x64 is not a real concern here - these are advisory
// diagnostics, not something correctness depends on).
static volatile LONG64 g_ClassifyCount;
static volatile LONG64 g_RewriteSuccessCount;
static volatile LONG64 g_RewriteFailureCount;
static volatile LONG64 g_IoctlFailureCount;
static volatile LONG64 g_MatchedPidCount;
static volatile LONG64 g_UnmatchedPidCount;
static volatile LONG64 g_RewriteAttempts;

// Last-matched/last-rewritten are single-slot "most recent" diagnostics, not
// aggregates - InterlockedExchange makes each individual write atomic, and
// a reader racing a writer sees either the old or the new value, never a
// torn one. That's all IOCTL_NETSPLIT_GET_DIAGNOSTICS needs: an advisory
// snapshot, not a value anything correctness-sensitive depends on.
static volatile LONG g_LastMatchedPid = -1;
static volatile LONG g_LastRewrittenAddress; // raw IPv4 bytes reinterpreted as one 32-bit value

// ---------------------------------------------------------------------
// Classify: PID -> hash lookup -> rewrite. No executable names, no string
// comparisons, no adapter discovery anywhere in this function or anything
// it calls. The only thing identifying the process is the PID the engine
// itself hands us in metadata - nothing here inspects the process at all.
// ---------------------------------------------------------------------
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
    UNREFERENCED_PARAMETER(InFixedValues);
    UNREFERENCED_PARAMETER(LayerData);
    UNREFERENCED_PARAMETER(FlowContext);

    InterlockedIncrement64(&g_ClassifyCount);

    // Default, and the outcome for anything not covered below: behave
    // exactly like Windows would with no driver present.
    ClassifyOut->actionType = FWP_ACTION_PERMIT;

    if (!FWPS_IS_METADATA_FIELD_PRESENT(InMetaValues, FWPS_METADATA_FIELD_PROCESS_ID))
    {
        return; // can't identify the process - permit unchanged
    }

    INT32 pid = (INT32)InMetaValues->processId;

    UCHAR targetAddressBytes[4];
    if (!NetSplitRuleEngine_FindRule(pid, targetAddressBytes))
    {
        InterlockedIncrement64(&g_UnmatchedPidCount);
        return; // no runtime rule for this PID - permit unchanged
    }

    InterlockedIncrement64(&g_MatchedPidCount);
    InterlockedExchange(&g_LastMatchedPid, pid);

    if (!(ClassifyOut->rights & FWPS_RIGHT_ACTION_WRITE))
    {
        InterlockedIncrement64(&g_RewriteFailureCount);
        return; // rule matched but can't modify - permit unchanged
    }

    InterlockedIncrement64(&g_RewriteAttempts);

    UINT64 classifyHandle = 0;
    if (!NT_SUCCESS(FwpsAcquireClassifyHandle0((PVOID)ClassifyContext, 0, &classifyHandle)))
    {
        InterlockedIncrement64(&g_RewriteFailureCount);
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
        UCHAR* addrBytes = (UCHAR*)&localAddr->sin_addr;

        // Only the local address bytes are touched - port, family, and
        // everything else in the bind request is left as-is.
        RtlCopyMemory(addrBytes, targetAddressBytes, 4);

        FwpsApplyModifiedLayerData0(classifyHandle, writableLayerData, 0);

        InterlockedIncrement64(&g_RewriteSuccessCount);

        LONG rewrittenAddress;
        RtlCopyMemory(&rewrittenAddress, targetAddressBytes, 4);
        InterlockedExchange(&g_LastRewrittenAddress, rewrittenAddress);

        DbgPrintEx(
            DPFLTR_IHVNETWORK_ID,
            DPFLTR_INFO_LEVEL,
            "NetSplit: PID %d rebound to %u.%u.%u.%u\n",
            pid,
            targetAddressBytes[0], targetAddressBytes[1], targetAddressBytes[2], targetAddressBytes[3]);
    }
    else
    {
        InterlockedIncrement64(&g_RewriteFailureCount);
    }

    FwpsReleaseClassifyHandle0(classifyHandle);

    ClassifyOut->actionType = FWP_ACTION_PERMIT;
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

// ---------------------------------------------------------------------
// IOCTL handling: AddRuntimeRule / RemoveRuntimeRule / ClearRuntimeRules.
// ---------------------------------------------------------------------

static NTSTATUS
NetSplitCreateClose(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP Irp
)
{
    UNREFERENCED_PARAMETER(DeviceObject);

    Irp->IoStatus.Status = STATUS_SUCCESS;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

static NTSTATUS
NetSplitDeviceControl(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP Irp
)
{
    UNREFERENCED_PARAMETER(DeviceObject);

    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    ULONG ioctl = stack->Parameters.DeviceIoControl.IoControlCode;
    ULONG inLen = stack->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outLen = stack->Parameters.DeviceIoControl.OutputBufferLength;
    PVOID buffer = Irp->AssociatedIrp.SystemBuffer; // METHOD_BUFFERED: already a kernel-owned copy

    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR information = 0;

    switch (ioctl)
    {
        case IOCTL_NETSPLIT_ADD_RUNTIME_RULE:
        {
            if (inLen < sizeof(NETSPLIT_RUNTIME_RULE))
            {
                status = STATUS_BUFFER_TOO_SMALL;
                break;
            }

            PNETSPLIT_RUNTIME_RULE request = (PNETSPLIT_RUNTIME_RULE)buffer;

            if (request->Pid <= 0)
            {
                status = STATUS_INVALID_PARAMETER;
                break;
            }

            // This pass only enforces IPv4. A non-IPv4 rule is rejected
            // outright rather than silently stored and never acted on -
            // wrong data should fail loudly, not disappear quietly.
            if (request->AddressFamily != AF_INET)
            {
                status = STATUS_NOT_SUPPORTED;
                break;
            }

            if (!request->Enabled)
            {
                // An explicitly-disabled rule has the same effect as no
                // rule at all - nothing to store.
                NetSplitRuleEngine_RemoveRule(request->Pid);
                status = STATUS_SUCCESS;
                break;
            }

            status = NetSplitRuleEngine_AddRule(request->Pid, request->TargetAddress);
            break;
        }

        case IOCTL_NETSPLIT_REMOVE_RUNTIME_RULE:
        {
            if (inLen < sizeof(NETSPLIT_REMOVE_RUNTIME_RULE_REQUEST))
            {
                status = STATUS_BUFFER_TOO_SMALL;
                break;
            }

            PNETSPLIT_REMOVE_RUNTIME_RULE_REQUEST request = (PNETSPLIT_REMOVE_RUNTIME_RULE_REQUEST)buffer;

            if (request->Pid <= 0)
            {
                status = STATUS_INVALID_PARAMETER;
                break;
            }

            status = NetSplitRuleEngine_RemoveRule(request->Pid)
                ? STATUS_SUCCESS
                : STATUS_NOT_FOUND;
            break;
        }

        case IOCTL_NETSPLIT_CLEAR_RUNTIME_RULES:
        {
            NetSplitRuleEngine_Clear();
            status = STATUS_SUCCESS;
            break;
        }

        case IOCTL_NETSPLIT_GET_VERSION:
        {
            // TEMPORARY INSTRUMENTATION - remove after handshake diagnosis
            DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_INFO_LEVEL,
                "NetSplit[TRACE]: IOCTL_NETSPLIT_GET_VERSION received. ioctl=0x%08X outLen=%lu sizeof(NETSPLIT_VERSION_INFO)=%Iu\n",
                ioctl, outLen, sizeof(NETSPLIT_VERSION_INFO));

            if (outLen < sizeof(NETSPLIT_VERSION_INFO))
            {
                DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL,
                    "NetSplit[TRACE]: outLen %lu < sizeof(NETSPLIT_VERSION_INFO) %Iu -> STATUS_BUFFER_TOO_SMALL\n",
                    outLen, sizeof(NETSPLIT_VERSION_INFO));
                status = STATUS_BUFFER_TOO_SMALL;
                break;
            }

            PNETSPLIT_VERSION_INFO response = (PNETSPLIT_VERSION_INFO)buffer;
            response->ProtocolVersion = NETSPLIT_PROTOCOL_VERSION;
            response->Capabilities = NETSPLIT_CAPABILITY_IPV4_REDIRECT;
            information = sizeof(NETSPLIT_VERSION_INFO);
            status = STATUS_SUCCESS;

            // TEMPORARY INSTRUMENTATION - remove after handshake diagnosis.
            // Build marker repeated here (DPFLTR_ERROR_LEVEL, always visible)
            // so this specific response is directly, unmistakably tied to a
            // build - not just "some driver logged a banner at load time".
            DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL,
                "NetSplit[TRACE]: === BUILD MARKER === this GET_VERSION response was produced by build compiled %s %s\n",
                __DATE__, __TIME__);
            DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_INFO_LEVEL,
                "NetSplit[TRACE]: IOCTL_NETSPLIT_GET_VERSION responding. ProtocolVersion=%lu Capabilities=0x%08X sizeof(NETSPLIT_VERSION_INFO)=%Iu\n",
                response->ProtocolVersion, response->Capabilities, sizeof(NETSPLIT_VERSION_INFO));
            break;
        }

        case IOCTL_NETSPLIT_GET_STATISTICS:
        {
            if (outLen < sizeof(NETSPLIT_STATISTICS))
            {
                status = STATUS_BUFFER_TOO_SMALL;
                break;
            }

            PNETSPLIT_STATISTICS response = (PNETSPLIT_STATISTICS)buffer;
            response->ClassifyCount = (UINT64)g_ClassifyCount;
            response->RewriteSuccessCount = (UINT64)g_RewriteSuccessCount;
            response->RewriteFailureCount = (UINT64)g_RewriteFailureCount;
            response->IoctlFailureCount = (UINT64)g_IoctlFailureCount;
            response->ActiveRuleCount = NetSplitRuleEngine_GetCount();
            information = sizeof(NETSPLIT_STATISTICS);
            status = STATUS_SUCCESS;
            break;
        }

        case IOCTL_NETSPLIT_GET_DIAGNOSTICS:
        {
            if (outLen < sizeof(NETSPLIT_DIAGNOSTICS))
            {
                status = STATUS_BUFFER_TOO_SMALL;
                break;
            }

            PNETSPLIT_DIAGNOSTICS response = (PNETSPLIT_DIAGNOSTICS)buffer;
            response->ProtocolVersion = NETSPLIT_PROTOCOL_VERSION;
            response->Capabilities = NETSPLIT_CAPABILITY_IPV4_REDIRECT;
            response->ClassifyCount = (UINT64)g_ClassifyCount;
            response->MatchedPidCount = (UINT64)g_MatchedPidCount;
            response->UnmatchedPidCount = (UINT64)g_UnmatchedPidCount;
            response->RewriteAttempts = (UINT64)g_RewriteAttempts;
            response->RewriteSuccessCount = (UINT64)g_RewriteSuccessCount;
            response->RewriteFailureCount = (UINT64)g_RewriteFailureCount;
            response->IoctlFailureCount = (UINT64)g_IoctlFailureCount;
            response->ActiveRuleCount = NetSplitRuleEngine_GetCount();
            response->LastMatchedPid = (INT32)g_LastMatchedPid;
            LONG lastAddress = g_LastRewrittenAddress;
            RtlCopyMemory(response->LastRewrittenAddress, &lastAddress, 4);
            information = sizeof(NETSPLIT_DIAGNOSTICS);
            status = STATUS_SUCCESS;
            break;
        }

        default:
            status = STATUS_INVALID_DEVICE_REQUEST;
            break;
    }

    if (!NT_SUCCESS(status) && status != STATUS_NOT_FOUND)
    {
        // STATUS_NOT_FOUND (RemoveRuntimeRule on an unknown PID) is a normal
        // outcome, not a protocol/input failure - everything else here means
        // the caller sent something wrong or the driver couldn't comply.
        InterlockedIncrement64(&g_IoctlFailureCount);
    }

    // TEMPORARY INSTRUMENTATION - remove after handshake diagnosis
    DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_INFO_LEVEL,
        "NetSplit[TRACE]: completing ioctl=0x%08X Status=0x%08X Information=%Iu\n",
        ioctl, status, information);

    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = information;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}

// ---------------------------------------------------------------------
// Registration / teardown.
// ---------------------------------------------------------------------

static VOID
NetSplitUnregister()
{
    if (gEngineHandle != nullptr)
    {
        FwpmEngineClose0(gEngineHandle);
        gEngineHandle = nullptr;
    }

    if (g_CalloutRegistered)
    {
        FwpsCalloutUnregisterByKey0(&NETSPLIT_CALLOUT_GUID);
        g_CalloutRegistered = FALSE;
    }

    if (g_DeviceObject != nullptr)
    {
        UNICODE_STRING symLink;
        RtlInitUnicodeString(&symLink, NETSPLIT_SYMLINK_NAME);
        IoDeleteSymbolicLink(&symLink);

        IoDeleteDevice(g_DeviceObject);
        g_DeviceObject = nullptr;
    }

    NetSplitRuleEngine_Uninitialize();
}

VOID DriverUnload(
    _In_ PDRIVER_OBJECT DriverObject
)
{
    UNREFERENCED_PARAMETER(DriverObject);

    NetSplitUnregister();

    DbgPrintEx(
        DPFLTR_IHVNETWORK_ID,
        DPFLTR_INFO_LEVEL,
        "NetSplit: unloaded\n");
}

extern "C"
NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    UNREFERENCED_PARAMETER(RegistryPath);

    // TEMPORARY INSTRUMENTATION - remove after handshake diagnosis.
    // __DATE__/__TIME__ are preprocessor macros expanded by the compiler at
    // compile time into string literals baked into this exact binary - they
    // are NOT read at runtime, so this line is unmistakable, positive proof
    // of which build is executing DriverEntry right now. Printed at
    // DPFLTR_ERROR_LEVEL (not INFO) specifically so it is visible in
    // DebugView even without "Enable Verbose Kernel Output" turned on -
    // this one line alone should never be silently filtered.
    DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL,
        "NetSplit[TRACE]: === BUILD MARKER === compiled %s %s - if you do not see this exact string in DebugView, the loaded driver is NOT this build.\n",
        __DATE__, __TIME__);

    DriverObject->DriverUnload = DriverUnload;

    NetSplitRuleEngine_Initialize();

    FWPM_SESSION0 session = {};
    session.flags = FWPM_SESSION_FLAG_DYNAMIC;

    NTSTATUS status = FwpmEngineOpen0(
        nullptr,
        RPC_C_AUTHN_DEFAULT,
        nullptr,
        &session,
        &gEngineHandle);

    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: FwpmEngineOpen0 failed 0x%08X\n", status);
        NetSplitRuleEngine_Uninitialize();
        return status;
    }

    // Named and access-restricted: this device carries the production
    // runtime-rule IOCTLs, so an elevated NetSplit.App must be able to open
    // it - but only SYSTEM/Administrators.
    UNICODE_STRING deviceName;
    RtlInitUnicodeString(&deviceName, NETSPLIT_DEVICE_NAME);

    status = IoCreateDeviceSecure(
        DriverObject,
        0,
        &deviceName,
        FILE_DEVICE_NETWORK,
        FILE_DEVICE_SECURE_OPEN,
        FALSE,
        &SDDL_DEVOBJ_SYS_ALL_ADM_ALL,
        nullptr,
        &g_DeviceObject);

    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: IoCreateDeviceSecure failed 0x%08X\n", status);
        FwpmEngineClose0(gEngineHandle);
        gEngineHandle = nullptr;
        NetSplitRuleEngine_Uninitialize();
        return status;
    }

    UNICODE_STRING symLink;
    RtlInitUnicodeString(&symLink, NETSPLIT_SYMLINK_NAME);
    status = IoCreateSymbolicLink(&symLink, &deviceName);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: IoCreateSymbolicLink failed 0x%08X\n", status);
        IoDeleteDevice(g_DeviceObject);
        g_DeviceObject = nullptr;
        FwpmEngineClose0(gEngineHandle);
        gEngineHandle = nullptr;
        NetSplitRuleEngine_Uninitialize();
        return status;
    }

    DriverObject->MajorFunction[IRP_MJ_CREATE] = NetSplitCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLOSE] = NetSplitCreateClose;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = NetSplitDeviceControl;

    FWPS_CALLOUT1 sCallout = {};
    sCallout.calloutKey = NETSPLIT_CALLOUT_GUID;
    sCallout.classifyFn = NetSplitClassify;
    sCallout.notifyFn = NetSplitNotify;
    sCallout.flowDeleteFn = nullptr;

    UINT32 calloutId = 0;
    status = FwpsCalloutRegister1(g_DeviceObject, &sCallout, &calloutId);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: FwpsCalloutRegister1 failed 0x%08X\n", status);
        NetSplitUnregister();
        return status;
    }
    g_CalloutRegistered = TRUE;

    status = FwpmTransactionBegin0(gEngineHandle, 0);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: FwpmTransactionBegin0 failed 0x%08X\n", status);
        NetSplitUnregister();
        return status;
    }

    FWPM_PROVIDER0 provider = {};
    provider.providerKey = NETSPLIT_PROVIDER_GUID;
    provider.displayData.name = const_cast<wchar_t*>(L"NetSplit Provider");
    provider.displayData.description = const_cast<wchar_t*>(L"NetSplit provider");

    status = FwpmProviderAdd0(gEngineHandle, &provider, nullptr);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: FwpmProviderAdd0 failed 0x%08X\n", status);
        FwpmTransactionAbort0(gEngineHandle);
        NetSplitUnregister();
        return status;
    }

    FWPM_SUBLAYER0 subLayer = {};
    subLayer.subLayerKey = NETSPLIT_SUBLAYER_GUID;
    subLayer.displayData.name = const_cast<wchar_t*>(L"NetSplit Sublayer");
    subLayer.displayData.description = const_cast<wchar_t*>(L"NetSplit sublayer");
    subLayer.providerKey = const_cast<GUID*>(&NETSPLIT_PROVIDER_GUID);
    subLayer.weight = 0x100;

    status = FwpmSubLayerAdd0(gEngineHandle, &subLayer, nullptr);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: FwpmSubLayerAdd0 failed 0x%08X\n", status);
        FwpmTransactionAbort0(gEngineHandle);
        NetSplitUnregister();
        return status;
    }

    FWPM_CALLOUT0 mCallout = {};
    mCallout.calloutKey = NETSPLIT_CALLOUT_GUID;
    mCallout.displayData.name = const_cast<wchar_t*>(L"NetSplit Callout");
    mCallout.displayData.description = const_cast<wchar_t*>(L"NetSplit bind-redirect callout");
    mCallout.providerKey = const_cast<GUID*>(&NETSPLIT_PROVIDER_GUID);
    mCallout.applicableLayer = FWPM_LAYER_ALE_BIND_REDIRECT_V4;

    status = FwpmCalloutAdd0(gEngineHandle, &mCallout, nullptr, nullptr);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: FwpmCalloutAdd0 failed 0x%08X\n", status);
        FwpmTransactionAbort0(gEngineHandle);
        NetSplitUnregister();
        return status;
    }

    FWPM_FILTER0 filter = {};
    filter.providerKey = const_cast<GUID*>(&NETSPLIT_PROVIDER_GUID);
    filter.subLayerKey = NETSPLIT_SUBLAYER_GUID;
    filter.layerKey = FWPM_LAYER_ALE_BIND_REDIRECT_V4;
    filter.displayData.name = const_cast<wchar_t*>(L"NetSplit Filter");
    filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
    filter.action.calloutKey = NETSPLIT_CALLOUT_GUID;
    filter.weight.type = FWP_EMPTY;
    filter.numFilterConditions = 0;
    filter.filterCondition = nullptr;

    UINT64 filterId = 0;
    status = FwpmFilterAdd0(gEngineHandle, &filter, nullptr, &filterId);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: FwpmFilterAdd0 failed 0x%08X\n", status);
        FwpmTransactionAbort0(gEngineHandle);
        NetSplitUnregister();
        return status;
    }

    status = FwpmTransactionCommit0(gEngineHandle);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: FwpmTransactionCommit0 failed 0x%08X\n", status);
        NetSplitUnregister();
        return status;
    }

    DbgPrintEx(
        DPFLTR_IHVNETWORK_ID,
        DPFLTR_INFO_LEVEL,
        "NetSplit: loaded. Waiting for runtime rules via IOCTL - no rules active yet.\n");

    return STATUS_SUCCESS;
}
