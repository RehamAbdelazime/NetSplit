#pragma once

#include <ntddk.h>

// Deliberately does NOT re-include ndis.h/fwpsk.h/fwpmk.h here: this header
// is only ever included by translation units (Driver.cpp, NetSplitCallout.cpp)
// that already include that stack themselves before including this file.
// Re-including it a second time within the same TU causes fwpmk.h's
// DEFINE_GUID-based layer identifiers to be parsed twice under INITGUID,
// which the compiler correctly rejects as redefinitions.

// ISOLATED PROOF DRIVER - see Driver.cpp for scope. This file answers
// exactly one engineering question: can rewriting
// FWPS_BIND_REQUEST0.localAddressAndPort at the ALE_BIND_REDIRECT_V4 layer
// force a process's outbound IPv4 traffic onto a different adapter?
// Registers the ALE_BIND_REDIRECT_V4 callout + filter. Called once from DriverEntry.
NTSTATUS
NetSplitRegisterCallouts(
    _In_ PDEVICE_OBJECT DeviceObject
);

// Unregisters everything. Called once from DriverUnload.
VOID
NetSplitUnregisterCallouts();

// Overwrites the single hardcoded rule slot (process name + target local
// IPv4). Called once from DriverEntry with a compile-time process name and
// the live-discovered adapter address - never called from an IOCTL or any
// other user-mode-reachable path in this proof driver.
VOID
NetSplitSetTarget(
    _In_ PCWSTR ProcessName,
    _In_ UINT32 LocalAddressV4
);
