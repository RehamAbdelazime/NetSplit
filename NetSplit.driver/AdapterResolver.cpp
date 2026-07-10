#include "AdapterResolver.h"

#include <netioapi.h>

#pragma comment(lib, "netio.lib")

BOOLEAN
NetSplitAdapterResolver_GetCurrentIPv4(
    _In_ LONG InterfaceIndex,
    _Out_writes_bytes_(4) PUCHAR AddressBytesOut
)
{
    // GetUnicastIpAddressTable allocates pool and can block - only legal at
    // PASSIVE_LEVEL. Defensive check since classify may in principle be
    // reached at a higher IRQL for other layers/configurations.
    if (KeGetCurrentIrql() > PASSIVE_LEVEL)
    {
        return FALSE;
    }

    PMIB_UNICASTIPADDRESS_TABLE table = nullptr;
    NETIO_STATUS status = GetUnicastIpAddressTable(AF_INET, &table);
    if (status != 0 || table == nullptr) // NETIO_STATUS success == 0 (ERROR_SUCCESS); NO_ERROR isn't available in kernel headers
    {
        return FALSE;
    }

    BOOLEAN found = FALSE;
    for (ULONG i = 0; i < table->NumEntries; i++)
    {
        MIB_UNICASTIPADDRESS_ROW* row = &table->Table[i];
        if ((LONG)row->InterfaceIndex == InterfaceIndex && row->Address.si_family == AF_INET)
        {
            RtlCopyMemory(AddressBytesOut, &row->Address.Ipv4.sin_addr, 4);
            found = TRUE;
            break;
        }
    }

    FreeMibTable(table);
    return found;
}
