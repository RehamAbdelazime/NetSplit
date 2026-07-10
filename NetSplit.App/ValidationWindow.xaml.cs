using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetSplit.Ipc;

namespace NetSplit.App;

/// <summary>
/// Temporary, developer-only window: one row per stage of
/// UI -> Service -> Runtime Rule -> IOCTL -> Driver Cache -> Classify() ->
/// Rewrite() -> Traffic Redirected, each showing whether that stage's own
/// evidence is currently present. "Traffic Redirected" is deliberately
/// never automated - none of the counters here can prove a socket actually
/// bound through the target adapter, only that the driver attempted the
/// rewrite - so it always reads "?", same as production validation would
/// require an external capture/connection check to resolve either way.
/// </summary>
public partial class ValidationWindow : Window
{
    private static readonly Brush Green = Brushes.SeaGreen;
    private static readonly Brush Red = Brushes.Firebrick;
    private static readonly Brush Amber = Brushes.DarkGoldenrod;

    private readonly PipeRoutingRuleClient _client;
    private readonly DispatcherTimer _timer;

    public ValidationWindow(PipeRoutingRuleClient client)
    {
        InitializeComponent();
        _client = client;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void Refresh()
    {
        DiagnosticsSnapshotDto snapshot;
        try
        {
            snapshot = _client.GetDiagnostics();
        }
        catch (Exception ex)
        {
            StageList.Items.Clear();
            StageList.Items.Add(Stage("UI", false, $"Could not reach NetSplit.Service: {ex.Message}"));
            FooterText.Text = $"Refresh failed at {DateTimeOffset.Now:HH:mm:ss}";
            return;
        }

        StageList.Items.Clear();

        // Each stage's own evidence, not inferred from the stage before it -
        // if an earlier stage failed, the later ones are naturally false too
        // (e.g. no runtime rule means no IOCTL push means no driver cache
        // entry), but each is still checked independently rather than
        // short-circuited, so a genuinely surprising combination (e.g.
        // Driver Cache populated but Classify() never called) is visible
        // instead of hidden.
        StageList.Items.Add(Stage("UI", true, "This window is itself a pipe client - reaching here proves the UI-side transport works."));
        StageList.Items.Add(Stage("Service", snapshot.ConnectedUiClients > 0, $"ConnectedUiClients = {snapshot.ConnectedUiClients}"));
        StageList.Items.Add(Stage("Runtime Rule", snapshot.RuntimeRules.Count > 0, $"RuntimeRules.Count = {snapshot.RuntimeRules.Count}"));
        StageList.Items.Add(Stage("IOCTL", snapshot.SyncPushSuccessCount > 0, $"SyncPushSuccessCount = {snapshot.SyncPushSuccessCount}, SyncPushFailureCount = {snapshot.SyncPushFailureCount}"));
        StageList.Items.Add(Stage("Driver Cache", snapshot.DriverActiveRuleCount > 0, $"DriverActiveRuleCount = {snapshot.DriverActiveRuleCount}"));
        StageList.Items.Add(Stage("Classify()", snapshot.DriverMatchedPidCount > 0, $"DriverMatchedPidCount = {snapshot.DriverMatchedPidCount} (Unmatched = {snapshot.DriverUnmatchedPidCount})"));
        StageList.Items.Add(Stage("Rewrite()", snapshot.DriverRewriteSuccessCount > 0, $"DriverRewriteSuccessCount = {snapshot.DriverRewriteSuccessCount} (Failures = {snapshot.DriverRewriteFailureCount}), LastRewrittenAddress = {snapshot.DriverLastRewrittenAddress ?? "(none yet)"}"));
        StageList.Items.Add(StageUnknown("Traffic Redirected", "Not automatable from driver counters alone - confirm with an external capture (e.g. netstat -ano / packet capture on the target adapter) that the rewritten connection actually flows there."));

        FooterText.Text = $"Last refreshed {DateTimeOffset.Now:HH:mm:ss}";
    }

    private static UIElement Stage(string name, bool ok, string evidence)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(new TextBlock { Text = ok ? "✔" : "✖", Foreground = ok ? Green : Red, Width = 24, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = name, Width = 140, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = evidence, Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static UIElement StageUnknown(string name, string evidence)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(new TextBlock { Text = "?", Foreground = Amber, Width = 24, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = name, Width = 140, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = evidence, Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }
}
