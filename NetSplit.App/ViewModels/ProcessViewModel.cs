using System.Collections.ObjectModel;
using NetSplit.Core;

namespace NetSplit.App.ViewModels;

// One row in the inspector. Depends only on IRoutingRuleService (NetSplit.Core)
// - no NetSplit.Driver.Interop reference anywhere in this project.
public sealed class ProcessViewModel : ViewModelBase
{
    private readonly IRoutingRuleService _rules;

    // Set by MainViewModel while it is writing CurrentAdapter/AdapterOptions/
    // SelectedAdapterOption during a refresh, so that re-applying the
    // already-current selection doesn't re-issue a redundant rule write.
    public bool SuppressRuleWrites { get; set; }

    public required string ProcessName { get; init; }
    public required int Pid { get; init; }

    private string _currentAdapter = "Unknown";
    public string CurrentAdapter
    {
        get => _currentAdapter;
        set => SetField(ref _currentAdapter, value);
    }

    private double _sendBytesPerSecond;
    public double SendBytesPerSecond
    {
        get => _sendBytesPerSecond;
        set
        {
            if (SetField(ref _sendBytesPerSecond, value))
            {
                RaisePropertyChanged(nameof(SendRateText));
            }
        }
    }
    public string SendRateText => ByteRateFormatter.Format(SendBytesPerSecond);

    private double _receiveBytesPerSecond;
    public double ReceiveBytesPerSecond
    {
        get => _receiveBytesPerSecond;
        set
        {
            if (SetField(ref _receiveBytesPerSecond, value))
            {
                RaisePropertyChanged(nameof(ReceiveRateText));
            }
        }
    }
    public string ReceiveRateText => ByteRateFormatter.Format(ReceiveBytesPerSecond);

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();

    private List<AdapterOptionViewModel> _adapterOptions = new() { AdapterOptionViewModel.Auto };
    public List<AdapterOptionViewModel> AdapterOptions
    {
        get => _adapterOptions;
        set => SetField(ref _adapterOptions, value);
    }

    private AdapterOptionViewModel _selectedAdapterOption = AdapterOptionViewModel.Auto;
    public AdapterOptionViewModel SelectedAdapterOption
    {
        get => _selectedAdapterOption;
        set
        {
            if (!SetField(ref _selectedAdapterOption, value) || SuppressRuleWrites)
            {
                return;
            }

            ApplyRule(value);
        }
    }

    // Not yet populated by anything - reserved for when the driver can
    // report back which connections it actually redirected.
    private bool? _ruleApplied;
    public bool? RuleApplied
    {
        get => _ruleApplied;
        set => SetField(ref _ruleApplied, value);
    }

    public ProcessViewModel(IRoutingRuleService rules)
    {
        _rules = rules;
    }

    private void ApplyRule(AdapterOptionViewModel adapter)
    {
        RoutingRule? existing = _rules.GetRules().FirstOrDefault(r =>
            r.TargetType == RuleTargetType.ProcessName &&
            string.Equals(r.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase));

        if (adapter.IsAuto)
        {
            if (existing != null)
            {
                _rules.DeleteRule(existing.Id);
            }
            return;
        }

        var preference = new AdapterPreference(adapter.InterfaceIndex, adapter.DisplayName);

        if (existing != null)
        {
            _rules.UpdateRule(existing.Id, ProcessName, null, RuleTargetType.ProcessName, preference);
        }
        else
        {
            _rules.AddRule(ProcessName, null, RuleTargetType.ProcessName, preference);
        }
    }
}
