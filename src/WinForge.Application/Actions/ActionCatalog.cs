using WinForge.Domain.Actions;

namespace WinForge.Application.Actions;

public sealed class TrustedActionCatalog
{
    private readonly IReadOnlyDictionary<string, ActionCatalogEntry> _entries;

    public TrustedActionCatalog()
    {
        _entries = new Dictionary<string, ActionCatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["read-only.inspect.backup-catalog"] = new(
                "read-only.inspect.backup-catalog",
                "Backup catalog inspection",
                "1.0.0",
                "Read-only inspection",
                ActionRiskLevel.Informational,
                false,
                true,
                false,
                "Read-only inspection of the bounded backup catalog and available metadata.",
                ["boot-volume", "backup-store", "recovery-volume"],
                false),
            ["read-only.inspect.recovery"] = new(
                "read-only.inspect.recovery",
                "Recovery readiness inspection",
                "1.0.0",
                "Read-only inspection",
                ActionRiskLevel.Informational,
                false,
                true,
                false,
                "Review the current recovery-readiness evidence without changing any configuration.",
                ["boot-configuration", "bitlocker", "winre"],
                false),
            ["service.inspect"] = new(
                "service.inspect",
                "Inspect service state",
                "1.0.0",
                "Read-only inspection",
                ActionRiskLevel.Informational,
                false,
                true,
                false,
                "Inspect a service entry in the simulation-only catalog without making any system change.",
                ["critical-service"],
                false),
            ["driver.inspect"] = new(
                "driver.inspect",
                "Inspect driver state",
                "1.0.0",
                "Read-only inspection",
                ActionRiskLevel.Informational,
                false,
                true,
                false,
                "Inspect a simulated driver record without changing any real driver state.",
                ["kernel-driver", "security-driver"],
                false),
            ["user-preferences.update"] = new(
                "user-preferences.update",
                "Update user preference",
                "1.0.0",
                "User-scoped configuration",
                ActionRiskLevel.Low,
                true,
                true,
                false,
                "Update a user preference in the isolated simulation state.",
                ["system-preference", "startup-critical"],
                true),
            ["workspace.cleanup.transient-artifacts"] = new(
                "workspace.cleanup.transient-artifacts",
                "Clean transient workspace artifacts",
                "1.0.0",
                "User-scoped configuration",
                ActionRiskLevel.Low,
                true,
                true,
                false,
                "Remove only transient artifacts from the simulated workspace state.",
                ["protected-workspace"],
                true),
            ["startup-entry.toggle.current-user"] = new(
                "startup-entry.toggle.current-user",
                "Toggle current-user startup entry",
                "1.0.0",
                "Moderate-risk configuration",
                ActionRiskLevel.Moderate,
                true,
                true,
                false,
                "Toggle a current-user startup entry inside the simulation-only state.",
                ["system-startup", "security-startup"],
                true),
            ["settings.apply.allowlisted"] = new(
                "settings.apply.allowlisted",
                "Apply allowlisted setting",
                "1.0.0",
                "User-scoped configuration",
                ActionRiskLevel.Low,
                true,
                true,
                false,
                "Apply a user-scoped network or UI setting in a safe, allowlisted simulation scope.",
                ["security-setting", "boot-setting"],
                true)
        };
    }

    public IReadOnlyList<ActionCatalogEntry> Entries => _entries.Values
        .OrderBy(static entry => entry.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public ActionCatalogEntry Get(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            throw new ArgumentException("Action id is required.", nameof(actionId));
        }

        if (!_entries.TryGetValue(actionId, out var entry))
        {
            throw new KeyNotFoundException($"Action '{actionId}' is not defined in the trusted catalog.");
        }

        return entry;
    }

    public bool Contains(string actionId) => !string.IsNullOrWhiteSpace(actionId) && _entries.ContainsKey(actionId);
}
