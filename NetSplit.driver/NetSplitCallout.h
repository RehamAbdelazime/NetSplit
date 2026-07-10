#pragma once

#include <ntddk.h>

#define NDIS630

#include <ndis.h>
#include <fwpsk.h>
#include <fwpmk.h>

// Registers the ALE_BIND_REDIRECT_V4 callout + filter. Called once from DriverEntry.
NTSTATUS
NetSplitRegisterCallouts(
    _In_ PDEVICE_OBJECT DeviceObject
);

// Unregisters everything. Called once from DriverUnload.
VOID
NetSplitUnregisterCallouts();

// Overwrites the single hardcoded rule slot (process name + target local IPv4).
VOID
NetSplitSetTarget(
    _In_ PCWSTR ProcessName,
    _In_ UINT32 LocalAddressV4
);
