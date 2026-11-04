using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinForge.Application.Diagnostics;

public sealed record DiagnosticExportResult(
    string ExportDirectory,
    string HumanReadableReportPath,
    string StructuredReportPath,
    string ManifestPath,
    IReadOnlyList<string> IncludedFiles);

public sealed class DiagnosticExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public DiagnosticExportResult Export(
        string exportDirectory,
        ApplicationHealthReport applicationHealth,
        string? databasePath = null,
        string? installationRoot = null)
    {
        ArgumentNullException.ThrowIfNull(applicationHealth);

        var outputDirectory = Path.GetFullPath(exportDirectory);
        Directory.CreateDirectory(outputDirectory);

        var reportText = CreateHumanReadableReport(applicationHealth, databasePath, installationRoot);
        var reportPath = Path.Combine(outputDirectory, "winforge-diagnostics.txt");
        var structuredPath = Path.Combine(outputDirectory, "winforge-diagnostics.json");
        var manifestPath = Path.Combine(outputDirectory, "winforge-diagnostics-manifest.json");

        File.WriteAllText(reportPath, reportText, Encoding.UTF8);

        var structuredPayload = new
        {
            exportedAtUtc = DateTimeOffset.UtcNow,
            applicationHealth,
            databasePath,
            installationRoot,
            privacyNotice = "This export contains local application health, schema, and configuration metadata only. It does not include passwords, secrets, private documents, or arbitrary filesystem contents."
        };
        File.WriteAllText(structuredPath, JsonSerializer.Serialize(structuredPayload, JsonOptions), Encoding.UTF8);

        var files = new[] { reportPath, structuredPath };
        var manifestEntries = files
            .Select(file => new
            {
                path = Path.GetFileName(file),
                sizeBytes = new FileInfo(file).Length,
                sha256 = ComputeSha256(file)
            })
            .ToArray();

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { exportedAtUtc = DateTimeOffset.UtcNow, files = manifestEntries }, JsonOptions), Encoding.UTF8);

        var includedFiles = files
            .Select(file => Path.GetFileName(file) ?? string.Empty)
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .ToArray();

        return new DiagnosticExportResult(
            outputDirectory,
            reportPath,
            structuredPath,
            manifestPath,
            includedFiles);
    }

    private static string CreateHumanReadableReport(ApplicationHealthReport applicationHealth, string? databasePath, string? installationRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("WinForge diagnostics export");
        builder.AppendLine("===========================");
        builder.AppendLine($"Assessed at: {applicationHealth.AssessedAtUtc:O}");
        builder.AppendLine($"Overall state: {applicationHealth.OverallState}");
        builder.AppendLine($"Application version: {applicationHealth.AppVersion}");
        builder.AppendLine($"Runtime version: {applicationHealth.RuntimeVersion}");
        builder.AppendLine($"Application data directory: {applicationHealth.DataDirectory}");
        builder.AppendLine($"Database path: {databasePath ?? applicationHealth.DatabasePath}");
        builder.AppendLine($"Installation root: {installationRoot ?? "unavailable"}");
        builder.AppendLine();
        builder.AppendLine("Checks:");

        foreach (var check in applicationHealth.Checks)
        {
            builder.AppendLine($"- {check.Name}: {check.State} - {check.Message}");
            if (!string.IsNullOrWhiteSpace(check.RecommendedAction))
            {
                builder.AppendLine($"  Recommended action: {check.RecommendedAction}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Privacy notice: This export intentionally includes only local WinForge health and metadata. It does not include secrets, passwords, documents, or arbitrary user files.");
        return builder.ToString();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
