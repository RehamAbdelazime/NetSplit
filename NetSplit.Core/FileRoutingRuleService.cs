using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetSplit.Core;

/// <summary>
/// Persists rules as JSON on disk. Wraps InMemoryRoutingRuleService for the
/// actual storage/validation/event logic (composition, not duplication) and
/// adds load-on-construct + save-on-every-mutation. Rule counts are small
/// (tens, not thousands) so a full-file rewrite per mutation is simple and
/// fast enough - no need for a database or incremental write format.
/// </summary>
public sealed class FileRoutingRuleService : IRoutingRuleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly InMemoryRoutingRuleService _inner = new();
    private readonly string _filePath;
    private readonly object _fileGate = new();

    public event EventHandler? RulesChanged
    {
        add => _inner.RulesChanged += value;
        remove => _inner.RulesChanged -= value;
    }

    public FileRoutingRuleService(string filePath)
    {
        _filePath = filePath;
        Load();

        // Every mutation that reaches this point already succeeded against
        // the in-memory store (validated, deduplicated) - persist it as a
        // side effect of the same event the UI/resolver already react to.
        _inner.RulesChanged += (_, _) => Save();
    }

    public IReadOnlyList<RoutingRule> GetRules() => _inner.GetRules();

    public RoutingRule? GetRule(Guid id) => _inner.GetRule(id);

    public RoutingRule AddRule(
        string processName, int? processId, RuleTargetType targetType, AdapterPreference targetAdapter, bool enabled = true) =>
        _inner.AddRule(processName, processId, targetType, targetAdapter, enabled);

    public RoutingRule UpdateRule(
        Guid id, string processName, int? processId, RuleTargetType targetType, AdapterPreference targetAdapter) =>
        _inner.UpdateRule(id, processName, processId, targetType, targetAdapter);

    public bool DeleteRule(Guid id) => _inner.DeleteRule(id);

    public bool EnableRule(Guid id) => _inner.EnableRule(id);

    public bool DisableRule(Guid id) => _inner.DisableRule(id);

    private void Load()
    {
        lock (_fileGate)
        {
            if (!File.Exists(_filePath))
            {
                return; // first run - nothing to load, not an error
            }

            List<RoutingRule>? rules;
            try
            {
                string json = File.ReadAllText(_filePath);
                rules = JsonSerializer.Deserialize<List<RoutingRule>>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A corrupt or unreadable rules file must not prevent the
                // service from starting - start empty rather than crash-loop.
                return;
            }

            if (rules == null)
            {
                return;
            }

            foreach (RoutingRule rule in rules)
            {
                try
                {
                    _inner.AddRule(rule.ProcessName, rule.ProcessId, rule.TargetType, rule.TargetAdapter, rule.Enabled);
                }
                catch (DuplicateRoutingRuleException)
                {
                    // A hand-edited or corrupted file could contain
                    // duplicates - keep the first, skip the rest, don't fail
                    // the whole load over it.
                }
            }
        }
    }

    private void Save()
    {
        lock (_fileGate)
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(_inner.GetRules(), JsonOptions);

            // Write to a temp file and replace - avoids a half-written rules
            // file if the process is killed mid-save.
            string tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
    }
}
