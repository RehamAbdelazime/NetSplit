using System.ComponentModel;

namespace NetSplit.ui;

// One row in the debug grid. No persistence, no rule list - just what's
// currently on screen.
public sealed class ProcessRow : INotifyPropertyChanged
{
    public string ProcessName { get; init; } = "";
    public int Pid { get; init; }
    public string CurrentAdapter { get; set; } = "";

    private string _selectedAdapter = "";
    public string SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (_selectedAdapter != value)
            {
                _selectedAdapter = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAdapter)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyCurrentAdapterChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentAdapter)));
}
