using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WinForge.Domain.Identity;
using WinForge.Domain.Inventory;

namespace WinForge.Application.Identity;

public sealed class SoftwareIdentityEngine
{
    public SoftwareIdentityAssessment Analyze(IEnumerable<InventoryComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        var componentList = components
            .Where(HasIdentityEvidence)
            .ToArray();

        if (componentList.Length == 0)
        {
            return new SoftwareIdentityAssessment([], [], ["No software identity evidence was available."]);
        }

        var groups = componentList
            .GroupBy(component => BuildCanonicalGroupKey(component), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var products = new List<SoftwareIdentityRecord>();
        var conflicts = new List<SoftwareIdentityConflict>();
        var warnings = new List<string>();

        foreach (var group in groups)
        {
            var groupComponents = group
                .OrderBy(component => component.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(component => component.IdentityKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var name = GetBestName(groupComponents);
            var publisher = GetBestPublisher(groupComponents);
            var version = GetBestVersion(groupComponents);
            var packageIdentity = GetBestPackageIdentity(groupComponents);
            var installLocation = GetBestInstallLocation(groupComponents);
            var evidence = groupComponents
                .SelectMany(GetEvidence)
                .GroupBy(item => $"{item.Source}|{item.EvidenceType}|{NormalizeText(item.Value)}")
                .Select(grouping => grouping.First())
                .OrderBy(item => item.ObservedAtUtc)
                .ToArray();

            var confidence = EvaluateConfidence(name, publisher, packageIdentity, installLocation, evidence.Length);
            if (confidence == SoftwareIdentityConfidence.Low)
            {
                warnings.Add($"{name} has low-confidence identity evidence and should not be treated as a confirmed product match.");
            }

            var record = new SoftwareIdentityRecord(
                LogicalProductId: CreateLogicalProductId(name, publisher, installLocation, packageIdentity),
                DisplayName: name,
                Publisher: publisher,
                Version: version,
                PackageIdentity: packageIdentity,
                InstallLocation: installLocation,
                CanonicalKey: group.Key,
                Confidence: confidence,
                Evidence: evidence,
                Components: groupComponents
                    .Select(component => new SoftwareComponentAssociation(
                        component.Type.ToString(),
                        component.IdentityKey,
                        component.DisplayName,
                        GetPublisher(component),
                        GetVersion(component),
                        GetInstallLocation(component),
                        GetPackageIdentity(component)))
                    .ToArray());

            products.Add(record);
        }

        var sameNameGroups = componentList
            .GroupBy(component =>
                $"{NormalizeText(GetBestName([component]))}|{NormalizeText(GetBestPublisher([component]))}")
            .Where(group =>
                group
                    .Select(component => NormalizeText(GetBestInstallLocation([component])) )
                    .Where(value => !string.IsNullOrEmpty(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() > 1 ||
                group
                    .Select(component => NormalizeText(GetBestPackageIdentity([component])))
                    .Where(value => !string.IsNullOrEmpty(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() > 1)
            .ToArray();

        foreach (var group in sameNameGroups)
        {
            var componentIds = group
                .Select(component => component.IdentityKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var keyParts = group.Key.Split('|', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var productName = keyParts.Length > 0 ? keyParts[0] : "Unknown product";
            var publisherName = keyParts.Length > 1 ? keyParts[1] : null;

            conflicts.Add(new SoftwareIdentityConflict(
                ConflictId: CreateLogicalProductId(productName, publisherName, null, null),
                ProductName: productName,
                Message: "Multiple discovered install paths or package identities share the same display name and publisher. They are kept distinct unless a stronger package identity confirms equivalence.",
                ComponentIdentityKeys: componentIds));
        }

        return new SoftwareIdentityAssessment(
            Products: products
                .OrderBy(product => product.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(product => product.Publisher ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Conflicts: conflicts,
            Warnings: warnings
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static bool HasIdentityEvidence(InventoryComponent component)
    {
        return !string.IsNullOrWhiteSpace(GetBestName([component])) ||
               !string.IsNullOrWhiteSpace(GetBestPublisher([component])) ||
               !string.IsNullOrWhiteSpace(GetBestInstallLocation([component])) ||
               !string.IsNullOrWhiteSpace(GetBestPackageIdentity([component])) ||
               !string.IsNullOrWhiteSpace(GetExecutablePath(component));
    }

    private static string BuildCanonicalGroupKey(InventoryComponent component)
    {
        var publisher = NormalizeText(GetBestPublisher([component]));
        var executableStem = ExtractExecutableStem(component);
        var packageIdentity = NormalizeText(GetBestPackageIdentity([component]));
        var installLocation = NormalizeText(GetBestInstallLocation([component]));
        var name = NormalizeText(GetBestName([component]));

        if (!string.IsNullOrEmpty(publisher) && !string.IsNullOrEmpty(executableStem))
        {
            var seed = $"{publisher}|{executableStem}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash);
        }

        if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(publisher))
        {
            var seed = string.Join("|", new[] { name, publisher }.Where(value => !string.IsNullOrEmpty(value)));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash);
        }

        if (!string.IsNullOrEmpty(packageIdentity))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(packageIdentity));
            return Convert.ToHexString(hash);
        }

        if (!string.IsNullOrEmpty(installLocation))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(installLocation));
            return Convert.ToHexString(hash);
        }

        return NormalizeText(component.IdentityKey);
    }

    private static string ExtractExecutableStem(InventoryComponent component)
    {
        foreach (var value in new[]
                 {
                     GetExecutablePath(component),
                     GetInstallLocation(component),
                     GetDisplayName(component)
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var candidate = value.Trim();
            if (candidate.Contains('\\', StringComparison.Ordinal))
            {
                var fileName = Path.GetFileNameWithoutExtension(candidate);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return NormalizeText(fileName);
                }
            }

            if (candidate.Contains('.', StringComparison.Ordinal))
            {
                var trimmed = candidate.Split('.', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return NormalizeText(trimmed);
                }
            }

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return NormalizeText(candidate);
            }
        }

        return string.Empty;
    }

    private static string CreateLogicalProductId(string? name, string? publisher, string? installLocation, string? packageIdentity)
    {
        var parts = new[]
        {
            NormalizeText(name),
            NormalizeText(publisher),
            NormalizeText(installLocation),
            NormalizeText(packageIdentity)
        };

        var seed = string.Join("|", parts.Where(part => !string.IsNullOrEmpty(part)));
        if (string.IsNullOrWhiteSpace(seed))
        {
            return "unknown-product";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string GetBestName(InventoryComponent[] components)
    {
        return components
            .Select(GetDisplayName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeText)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
    }

    private static string GetBestPublisher(InventoryComponent[] components)
    {
        return components
            .Select(GetPublisher)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeText)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
    }

    private static string GetBestVersion(InventoryComponent[] components)
    {
        return components
            .Select(GetVersion)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeText)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
    }

    private static string GetBestPackageIdentity(InventoryComponent[] components)
    {
        return components
            .Select(GetPackageIdentity)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeText)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
    }

    private static string GetBestInstallLocation(InventoryComponent[] components)
    {
        return components
            .Select(GetInstallLocation)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeText)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
    }

    private static IEnumerable<SoftwareEvidence> GetEvidence(InventoryComponent component)
    {
        var entries = new List<SoftwareEvidence>();

        AddEvidence(entries, component.Source, "source", component.Source);
        AddEvidence(entries, component.Source, "displayName", GetDisplayName(component));
        AddEvidence(entries, component.Source, "publisher", GetPublisher(component));
        AddEvidence(entries, component.Source, "version", GetVersion(component));
        AddEvidence(entries, component.Source, "installLocation", GetInstallLocation(component));
        AddEvidence(entries, component.Source, "packageIdentity", GetPackageIdentity(component));
        AddEvidence(entries, component.Source, "executablePath", GetExecutablePath(component));

        foreach (var detail in component.Details)
        {
            if (string.IsNullOrWhiteSpace(detail.Key) || string.IsNullOrWhiteSpace(detail.Value))
            {
                continue;
            }

            AddEvidence(entries, component.Source, detail.Key, detail.Value);
        }

        return entries
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .GroupBy(item => $"{item.Source}|{item.EvidenceType}|{NormalizeText(item.Value)}")
            .Select(group => group.First())
            .ToArray();
    }

    private static void AddEvidence(List<SoftwareEvidence> entries, string source, string evidenceType, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        entries.Add(new SoftwareEvidence(source, evidenceType, NormalizeText(value), DateTimeOffset.UtcNow));
    }

    private static string GetDisplayName(InventoryComponent component)
    {
        var keys = new[]
        {
            "displayName",
            "applicationName",
            "name",
            "friendlyName",
            "productName"
        };

        foreach (var key in keys)
        {
            if (component.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return component.DisplayName.Trim();
    }

    private static string GetPublisher(InventoryComponent component)
    {
        var keys = new[] { "publisher", "vendor", "manufacturer", "creator" };
        foreach (var key in keys)
        {
            if (component.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string GetVersion(InventoryComponent component)
    {
        var keys = new[] { "version", "displayVersion", "fileVersion", "productVersion" };
        foreach (var key in keys)
        {
            if (component.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string GetPackageIdentity(InventoryComponent component)
    {
        var keys = new[]
        {
            "packageIdentity",
            "packageFamilyName",
            "productCode",
            "uninstallKeyName",
            "registryKeyPath",
            "appId",
            "productId"
        };

        foreach (var key in keys)
        {
            if (component.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string GetInstallLocation(InventoryComponent component)
    {
        var keys = new[] { "installLocation", "installPath", "targetPath", "path", "executablePath", "workingDirectory" };
        foreach (var key in keys)
        {
            if (component.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return NormalizeInstallPath(value.Trim());
            }
        }

        return string.Empty;
    }

    private static string GetExecutablePath(InventoryComponent component)
    {
        var keys = new[] { "executablePath", "path", "targetPath" };
        foreach (var key in keys)
        {
            if (component.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string NormalizeInstallPath(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(trimmed);
            return string.IsNullOrWhiteSpace(directory) ? trimmed : directory.Trim();
        }

        return trimmed;
    }

    private static SoftwareIdentityConfidence EvaluateConfidence(
        string name,
        string publisher,
        string packageIdentity,
        string installLocation,
        int evidenceCount)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SoftwareIdentityConfidence.Low;
        }

        var hasStrongIdentity = !string.IsNullOrWhiteSpace(publisher) ||
                                !string.IsNullOrWhiteSpace(packageIdentity) ||
                                !string.IsNullOrWhiteSpace(installLocation);

        if (hasStrongIdentity && evidenceCount >= 2)
        {
            return SoftwareIdentityConfidence.High;
        }

        if (hasStrongIdentity)
        {
            return SoftwareIdentityConfidence.Medium;
        }

        return SoftwareIdentityConfidence.Low;
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = Regex.Replace(value.Trim(), @"\s+", " ");
        return collapsed.Trim();
    }
}
