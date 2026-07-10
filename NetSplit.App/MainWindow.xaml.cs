using System.Windows;
using NetSplit.App.ViewModels;
using NetSplit.Ipc;

namespace NetSplit.App;

public partial class MainWindow : Window
{
    private readonly PipeRoutingRuleClient _diagnosticsClient;

    public MainWindow(MainViewModel viewModel, PipeRoutingRuleClient diagnosticsClient)
    {
        InitializeComponent();
        DataContext = viewModel;
        _diagnosticsClient = diagnosticsClient;
    }

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        new DiagnosticsWindow(_diagnosticsClient) { Owner = this }.Show();
    }

    private void ValidationButton_Click(object sender, RoutedEventArgs e)
    {
        new ValidationWindow(_diagnosticsClient) { Owner = this }.Show();
    }
}
