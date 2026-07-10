#pragma once

// Shared wire format between the driver and NetSplit.Driver.Interop.
// The driver owns ONLY runtime (PID-based) rules - never executable names,
// never adapter discovery. Everything here is deliberately minimal: a PID
// and the address to rewrite to, plus a small version/health/statistics
// surface so a caller never has to guess whether it's talking to a
// compatible driver build.
//
// PROTOCOL VERSIONING
// --------------------
// NETSPLIT_PROTOCOL_VERSION identifies the wire format of every struct in
// this file. A caller MUST call IOCTL_NETSPLIT_GET_VERSION first and
// compare ProtocolVersion before sending anything else. Any future,
// backward-incompatible change to an existing struct's layout bumps this
// number; new, purely-additive IOCTLs (new struct, new code, nothing
// existing changed) do NOT require a version bump - that's what
// Capabilities is for. A caller that only recognizes capability bit 0
// keeps working unmodified against a driver that later advertises bit 1,
// because it never looks at bits it doesn't understand.

#define NETSPLIT_PROTOCOL_VERSION 1

#define NETSPLIT_DEVICE_NAME   L"\\Device\\NetSplit"
#define NETSPLIT_SYMLINK_NAME  L"\\DosDevices\\NetSplit"
#define NETSPLIT_WIN32_PATH    L"\\\\.\\NetSplit"

#define IOCTL_NETSPLIT_ADD_RUNTIME_RULE \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x820, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_NETSPLIT_REMOVE_RUNTIME_RULE \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x821, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_NETSPLIT_CLEAR_RUNTIME_RULES \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x822, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_NETSPLIT_GET_VERSION \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x823, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_NETSPLIT_GET_STATISTICS \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x824, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_NETSPLIT_GET_DIAGNOSTICS \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x825, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Capability bits, independent of ProtocolVersion. A caller checks
// (Capabilities & NETSPLIT_CAPABILITY_X) rather than assuming anything
// about what a given version supports - lets capabilities be added without
// forcing every client to be rebuilt against a new protocol version.
#define NETSPLIT_CAPABILITY_IPV4_REDIRECT 0x00000001u
// Reserved for a future V6 bind-redirect callout - not implemented yet;
// the bit is defined now so the wire format for advertising it never
// changes shape later.
#define NETSPLIT_CAPABILITY_IPV6_REDIRECT 0x00000002u

// Output for IOCTL_NETSPLIT_GET_VERSION. No input required.
typedef struct _NETSPLIT_VERSION_INFO
{
    UINT32 ProtocolVersion;
    UINT32 Capabilities;
} NETSPLIT_VERSION_INFO, *PNETSPLIT_VERSION_INFO;

// Output for IOCTL_NETSPLIT_GET_STATISTICS. No input required. Counters are
// monotonic for the lifetime of the driver load (reset to zero only when
// the driver is unloaded and reloaded) - a caller wanting a rate samples
// twice and diffs, same pattern NetSplit.Network's TrafficMonitor already
// uses for byte counters.
typedef struct _NETSPLIT_STATISTICS
{
    UINT64 ClassifyCount;         // every classify invocation, matched or not
    UINT64 RewriteSuccessCount;   // classify calls that matched a rule and rewrote the bind request
    UINT64 RewriteFailureCount;   // matched a rule but FwpsAcquireWritableLayerDataPointer0 or the write-rights check failed
    UINT64 IoctlFailureCount;     // rejected/failed AddRuntimeRule/RemoveRuntimeRule calls (bad input, not "rule not found")
    UINT32 ActiveRuleCount;       // current size of the kernel PID->address table
} NETSPLIT_STATISTICS, *PNETSPLIT_STATISTICS;

// Output for IOCTL_NETSPLIT_GET_DIAGNOSTICS. No input required. A strict
// superset of NETSPLIT_STATISTICS added as a NEW ioctl/struct rather than by
// growing NETSPLIT_STATISTICS in place - GET_STATISTICS's wire shape is
// therefore untouched (no version bump needed) and this is purely additive.
// Every counter here exists to answer one link in the routing pipeline
// trace: chrome.exe -> PID -> Runtime Rule -> IOCTL -> Kernel Cache ->
// Classify() -> Rewrite -> Permit. MatchedPidCount/UnmatchedPidCount answer
// "did classify() find the PID"; RewriteAttempts/RewriteSuccessCount/
// RewriteFailureCount answer "did the rewrite happen"; LastMatchedPid/
// LastRewrittenAddress answer "was the rewritten address the expected one".
typedef struct _NETSPLIT_DIAGNOSTICS
{
    UINT32 ProtocolVersion;
    UINT32 Capabilities;
    UINT64 ClassifyCount;         // every classify invocation, matched or not
    UINT64 MatchedPidCount;       // classify calls where the PID had a runtime rule
    UINT64 UnmatchedPidCount;     // classify calls where the PID had no runtime rule
    UINT64 RewriteAttempts;       // matched calls that had write rights and attempted FwpsAcquireWritableLayerDataPointer0
    UINT64 RewriteSuccessCount;   // rewrite attempts that succeeded
    UINT64 RewriteFailureCount;   // matched a rule but couldn't rewrite (no write rights, or the acquire/apply failed)
    UINT64 IoctlFailureCount;     // rejected/failed AddRuntimeRule/RemoveRuntimeRule calls
    UINT32 ActiveRuleCount;       // current size of the kernel PID->address table
    INT32  LastMatchedPid;        // -1 if no PID has ever matched a rule this driver load
    UCHAR  LastRewrittenAddress[4]; // last IPv4 successfully written into a bind request; 0.0.0.0 if none yet
} NETSPLIT_DIAGNOSTICS, *PNETSPLIT_DIAGNOSTICS;

// Input for IOCTL_NETSPLIT_ADD_RUNTIME_RULE.
// AddressFamily/TargetAddress are sized for IPv6 up front so this struct
// never needs to change shape later - but this pass only enforces IPv4
// (AF_INET); a rule with any other family is accepted and stored, but the
// classify path only acts on AF_INET rules until a V6 bind-redirect
// callout is registered (a separate, additive sprint, not a redesign).
typedef struct _NETSPLIT_RUNTIME_RULE
{
    INT32  Pid;
    UINT16 AddressFamily;      // AF_INET (2) for this pass
    UCHAR  TargetAddress[16];  // low 4 bytes used for IPv4
    BOOLEAN Enabled;
} NETSPLIT_RUNTIME_RULE, *PNETSPLIT_RUNTIME_RULE;

// Input for IOCTL_NETSPLIT_REMOVE_RUNTIME_RULE.
typedef struct _NETSPLIT_REMOVE_RUNTIME_RULE_REQUEST
{
    INT32 Pid;
} NETSPLIT_REMOVE_RUNTIME_RULE_REQUEST, *PNETSPLIT_REMOVE_RUNTIME_RULE_REQUEST;

// IOCTL_NETSPLIT_CLEAR_RUNTIME_RULES takes no input.
