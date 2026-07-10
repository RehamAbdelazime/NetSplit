using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetSplit.Ipc;

namespace NetSplit.App;

/// <summary>
/// Temporary, developer-only window: raw live values from every stage of
/// the routing pipeline, refreshed once a second. Not part of the product
/// UI - no view model, no MVVM ceremony, built to be deleted once the
/// routing engine is trusted again. Green = OK, red = failure, gray =
/// no data yet.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private static readonly Brush Green = Brushes.SeaGreen;
    private static readonly Brush Red = Brushes.Firebrick;
    private static readonly Brush Gray = Brushes.Gray;
    private static readonly Brush Normal = Brushes.Black;

    private readonly PipeRoutingRuleClient _client;
    private readonly DispatcherTimer _timer;

    public DiagnosticsWindow(PipeRoutingRuleClient client)
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
            LastRefreshedText.Text = $"Refresh failed: {ex.Message}";
            LastRefreshedText.Foreground = Red;
            return;
        }

        LastRefreshedText.Text = $"Last refreshed {DateTimeOffset.Now:HH:mm:ss}";
        LastRefreshedText.Foreground = Gray;

        DeviceOpenFields.Items.Clear();
        DeviceOpenDiagnosticsDto d0 = snapshot.DeviceOpen;
        DeviceOpenFields.Items.Add(Row("Device Path Attempted", d0.DevicePathAttempted, Normal));
        DeviceOpenFields.Items.Add(Row("CreateFile Succeeded", d0.CreateFileSucceeded.ToString(), d0.CreateFileSucceeded ? Green : Red));
        DeviceOpenFields.Items.Add(Row("Win32 Error Code", d0.Win32ErrorCode.ToString(), d0.CreateFileSucceeded ? Normal : Red));
        DeviceOpenFields.Items.Add(Row("Win32 Error Message", d0.Win32ErrorMessage, d0.CreateFileSucceeded ? Normal : Red));
        DeviceOpenFields.Items.Add(Row("Symbolic Link Path", d0.SymbolicLinkPath, Normal));
        DeviceOpenFields.Items.Add(Row("Symbolic Link Exists", d0.SymbolicLinkExists.ToString(), d0.SymbolicLinkExists ? Green : Red));
        DeviceOpenFields.Items.Add(Row("Symbolic Link Target", d0.SymbolicLinkTarget ?? "(none)", d0.SymbolicLinkExists ? Normal : Gray));
        DeviceOpenFields.Items.Add(Row("Device Object Exists (GLOBALROOT probe)", d0.DeviceObjectExists.ToString(), d0.DeviceObjectExists ? Green : Red));
        DeviceOpenFields.Items.Add(Row("Device Object Probe Win32 Error", $"{d0.DeviceObjectProbeWin32ErrorCode} ({d0.DeviceObjectProbeWin32ErrorMessage})", d0.DeviceObjectExists ? Normal : Red));
        DeviceOpenFields.Items.Add(Row("Driver Service State", d0.DriverServiceState, d0.DriverServiceState == "Running" ? Green : Red));
        DeviceOpenFields.Items.Add(Row("Driver Service State Detail", d0.DriverServiceStateDetail, d0.DriverServiceState == "Running" ? Normal : Red));

        ScmStateDto scm = snapshot.ScmState;
        DeviceOpenFields.Items.Add(Row("SCM State", scm.State, scm.State == "Running" ? Green : Red));
        DeviceOpenFields.Items.Add(Row("SCM State Detail", scm.Detail, scm.State == "Running" ? Normal : Red));
        DeviceOpenFields.Items.Add(Row("SCM Win32 Error", scm.Win32ErrorCode.HasValue ? $"{scm.Win32ErrorCode} ({scm.Win32ErrorMessage})" : "(none)", scm.Win32ErrorCode.HasValue ? Red : Gray));
        DeviceOpenFields.Items.Add(Row("SCM Start Permission Available", scm.StartPermissionAvailable.ToString(), scm.StartPermissionAvailable ? Normal : Gray));

        DriverBuildInfoDto build = snapshot.DriverBuild;
        DeviceOpenFields.Items.Add(Row("Driver Build Image Path", build.ImagePath ?? "(unknown)", build.ImagePath != null ? Normal : Gray));
        DeviceOpenFields.Items.Add(Row("Driver Build Timestamp (file last-write, UTC)", build.FileWriteTimeUtc?.ToString("u") ?? "(unknown)", build.FileWriteTimeUtc.HasValue ? Normal : Gray));
        DeviceOpenFields.Items.Add(Row("Driver Build File Size", build.FileSizeBytes?.ToString() ?? "(unknown)", build.FileSizeBytes.HasValue ? Normal : Gray));
        DeviceOpenFields.Items.Add(Row("Driver Build Detail", build.Detail, Gray));

        ServiceFields.Items.Clear();
        ServiceFields.Items.Add(Row("Driver Connected", snapshot.DriverConnected.ToString(), snapshot.DriverConnected ? Green : Red));
        ServiceFields.Items.Add(Row("Driver Availability State", snapshot.DriverAvailabilityState, snapshot.DriverAvailabilityState == "Running" ? Green : Red));
        ServiceFields.Items.Add(Row("Driver Availability Detail", snapshot.DriverAvailabilityDetail, snapshot.DriverAvailabilityState == "Running" ? Normal : Red));
        ServiceFields.Items.Add(Row("Driver Protocol Version", snapshot.DriverProtocolVersion?.ToString() ?? "(unknown)", snapshot.DriverConnected ? Normal : Gray));
        ServiceFields.Items.Add(Row("Driver Protocol Compatible", snapshot.DriverProtocolCompatible.ToString(), snapshot.DriverConnected && !snapshot.DriverProtocolCompatible ? Red : Normal));
        ServiceFields.Items.Add(Row("Permanent Rule Count", snapshot.PermanentRules.Count.ToString(), Normal));
        ServiceFields.Items.Add(Row("Runtime Rule Count", snapshot.RuntimeRules.Count.ToString(), snapshot.PermanentRules.Count > 0 && snapshot.RuntimeRules.Count == 0 ? Red : Normal));
        ServiceFields.Items.Add(Row("Connected UI Clients", snapshot.ConnectedUiClients.ToString(), Normal));
        ServiceFields.Items.Add(Row("Last IOCTL Result: Last Successful Sync", FormatTime(snapshot.LastSuccessfulSyncAt), snapshot.LastSuccessfulSyncAt.HasValue ? Green : Gray));
        ServiceFields.Items.Add(Row("Last IOCTL Result: Last Failed Sync", FormatTime(snapshot.LastFailedSyncAt), snapshot.LastFailedSyncAt.HasValue ? Red : Gray));
        ServiceFields.Items.Add(Row("Last IOCTL Result: Failure Reason", snapshot.LastFailedSyncReason ?? "(none)", snapshot.LastFailedSyncReason != null ? Red : Gray));
        ServiceFields.Items.Add(Row("Sync Push Success Count", snapshot.SyncPushSuccessCount.ToString(), Normal));
        ServiceFields.Items.Add(Row("Sync Push Failure Count", snapshot.SyncPushFailureCount.ToString(), snapshot.SyncPushFailureCount > 0 ? Red : Normal));

        DriverFields.Items.Clear();
        DriverFields.Items.Add(Row("Classify Count", snapshot.DriverClassifyCount.ToString(), Normal));
        DriverFields.Items.Add(Row("Matched PID Count", snapshot.DriverMatchedPidCount.ToString(), Normal));
        DriverFields.Items.Add(Row("Unmatched PID Count", snapshot.DriverUnmatchedPidCount.ToString(), Normal));
        DriverFields.Items.Add(Row("Rewrite Attempts", snapshot.DriverRewriteAttempts.ToString(), Normal));
        DriverFields.Items.Add(Row("Rewrite Success Count", snapshot.DriverRewriteSuccessCount.ToString(), snapshot.DriverRewriteAttempts > 0 && snapshot.DriverRewriteSuccessCount == 0 ? Red : Green));
        DriverFields.Items.Add(Row("Rewrite Failure Count", snapshot.DriverRewriteFailureCount.ToString(), snapshot.DriverRewriteFailureCount > 0 ? Red : Normal));
        DriverFields.Items.Add(Row("IOCTL Failure Count", snapshot.DriverIoctlFailureCount.ToString(), snapshot.DriverIoctlFailureCount > 0 ? Red : Normal));
        DriverFields.Items.Add(Row("Active Rule Count (kernel table)", snapshot.DriverActiveRuleCount.ToString(), Normal));
        DriverFields.Items.Add(Row("Last Matched PID", snapshot.DriverLastMatchedPid?.ToString() ?? "(none yet)", snapshot.DriverLastMatchedPid.HasValue ? Normal : Gray));
        DriverFields.Items.Add(Row("Last Rewritten Address", snapshot.DriverLastRewrittenAddress ?? "(none yet)", snapshot.DriverLastRewrittenAddress != null ? Green : Gray));

        RuntimeRulesList.Items.Clear();
        if (snapshot.RuntimeRules.Count == 0)
        {
            RuntimeRulesList.Items.Add(Row("(none)", "no runtime rule currently active", Gray));
        }
        foreach (RuntimeRuleDto rule in snapshot.RuntimeRules)
        {
            string label = $"{rule.ProcessName} (PID {rule.Pid})";
            string value = $"-> {rule.TargetAdapterName} [{rule.TargetInterfaceIndex}]  pushed: {rule.PushedAddress ?? "(not yet pushed)"}";
            RuntimeRulesList.Items.Add(Row(label, value, rule.PushedAddress == null ? Red : Green));
        }

        AdapterCacheList.Items.Clear();
        foreach (AdapterSnapshotDto adapter in snapshot.AdapterCache)
        {
            AdapterCacheList.Items.Add(Row(adapter.FriendlyName, $"index={adapter.InterfaceIndex} ipv4={adapter.IPv4 ?? "(none)"}", adapter.IPv4 == null ? Red : Normal));
        }

        PermanentRulesList.Items.Clear();
        foreach (var rule in snapshot.PermanentRules)
        {
            PermanentRulesList.Items.Add(Row(rule.ProcessName, $"-> {rule.TargetAdapter.AdapterName} (enabled={rule.Enabled})", rule.Enabled ? Normal : Gray));
        }
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value.HasValue ? $"{value.Value.ToLocalTime():HH:mm:ss} ({(DateTimeOffset.UtcNow - value.Value).TotalSeconds:F0}s ago)" : "(never)";

    private static UIElement Row(string label, string value, Brush color)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        panel.Children.Add(new TextBlock { Text = label, Width = 260, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = value, Foreground = color });
        return panel;
    }
}
