#include "AdapterDiscovery.h"

#include <netioapi.h>

#pragma comment(lib, "netio.lib")

// IANA ifType values (RFC 2863 / IANAifType-MIB) - used as plain numeric
// literals rather than pulling in ipifcons.h, which isn't guaranteed to be
// on this WDK include path. These two values are stable, publicly
// registered constants, not something that changes per Windows version.
#define NETSPLIT_IF_TYPE_ETHERNET_CSMACD 6
#define NETSPLIT_IF_TYPE_IEEE80211       71

BOOLEAN
NetSplitAdapterDiscovery_FindWiFiAndEthernet(
    _Out_writes_bytes_(4) PUCHAR WiFiAddressBytes,
    _Out_writes_bytes_(4) PUCHAR EthernetAddressBytes
)
{
    RtlZeroMemory(WiFiAddressBytes, 4);
    RtlZeroMemory(EthernetAddressBytes, 4);

    // Both IP Helper calls below allocate and can block - PASSIVE_LEVEL
    // only. DriverEntry runs at PASSIVE_LEVEL, so this is fine there.
    if (KeGetCurrentIrql() > PASSIVE_LEVEL)
    {
        return FALSE;
    }

    // NETIO_STATUS/ULONG success == 0 (ERROR_SUCCESS); NO_ERROR isn't
    // available in kernel headers.
    PMIB_IF_TABLE2 ifTable = nullptr;
    if (GetIfTable2(&ifTable) != 0 || ifTable == nullptr)
    {
        return FALSE;
    }

    PMIB_UNICASTIPADDRESS_TABLE addrTable = nullptr;
    if (GetUnicastIpAddressTable(AF_INET, &addrTable) != 0 || addrTable == nullptr)
    {
        FreeMibTable(ifTable);
        return FALSE;
    }

    BOOLEAN foundWiFi = FALSE;
    BOOLEAN foundEthernet = FALSE;

    for (ULONG i = 0; i < ifTable->NumEntries; i++)
    {
        MIB_IF_ROW2* ifRow = &ifTable->Table[i];

        // Only adapters that are actually up and passing traffic right now
        // are candidates - a disabled/disconnected adapter is not "active".
        if (ifRow->OperStatus != IfOperStatusUp)
        {
            continue;
        }

        BOOLEAN isWiFiCandidate = (ifRow->Type == NETSPLIT_IF_TYPE_IEEE80211) && !foundWiFi;
        BOOLEAN isEthernetCandidate = (ifRow->Type == NETSPLIT_IF_TYPE_ETHERNET_CSMACD) && !foundEthernet;

        if (!isWiFiCandidate && !isEthernetCandidate)
        {
            continue;
        }

        // Cross-reference against the unicast IPv4 table to get this
        // interface's current address - MIB_IF_ROW2 itself carries no IP.
        for (ULONG j = 0; j < addrTable->NumEntries; j++)
        {
            MIB_UNICASTIPADDRESS_ROW* addrRow = &addrTable->Table[j];

            if (addrRow->InterfaceIndex != ifRow->InterfaceIndex || addrRow->Address.si_family != AF_INET)
            {
                continue;
            }

            UCHAR* bytes = (UCHAR*)&addrRow->Address.Ipv4.sin_addr;

            DbgPrintEx(
                DPFLTR_IHVNETWORK_ID,
                DPFLTR_INFO_LEVEL,
                "NetSplit: adapter '%ws' idx=%lu type=%lu up, IPv4=%u.%u.%u.%u\n",
                ifRow->Alias,
                ifRow->InterfaceIndex,
                ifRow->Type,
                bytes[0], bytes[1], bytes[2], bytes[3]);

            if (isWiFiCandidate)
            {
                RtlCopyMemory(WiFiAddressBytes, bytes, 4);
                foundWiFi = TRUE;
            }
            else
            {
                RtlCopyMemory(EthernetAddressBytes, bytes, 4);
                foundEthernet = TRUE;
            }

            break;
        }
    }

    FreeMibTable(addrTable);
    FreeMibTable(ifTable);

    if (!foundWiFi)
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: no active Wi-Fi adapter with an IPv4 address found\n");
    }

    if (!foundEthernet)
    {
        DbgPrintEx(DPFLTR_IHVNETWORK_ID, DPFLTR_ERROR_LEVEL, "NetSplit: no active Ethernet adapter with an IPv4 address found\n");
    }

    return foundWiFi && foundEthernet;
}
