#include "RuleEngine.h"

#define NETSPLIT_RULE_HASH_BUCKETS 64
#define NETSPLIT_RULE_POOL_TAG 'RlsN'

typedef struct _NETSPLIT_RULE_NODE
{
    LIST_ENTRY BucketLink;
    INT32 Pid;
    UCHAR TargetAddressV4[4];
} NETSPLIT_RULE_NODE, *PNETSPLIT_RULE_NODE;

static LIST_ENTRY g_Buckets[NETSPLIT_RULE_HASH_BUCKETS];

// EX_PUSH_LOCK: FindRule only ever needs shared (read) access, and classify
// runs concurrently on multiple processors - a reader/writer lock lets
// those proceed without serializing on each other. AddRule/RemoveRule/Clear
// take the exclusive side; they run rarely (IOCTL-driven, human/process-
// lifecycle timescale), so writer contention is a non-issue at this scale.
static EX_PUSH_LOCK g_RuleLock;

// Protected by g_RuleLock, same as the table itself - always mutated while
// already holding the exclusive side, so no separate interlocked op needed.
static ULONG g_RuleCount;

// PIDs are already well-distributed small integers handed out by the OS -
// no string hashing needed, just spread them across buckets.
static ULONG
NetSplitHashPid(
    _In_ INT32 Pid
)
{
    return (ULONG)Pid;
}

NTSTATUS
NetSplitRuleEngine_Initialize()
{
    ExInitializePushLock(&g_RuleLock);
    for (ULONG i = 0; i < NETSPLIT_RULE_HASH_BUCKETS; i++)
    {
        InitializeListHead(&g_Buckets[i]);
    }
    return STATUS_SUCCESS;
}

VOID
NetSplitRuleEngine_Uninitialize()
{
    NetSplitRuleEngine_Clear();
}

NTSTATUS
NetSplitRuleEngine_AddRule(
    _In_ INT32 Pid,
    _In_reads_bytes_(4) PUCHAR TargetAddressV4
)
{
    // The only allocation in the entire rule lifecycle happens here.
    PNETSPLIT_RULE_NODE node = (PNETSPLIT_RULE_NODE)ExAllocatePool2(
        POOL_FLAG_NON_PAGED,
        sizeof(NETSPLIT_RULE_NODE),
        NETSPLIT_RULE_POOL_TAG);

    if (node == nullptr)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    node->Pid = Pid;
    RtlCopyMemory(node->TargetAddressV4, TargetAddressV4, 4);

    ULONG bucketIndex = NetSplitHashPid(Pid) % NETSPLIT_RULE_HASH_BUCKETS;
    PLIST_ENTRY head = &g_Buckets[bucketIndex];

    ExAcquirePushLockExclusive(&g_RuleLock);

    // Replace-on-duplicate: a second AddRule for the same PID (e.g. the
    // target adapter changed) updates the target instead of creating a
    // second entry - so the count only grows for a genuinely new PID.
    BOOLEAN replacedExisting = FALSE;
    for (PLIST_ENTRY entry = head->Flink; entry != head; entry = entry->Flink)
    {
        PNETSPLIT_RULE_NODE existing = CONTAINING_RECORD(entry, NETSPLIT_RULE_NODE, BucketLink);
        if (existing->Pid == Pid)
        {
            RemoveEntryList(entry);
            ExFreePoolWithTag(existing, NETSPLIT_RULE_POOL_TAG);
            replacedExisting = TRUE;
            break;
        }
    }

    InsertHeadList(head, &node->BucketLink);

    if (!replacedExisting)
    {
        g_RuleCount++;
    }

    ExReleasePushLockExclusive(&g_RuleLock);

    return STATUS_SUCCESS;
}

BOOLEAN
NetSplitRuleEngine_RemoveRule(
    _In_ INT32 Pid
)
{
    ULONG bucketIndex = NetSplitHashPid(Pid) % NETSPLIT_RULE_HASH_BUCKETS;
    PLIST_ENTRY head = &g_Buckets[bucketIndex];

    BOOLEAN removed = FALSE;

    ExAcquirePushLockExclusive(&g_RuleLock);

    for (PLIST_ENTRY entry = head->Flink; entry != head; entry = entry->Flink)
    {
        PNETSPLIT_RULE_NODE existing = CONTAINING_RECORD(entry, NETSPLIT_RULE_NODE, BucketLink);
        if (existing->Pid == Pid)
        {
            RemoveEntryList(entry);
            ExFreePoolWithTag(existing, NETSPLIT_RULE_POOL_TAG);
            removed = TRUE;
            g_RuleCount--;
            break;
        }
    }

    ExReleasePushLockExclusive(&g_RuleLock);

    return removed;
}

VOID
NetSplitRuleEngine_Clear()
{
    ExAcquirePushLockExclusive(&g_RuleLock);

    for (ULONG i = 0; i < NETSPLIT_RULE_HASH_BUCKETS; i++)
    {
        PLIST_ENTRY head = &g_Buckets[i];
        while (!IsListEmpty(head))
        {
            PLIST_ENTRY entry = RemoveHeadList(head);
            PNETSPLIT_RULE_NODE node = CONTAINING_RECORD(entry, NETSPLIT_RULE_NODE, BucketLink);
            ExFreePoolWithTag(node, NETSPLIT_RULE_POOL_TAG);
        }
    }

    g_RuleCount = 0;

    ExReleasePushLockExclusive(&g_RuleLock);
}

BOOLEAN
NetSplitRuleEngine_FindRule(
    _In_ INT32 Pid,
    _Out_writes_bytes_(4) PUCHAR TargetAddressV4Out
)
{
    ULONG bucketIndex = NetSplitHashPid(Pid) % NETSPLIT_RULE_HASH_BUCKETS;
    PLIST_ENTRY head = &g_Buckets[bucketIndex];

    BOOLEAN found = FALSE;

    ExAcquirePushLockShared(&g_RuleLock);

    for (PLIST_ENTRY entry = head->Flink; entry != head; entry = entry->Flink)
    {
        PNETSPLIT_RULE_NODE node = CONTAINING_RECORD(entry, NETSPLIT_RULE_NODE, BucketLink);
        if (node->Pid == Pid)
        {
            RtlCopyMemory(TargetAddressV4Out, node->TargetAddressV4, 4);
            found = TRUE;
            break;
        }
    }

    ExReleasePushLockShared(&g_RuleLock);

    return found;
}

ULONG
NetSplitRuleEngine_GetCount()
{
    ULONG count;

    ExAcquirePushLockShared(&g_RuleLock);
    count = g_RuleCount;
    ExReleasePushLockShared(&g_RuleLock);

    return count;
}
