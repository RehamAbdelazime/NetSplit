namespace NetSplit.App.ViewModels;

// One selectable entry in a process's adapter dropdown. Every physical or
// virtual adapter is its own entry - never collapsed by NetworkInterfaceType.
public sealed record AdapterOptionViewModel(string DisplayName, string? Ipv4Text, int InterfaceIndex, bool IsAuto)
{
    public static readonly AdapterOptionViewModel Auto = new("Auto", null, -1, true);
}
