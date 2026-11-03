using System.Globalization;
using WinForge.Domain.Inventory;

namespace WinForge.App.ViewModels;

public sealed class PassportComponentViewModel
{
    public PassportComponentViewModel(
        InventoryComponent component,
        InventoryComparisonStatus? comparisonStatus,
        string comparisonText)
    {
        Type = component.Type.ToString();
        Category = component.Category;
        ComparisonStatus = comparisonStatus;
        ComparisonText = comparisonText;
        Details = component.Details;
        DisplayName = component.DisplayName;
        Source = component.Source;
        ObservedAtLocal = component.ObservedAt.ToLocalTime().ToString(
            "g",
            CultureInfo.CurrentCulture);
        var details = component.Details
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var availability = component.FieldAvailability.TryGetValue(pair.Key, out var value)
                    ? value
                    : pair.Value is null
                        ? InventoryFieldAvailability.Unknown
                        : InventoryFieldAvailability.Available;
                var displayValue = string.IsNullOrWhiteSpace(pair.Value)
                    ? FormatAvailability(availability)
                    : pair.Value;
                return $"{SplitName(pair.Key)}: {displayValue}";
            });
        DetailsText = $"History: {comparisonText}{Environment.NewLine}{string.Join(Environment.NewLine, details)}";

        var status = new[] { "driverStatus", "status", "Status", "State", "state", "activationStatus" }
            .Select(key => component.Details.GetValueOrDefault(key))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        StatusText = status ?? "Status not reported";
    }

    public string Type { get; }

    public string Category { get; }

    public string DisplayName { get; }

    public string Source { get; }

    public string ObservedAtLocal { get; }

    public string DetailsText { get; }

    public string StatusText { get; }

    public InventoryComparisonStatus? ComparisonStatus { get; }

    public string ComparisonText { get; }

    public IReadOnlyDictionary<string, string?> Details { get; }

    private static string FormatAvailability(InventoryFieldAvailability availability) =>
        availability switch
        {
            InventoryFieldAvailability.Missing => "Not reported",
            InventoryFieldAvailability.Unavailable => "Unavailable",
            InventoryFieldAvailability.Unsupported => "Unsupported",
            InventoryFieldAvailability.Inaccessible => "Inaccessible",
            _ => "Unknown"
        };

    private static string SplitName(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
}
