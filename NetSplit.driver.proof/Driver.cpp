#include <ntddk.h>

#define NDIS630
#include <ndis.h>

#define INITGUID
#include <initguid.h>
#include <fwpsk.h>
#include <fwpmk.h>

#include "NetSplitCallout.h"
#include "AdapterDiscovery.h"

// =======================================================================
// ISOLATED PROOF DRIVER - completely separate from NetSplit.driver (the
// frozen production driver). Answers exactly one engineering question:
//
//   Can ALE_BIND_REDIRECT_V4 redirect a process onto a different adapter
//   by rewriting the local bind address?
//
// Nothing else. Compile-time hardcoded target process name below - no
// IOCTL, no device control routine, no rule engine, no runtime rule sync,
// no Service, no user-mode communication of any kind. Adapter discovery
// happens once at driver load (AdapterDiscovery.cpp) purely so the target
// IPv4 doesn't have to be hand-typed before every test - production's
// adapter discovery stays entirely in NetSplit.Network/user mode and is
// untouched by this file existing.
//
// Once this question is answered, this entire project is meant to be
// discarded (uninstalled, deleted) - it is not part of, and must never be
// merged into, the production architecture (NetSplit.driver, NetSplit.
// Service, NetSplit.App, NetSplit.Driver.Interop, NetSplit.Ipc all remain
// exactly as they were).
// =======================================================================

// Compile-time proof configuration - edit and rebuild to point this at a
// different process. No runtime configuration exists by design.
#define NETSPLIT_PROOF_TARGET_PROCESS L"chrome.exe"

static PDEVICE_OBJECT g_DeviceObject = nullptr;

static NTSTATUS
NetSplitProofCreateClose(
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

VOID DriverUnload(
    _In_ PDRIVER_OBJECT DriverObject
)
{
    UNREFERENCED_PARAMETER(DriverObject);

    NetSplitUnregisterCallouts();

    if (g_DeviceObject != nullptr)
    {
        IoDeleteDevice(g_DeviceObject);
        g_DeviceObject = nullptr;
    }

    DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_INFO_LEVEL, "NetSplitProof: unloaded\n");
}

extern "C"
NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    UNREFERENCED_PARAMETER(RegistryPath);

    DriverObject->DriverUnload = DriverUnload;

    // Unnamed, unsecured device - FwpsCalloutRegister1 requires a device
    // object to associate the callout's lifetime with, but nothing here
    // ever needs to be opened from user mode. No symbolic link, no IOCTL
    // handler, no SDDL: there is no user-mode-reachable surface at all.
    NTSTATUS status = IoCreateDevice(
        DriverObject, 0, nullptr, FILE_DEVICE_NETWORK, 0, FALSE, &g_DeviceObject);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplitProof: IoCreateDevice failed 0x%08X\n", status);
        return status;
    }

    DriverObject->MajorFunction[IRP_MJ_CREATE] = NetSplitProofCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLOSE] = NetSplitProofCreateClose;

    UCHAR wifiAddress[4] = {};
    UCHAR ethernetAddress[4] = {};
    if (!NetSplitAdapterDiscovery_FindWiFiAndEthernet(wifiAddress, ethernetAddress))
    {
        DbgPrintEx(
            DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL,
            "NetSplitProof: could not resolve both a Wi-Fi and an Ethernet adapter IPv4 - proof cannot run.\n");
        IoDeleteDevice(g_DeviceObject);
        g_DeviceObject = nullptr;
        return STATUS_UNSUCCESSFUL;
    }

    UINT32 targetAddress;
    RtlCopyMemory(&targetAddress, wifiAddress, 4);
    NetSplitSetTarget(NETSPLIT_PROOF_TARGET_PROCESS, targetAddress);

    status = NetSplitRegisterCallouts(g_DeviceObject);
    if (!NT_SUCCESS(status))
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplitProof: NetSplitRegisterCallouts failed 0x%08X\n", status);
        IoDeleteDevice(g_DeviceObject);
        g_DeviceObject = nullptr;
        return status;
    }

    DbgPrintEx(
        DPFLTR_IHVNETWORK_ID, DPFLTR_INFO_LEVEL,
        "NetSplitProof: loaded. %ws will be rebound to %u.%u.%u.%u (Wi-Fi). Ethernet resolved as %u.%u.%u.%u (unused by this proof, discovered for parity with the two-adapter scenario).\n",
        NETSPLIT_PROOF_TARGET_PROCESS,
        wifiAddress[0], wifiAddress[1], wifiAddress[2], wifiAddress[3],
        ethernetAddress[0], ethernetAddress[1], ethernetAddress[2], ethernetAddress[3]);

    return STATUS_SUCCESS;
}
