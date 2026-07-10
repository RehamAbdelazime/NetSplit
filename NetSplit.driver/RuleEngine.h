#pragma once

#include <ntddk.h>

// PID -> IPv4 address, hash-bucketed. This is the entire kernel-side rule
// model: no executable names, no adapter identities, no discovery. All
// allocation happens in AddRule/RemoveRule/Clear; FindRule (called from
// classify) never allocates and never blocks.

NTSTATUS NetSplitRuleEngine_Initialize();
VOID NetSplitRuleEngine_Uninitialize();

// Replaces any existing rule for the same PID.
NTSTATUS
NetSplitRuleEngine_AddRule(
    _In_ INT32 Pid,
    _In_reads_bytes_(4) PUCHAR TargetAddressV4
);

// Returns TRUE if a rule existed and was removed.
BOOLEAN
NetSplitRuleEngine_RemoveRule(
    _In_ INT32 Pid
);

VOID NetSplitRuleEngine_Clear();

// No allocation, no blocking calls - safe to call from classify. Takes the
// hash table's shared (read) lock only.
BOOLEAN
NetSplitRuleEngine_FindRule(
    _In_ INT32 Pid,
    _Out_writes_bytes_(4) PUCHAR TargetAddressV4Out
);

// Current number of rules in the table - for IOCTL_NETSPLIT_GET_STATISTICS.
ULONG NetSplitRuleEngine_GetCount();
