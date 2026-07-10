using NetSplit.Core;

namespace NetSplit.Runtime;

/// <summary>
/// A permanent rule ("chrome.exe -> Wi-Fi") resolved against one currently
/// running process. The unit the driver will eventually act on - a PID, not
/// a name - because a name can match zero, one, or many running processes
/// at once, and each one starts and exits independently.
/// </summary>
public sealed record RuntimeRule(
    Guid PermanentRuleId,
    int Pid,
    string ProcessName,
    AdapterPreference TargetAdapter);
