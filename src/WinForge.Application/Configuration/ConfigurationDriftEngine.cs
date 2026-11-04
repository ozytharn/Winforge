using System.Globalization;
using WinForge.Domain.Configuration;
using WinForge.Domain.Inventory;

namespace WinForge.Application.Configuration;

public sealed class ConfigurationDriftEngine
{
    public ConfigurationDriftAssessment Analyze(
        PassportSnapshot? previous,
        PassportSnapshot current,
        PassportSnapshot? confirmation = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var previousComponents = previous?.Components ?? [];
        var previousMap = previousComponents
            .GroupBy(static component => MakeKey(component.Type, component.IdentityKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var currentMap = current.Components
            .GroupBy(static component => MakeKey(component.Type, component.IdentityKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var confirmationMap = confirmation is null
            ? new Dictionary<string, InventoryComponent>(StringComparer.OrdinalIgnoreCase)
            : confirmation.Components
                .GroupBy(static component => MakeKey(component.Type, component.IdentityKey), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var changes = new List<ConfigurationDriftChange>();
        var warnings = new List<string>();

        foreach (var component in current.Components.OrderBy(static component => component.Type).ThenBy(static component => component.IdentityKey, StringComparer.OrdinalIgnoreCase))
        {
            var key = MakeKey(component.Type, component.IdentityKey);
            if (!previousMap.TryGetValue(key, out var previousComponent))
            {
                changes.Add(new ConfigurationDriftChange(
                    component.Type.ToString(),
                    component.IdentityKey,
                    component.DisplayName,
                    component.Source,
                    ConfigurationDriftClassification.Added,
                    $"{component.DisplayName} was observed in the current snapshot and was not present in the previous snapshot.",
                    [],
                    0.85m,
                    true,
                    component.ObservedAt));
                continue;
            }

            var fieldChanges = CompareComponentFields(previousComponent, component);
            if (fieldChanges.Count == 0)
            {
                continue;
            }

            var summary = fieldChanges.Count == 1
                ? $"The value of {fieldChanges[0].FieldName} changed for {component.DisplayName}."
                : $"Multiple fields changed for {component.DisplayName}; the evidence indicates a configuration update.";

            changes.Add(new ConfigurationDriftChange(
                component.Type.ToString(),
                component.IdentityKey,
                component.DisplayName,
                component.Source,
                ConfigurationDriftClassification.Modified,
                summary,
                fieldChanges,
                ComputeConfidence(fieldChanges),
                true,
                component.ObservedAt));

            if (fieldChanges.Any(change =>
                    change.PreviousAvailability != InventoryFieldAvailability.Available &&
                    change.CurrentAvailability == InventoryFieldAvailability.Available ||
                    change.PreviousAvailability == InventoryFieldAvailability.Available &&
                    change.CurrentAvailability != InventoryFieldAvailability.Available))
            {
                warnings.Add($"Field availability changed for {component.DisplayName}; the current observation may be incomplete or intentionally unavailable.");
            }
        }

        foreach (var component in previousComponents.OrderBy(static component => component.Type).ThenBy(static component => component.IdentityKey, StringComparer.OrdinalIgnoreCase))
        {
            var key = MakeKey(component.Type, component.IdentityKey);
            if (currentMap.ContainsKey(key))
            {
                continue;
            }

            var confirmationProvided = confirmation is not null;
            var currentlyAbsent = confirmationProvided && !confirmationMap.ContainsKey(key);
            var classification = currentlyAbsent
                ? ConfigurationDriftClassification.PotentiallyRemoved
                : ConfigurationDriftClassification.NotObserved;

            var summary = currentlyAbsent
                ? $"{component.DisplayName} was absent in the current snapshot and again absent in the follow-up snapshot; this suggests a possible removal but is not proof."
                : $"{component.DisplayName} was not observed in the current snapshot. Missing data is not treated as a confirmed removal.";

            if (currentlyAbsent)
            {
                warnings.Add($"{component.DisplayName} may have been removed, but absence remains an inference rather than a confirmed fact.");
            }

            changes.Add(new ConfigurationDriftChange(
                component.Type.ToString(),
                component.IdentityKey,
                component.DisplayName,
                component.Source,
                classification,
                summary,
                [],
                currentlyAbsent ? 0.58m : 0.35m,
                false,
                component.ObservedAt));
        }

        var addedCount = changes.Count(change => change.Classification == ConfigurationDriftClassification.Added);
        var modifiedCount = changes.Count(change => change.Classification == ConfigurationDriftClassification.Modified);
        var potentiallyRemovedCount = changes.Count(change => change.Classification == ConfigurationDriftClassification.PotentiallyRemoved);
        var notObservedCount = changes.Count(change => change.Classification == ConfigurationDriftClassification.NotObserved);
        var unchangedCount = Math.Max(0, current.Components.Count - addedCount - modifiedCount);

        return new ConfigurationDriftAssessment(
            Changes: changes
                .OrderBy(change => change.Classification)
                .ThenBy(change => change.EntityKind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(change => change.EntityIdentity, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Warnings: warnings
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            AddedCount: addedCount,
            ModifiedCount: modifiedCount,
            PotentiallyRemovedCount: potentiallyRemovedCount,
            NotObservedCount: notObservedCount,
            UnchangedCount: unchangedCount);
    }

    private static string MakeKey(InventoryComponentType type, string identityKey)
        => $"{type}:{identityKey.Trim()}";

    private static List<ConfigurationDriftFieldChange> CompareComponentFields(InventoryComponent previous, InventoryComponent current)
    {
        var changes = new List<ConfigurationDriftFieldChange>();
        var allKeys = previous.Details.Keys
            .Concat(current.Details.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var key in allKeys)
        {
            var previousValue = previous.Details.TryGetValue(key, out var previousDetail) ? previousDetail : null;
            var currentValue = current.Details.TryGetValue(key, out var currentDetail) ? currentDetail : null;
            var previousAvailability = previous.FieldAvailability.TryGetValue(key, out var previousAvailabilityValue)
                ? previousAvailabilityValue
                : InventoryFieldAvailability.Unknown;
            var currentAvailability = current.FieldAvailability.TryGetValue(key, out var currentAvailabilityValue)
                ? currentAvailabilityValue
                : InventoryFieldAvailability.Unknown;

            if (Normalized(previousValue) == Normalized(currentValue) &&
                previousAvailability == currentAvailability)
            {
                continue;
            }

            changes.Add(new ConfigurationDriftFieldChange(
                key,
                previousValue,
                currentValue,
                previousAvailability,
                currentAvailability,
                BuildExplanation(key, previousValue, currentValue, previousAvailability, currentAvailability)));
        }

        if (!string.Equals(previous.DisplayName, current.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.Source, current.Source, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(new ConfigurationDriftFieldChange(
                "identity-context",
                previous.DisplayName,
                current.DisplayName,
                InventoryFieldAvailability.Available,
                InventoryFieldAvailability.Available,
                "Display name or collection source changed for the same inventory identity."));
        }

        return changes;
    }

    private static string BuildExplanation(
        string fieldName,
        string? previousValue,
        string? currentValue,
        InventoryFieldAvailability previousAvailability,
        InventoryFieldAvailability currentAvailability)
    {
        if (previousAvailability != currentAvailability)
        {
            return $"Field '{fieldName}' changed availability from {previousAvailability} to {currentAvailability}.";
        }

        return $"Field '{fieldName}' changed from '{Truncate(previousValue)}' to '{Truncate(currentValue)}'.";
    }

    private static string? Normalized(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal ComputeConfidence(IReadOnlyList<ConfigurationDriftFieldChange> fieldChanges)
    {
        var score = 0.55m;
        score += (decimal)fieldChanges.Count * 0.08m;
        if (fieldChanges.Any(change => change.PreviousAvailability != change.CurrentAvailability))
        {
            score -= 0.12m;
        }

        return Math.Clamp(score, 0.45m, 0.96m);
    }

    private static string Truncate(string? value, int maxLength = 160) =>
        string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : value.Length <= maxLength
                ? value
                : value.Substring(0, maxLength) + "…";
}
