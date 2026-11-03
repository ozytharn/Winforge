namespace WinForge.Application.Monitoring;

public sealed class GuardianAlertTracker
{
    private readonly Dictionary<string, (GuardianFinding Finding, int ConsecutiveSamples)> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GuardianFinding> _active = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<GuardianFinding> ActiveAlerts => _active.Values.ToArray();

    public IReadOnlyList<GuardianFinding> Observe(IEnumerable<GuardianFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var current = findings.ToDictionary(finding => finding.Category, StringComparer.OrdinalIgnoreCase);
        var newlyActive = new List<GuardianFinding>();
        foreach (var category in _pending.Keys.Concat(_active.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            if (!current.TryGetValue(category, out var finding) ||
                finding.State is GuardianFindingState.Informational or GuardianFindingState.Unknown)
            {
                _pending.Remove(category);
                _active.Remove(category);
                continue;
            }

            var count = _pending.TryGetValue(category, out var previous)
                ? previous.ConsecutiveSamples + 1
                : 1;
            _pending[category] = (finding, count);
            if (count < 2)
            {
                continue;
            }

            if (!_active.TryGetValue(category, out var active) || active.State != finding.State)
            {
                newlyActive.Add(finding);
            }

            _active[category] = finding;
        }

        foreach (var finding in current.Values.Where(finding =>
                     (finding.State is GuardianFindingState.Warning or GuardianFindingState.Critical) &&
                     !_pending.ContainsKey(finding.Category)))
        {
            _pending[finding.Category] = (finding, 1);
        }

        return newlyActive;
    }
}
