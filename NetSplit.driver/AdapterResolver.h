#pragma once

#include <ntddk.h>

// Resolves the CURRENT IPv4 address of the adapter identified by
// InterfaceIndex, by querying live system state on every call - no caching,
// so DHCP renewals and adapter reconnects are picked up immediately by the
// next connection.
//
// Returns FALSE if the adapter has no IPv4 address right now (unplugged,
// still negotiating DHCP, or gone entirely). Callers must permit the
// connection unmodified in that case.
BOOLEAN
NetSplitAdapterResolver_GetCurrentIPv4(
    _In_ LONG InterfaceIndex,
    _Out_writes_bytes_(4) PUCHAR AddressBytesOut
);
