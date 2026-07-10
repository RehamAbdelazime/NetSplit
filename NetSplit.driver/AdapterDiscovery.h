#pragma once

#include <ntddk.h>

// Discovers this machine's active Wi-Fi and Ethernet adapters at driver
// load time and returns their current IPv4 addresses. No hardcoded adapter
// names, no hardcoded subnet assumptions - queried live via the IP Helper
// API (GetIfTable2 + GetUnicastIpAddressTable). Resolved once, at load
// time, for this sprint's hardcoded two-entry proof; NOT re-queried per
// connection (that dynamic-per-connection behavior belongs to a later
// sprint, not this one).
//
// Returns FALSE if either adapter cannot be found (not present, not
// operationally up, or has no IPv4 address). Callers must not register the
// filter in that case.
BOOLEAN
NetSplitAdapterDiscovery_FindWiFiAndEthernet(
    _Out_writes_bytes_(4) PUCHAR WiFiAddressBytes,
    _Out_writes_bytes_(4) PUCHAR EthernetAddressBytes
);
