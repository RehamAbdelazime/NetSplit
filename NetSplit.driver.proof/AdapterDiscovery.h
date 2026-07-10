#pragma once

#include <ntddk.h>

// ISOLATED PROOF DRIVER ONLY - discovers this machine's active Wi-Fi and
// Ethernet adapters at driver load time and returns their current IPv4
// addresses. This exists here, and ONLY here, so the proof driver needs no
// manual "look up the adapter's IP first" step. NetSplit.driver (the frozen
// production driver) does not do this and must not start doing this - all
// adapter discovery for production stays in NetSplit.Network/user mode.
//
// Resolved once, at load time, for this one-shot proof; NOT re-queried per
// connection (that dynamic-per-connection behavior belongs to the
// production Service/Driver.Interop pipeline, not this throwaway driver).
//
// Returns FALSE if either adapter cannot be found (not present, not
// operationally up, or has no IPv4 address). Callers must not register the
// filter in that case.
BOOLEAN
NetSplitAdapterDiscovery_FindWiFiAndEthernet(
    _Out_writes_bytes_(4) PUCHAR WiFiAddressBytes,
    _Out_writes_bytes_(4) PUCHAR EthernetAddressBytes
);
