namespace NetSplit.App.ViewModels;

// One TCP connection row shown when a process is expanded - the TCPView-style detail view.
public sealed class ConnectionViewModel
{
    public required string LocalEndpoint { get; init; }
    public required string RemoteEndpoint { get; init; }
    public required string State { get; init; }
    public required string Adapter { get; init; }
}
