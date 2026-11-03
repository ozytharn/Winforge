using System.Security;
using System.Text;
using System.Xml;
using WinForge.Application.Recovery;
using WinForge.Domain.Recovery;

namespace WinForge.Infrastructure.Recovery;

public sealed class WindowsBackupInspector : IBackupInspector
{
    private const int MaximumRootEntries = 256;
    private const int MaximumBackupGroups = 32;
    private const int MaximumBackupSets = 64;
    private const int MaximumMetadataFiles = 32;
    private const long MaximumMetadataFileBytes = 1024 * 1024;
    private const long MaximumTotalMetadataBytes = 8 * 1024 * 1024;
    private const int MaximumContainerFiles = 64;
    private const string VhdxSignature = "vhdxfile";
    private const string VhdFooterSignature = "conectix";

    public async Task<BackupInspectionResult> InspectAsync(
        BackupCandidate candidate,
        Guid inspectionId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.Provider == BackupProvider.Unsupported)
        {
            return Result(
                candidate,
                inspectionId,
                startedAt,
                BackupInspectionState.Unsupported,
                "No read-only inspection adapter is implemented for this backup provider.",
                false,
                null,
                null,
                null,
                [Check("Format support", BackupCheckState.Skipped, "No supported adapter is available.", "WinForge")],
                ["The location was not opened because its provider is unsupported."]);
        }

        if (string.IsNullOrWhiteSpace(candidate.Location))
        {
            return Result(
                candidate,
                inspectionId,
                startedAt,
                BackupInspectionState.Inaccessible,
                "The candidate location was not available from Recovery discovery.",
                false,
                null,
                null,
                null,
                [Check("Candidate location", BackupCheckState.Inaccessible, "Discovery did not provide an accessible path.", "Recovery discovery")],
                ["A path was not available; no backup location was inspected."]);
        }

        try
        {
            var root = ValidateCandidateRoot(candidate);
            EnsureNotReparsePoint(root);
            return candidate.Provider switch
            {
                BackupProvider.WindowsImageBackup =>
                    await InspectWindowsImageBackupAsync(candidate, root, inspectionId, startedAt, cancellationToken)
                        .ConfigureAwait(false),
                BackupProvider.FileHistory =>
                    await InspectFileHistoryAsync(candidate, root, inspectionId, startedAt, cancellationToken)
                        .ConfigureAwait(false),
                _ => Result(
                    candidate,
                    inspectionId,
                    startedAt,
                    BackupInspectionState.Unsupported,
                    "No read-only inspection adapter is implemented for this backup provider.",
                    false,
                    null,
                    null,
                    null,
                    [],
                    ["The location was not opened because its provider is unsupported."])
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return Inaccessible(candidate, inspectionId, startedAt, exception.Message);
        }
        catch (SecurityException exception)
        {
            return Inaccessible(candidate, inspectionId, startedAt, exception.Message);
        }
        catch (IOException exception)
        {
            return Inaccessible(candidate, inspectionId, startedAt, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Inaccessible(candidate, inspectionId, startedAt, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return Inaccessible(candidate, inspectionId, startedAt, exception.Message);
        }
    }

    private static async Task<BackupInspectionResult> InspectWindowsImageBackupAsync(
        BackupCandidate candidate,
        string root,
        Guid inspectionId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var checks = new List<BackupInspectionCheck>();
        var limitations = new List<string>
        {
            "The Windows image format is not mounted. Full VHD/VHDX payloads, differencing chains, restore catalogs, and restoreability are not validated.",
            "Large backup containers are not hashed. Their observed file lengths are not a measure of used data or recoverable content."
        };
        var metadataBytes = 0L;
        var containerBytes = 0L;
        var groupCount = 0;
        var validManifestCount = 0;
        var validContainerCount = 0;
        var failedCheck = false;
        var truncated = false;
        var rootEntries = EnumerateEntries(root, MaximumRootEntries, cancellationToken, out truncated);
        if (truncated)
        {
            limitations.Add($"The root entry limit ({MaximumRootEntries}) was reached; some candidates were not inspected.");
        }

        var backupDirectories = new List<string>();
        foreach (var path in rootEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetDirectory(path))
            {
                continue;
            }

            if (backupDirectories.Count == MaximumBackupGroups)
            {
                truncated = true;
                limitations.Add($"The backup group limit ({MaximumBackupGroups}) was reached; additional groups were not inspected.");
                break;
            }

            EnsureNotReparsePoint(path);
            groupCount++;
            backupDirectories.Add(path);
        }

        checks.Add(Check(
            "WindowsImageBackup structure",
            backupDirectories.Count > 0 ? BackupCheckState.Passed : BackupCheckState.Warning,
            backupDirectories.Count > 0
                ? $"Found {backupDirectories.Count} immediate backup directory group(s); these are not asserted to be backup-set IDs."
                : "No immediate backup directory groups were found.",
            "WindowsImageBackup bounded enumeration"));

        var metadataFilesSeen = 0;
        var containerFilesSeen = 0;
        var backupSetsSeen = 0;
        var backupSetLimitReached = false;
        async Task InspectManifestAsync(string path)
        {
            if (metadataFilesSeen >= MaximumMetadataFiles)
            {
                truncated = true;
                return;
            }

            metadataFilesSeen++;
            if (!IsWithinMetadataBudget(path, metadataBytes))
            {
                truncated = true;
                checks.Add(Check(
                    "Manifest size bound",
                    BackupCheckState.Skipped,
                    "The manifest was not opened because the per-file or total metadata-byte limit would be exceeded.",
                    path));
                return;
            }

            var read = await ValidateXmlAsync(path, cancellationToken).ConfigureAwait(false);
            metadataBytes += read.BytesRead;
            AddManifestResult(checks, read, path);
            validManifestCount += read.IsValid ? 1 : 0;
            failedCheck |= !read.IsValid;
        }

        async Task InspectBackupFileAsync(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(path), "BackupSpecs.xml", StringComparison.OrdinalIgnoreCase))
            {
                await InspectManifestAsync(path).ConfigureAwait(false);
                return;
            }

            if (!IsVhdContainer(path))
            {
                return;
            }

            if (containerFilesSeen >= MaximumContainerFiles)
            {
                truncated = true;
                return;
            }

            containerFilesSeen++;
            var length = new FileInfo(path).Length;
            containerBytes = checked(containerBytes + length);
            var valid = await ValidateVirtualDiskHeaderAsync(path, cancellationToken).ConfigureAwait(false);
            checks.Add(Check(
                $"Container header: {Path.GetFileName(path)}",
                valid ? BackupCheckState.Passed : BackupCheckState.Failed,
                valid
                    ? $"The {Path.GetExtension(path)} signature was recognized; container contents were not read."
                    : "The expected virtual-disk signature was not present or the file was too short.",
                "Read-only bounded header read"));
            validContainerCount += valid ? 1 : 0;
            failedCheck |= !valid;
        }

        foreach (var backupDirectory in backupDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = EnumerateEntries(backupDirectory, MaximumContainerFiles, cancellationToken, out var groupTruncated);
            if (groupTruncated)
            {
                truncated = true;
                limitations.Add($"The entry limit ({MaximumContainerFiles}) was reached under '{Path.GetFileName(backupDirectory)}'.");
            }

            foreach (var path in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetDirectory(path))
                {
                    if (string.Equals(Path.GetFileName(path), "Catalog", StringComparison.OrdinalIgnoreCase))
                    {
                        EnsureNotReparsePoint(path);
                        var catalogEntries = EnumerateEntries(path, MaximumMetadataFiles, cancellationToken, out var catalogTruncated);
                        truncated |= catalogTruncated;
                        foreach (var catalogFile in catalogEntries.Where(IsMetadataFile))
                        {
                            await InspectManifestAsync(catalogFile).ConfigureAwait(false);
                        }
                    }
                    else if (Path.GetFileName(path).StartsWith("Backup", StringComparison.OrdinalIgnoreCase))
                    {
                        if (backupSetsSeen >= MaximumBackupSets)
                        {
                            truncated = true;
                            limitations.Add($"The backup set limit ({MaximumBackupSets}) was reached; additional sets were not inspected.");
                            backupSetLimitReached = true;
                            break;
                        }

                        backupSetsSeen++;
                        EnsureNotReparsePoint(path);
                        var setEntries = EnumerateEntries(
                            path,
                            MaximumContainerFiles,
                            cancellationToken,
                            out var setTruncated);
                        truncated |= setTruncated;
                        foreach (var setEntry in setEntries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!TryGetDirectory(setEntry))
                            {
                                await InspectBackupFileAsync(setEntry).ConfigureAwait(false);
                            }
                        }
                    }

                    continue;
                }

                await InspectBackupFileAsync(path).ConfigureAwait(false);
            }

            if (backupSetLimitReached)
            {
                break;
            }
        }

        if (metadataBytes > MaximumTotalMetadataBytes)
        {
            truncated = true;
        }

        var recognized = backupDirectories.Count > 0 &&
                         (validManifestCount > 0 || containerFilesSeen > 0);
        if (validManifestCount == 0)
        {
            checks.Add(Check(
                "Backup metadata",
                BackupCheckState.Skipped,
                "No bounded, supported XML manifest was available to validate.",
                "WindowsImageBackup metadata inspection"));
        }

        if (containerFilesSeen == 0)
        {
            checks.Add(Check(
                "Backup container headers",
                BackupCheckState.Skipped,
                "No directly enumerated VHD/VHDX container was found in the recognized backup directory groups.",
                "WindowsImageBackup bounded file inventory"));
        }

        checks.Add(Check(
            "Manifest file references",
            BackupCheckState.Skipped,
            "No schema-aware parser is implemented to resolve every Windows image manifest reference against its complete backup set.",
            "WinForge bounded inspection"));

        var state = failedCheck
            ? validManifestCount > 0 || validContainerCount > 0
                ? BackupInspectionState.Warning
                : BackupInspectionState.Failed
            : truncated
                ? BackupInspectionState.Incomplete
                : !recognized || validManifestCount == 0 || validContainerCount == 0
                    ? BackupInspectionState.Incomplete
                    : BackupInspectionState.Verified;
        var summary = state switch
        {
            BackupInspectionState.Verified =>
                $"Available Windows image metadata and {validContainerCount} bounded container header check(s) passed. This does not establish full backup integrity or restoreability.",
            BackupInspectionState.Warning =>
                "Some metadata or container checks passed and at least one check failed; the backup set is not fully validated.",
            BackupInspectionState.Failed =>
                "A supported metadata or container check failed.",
            _ => "The expected Windows image metadata or containers were incomplete, unsupported, or beyond the inspection bounds."
        };

        if (truncated)
        {
            limitations.Add("One or more metadata or directory bounds were reached; the inspection is incomplete.");
        }

        return Result(
            candidate,
            inspectionId,
            startedAt,
            state,
            summary,
            recognized,
            groupCount,
            metadataBytes,
            containerBytes,
            checks,
            limitations);
    }

    private static async Task<BackupInspectionResult> InspectFileHistoryAsync(
        BackupCandidate candidate,
        string root,
        Guid inspectionId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        const int maximumUsers = 64;
        const int maximumComputersPerUser = 32;
        var checks = new List<BackupInspectionCheck>();
        var limitations = new List<string>
        {
            "File History payload versions and catalog indexes are not exhaustively enumerated or validated.",
            "Configuration metadata is not proof that file contents are complete or restorable."
        };
        var metadataBytes = 0L;
        var profileCount = 0;
        var validConfigurationCount = 0;
        var failedConfigurationCount = 0;
        var metadataFilesSeen = 0;
        var truncated = false;
        var userDirectories = EnumerateDirectories(root, maximumUsers, cancellationToken, out var usersTruncated);
        truncated |= usersTruncated;

        foreach (var userDirectory in userDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(userDirectory);
            var computerDirectories = EnumerateDirectories(
                userDirectory,
                maximumComputersPerUser,
                cancellationToken,
                out var computersTruncated);
            truncated |= computersTruncated;

            foreach (var computerDirectory in computerDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNotReparsePoint(computerDirectory);
                profileCount++;
                var configuration = Path.Combine(computerDirectory, "Configuration");
                if (!TryGetDirectory(configuration))
                {
                    continue;
                }

                EnsureNotReparsePoint(configuration);
                foreach (var name in new[] { "Config1.xml", "Config2.xml" })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Path.Combine(configuration, name);
                    if (!TryGetFile(path))
                    {
                        continue;
                    }

                    if (metadataFilesSeen >= MaximumMetadataFiles)
                    {
                        truncated = true;
                        break;
                    }

                    metadataFilesSeen++;
                    if (!IsWithinMetadataBudget(path, metadataBytes))
                    {
                        truncated = true;
                        checks.Add(Check(
                            "Configuration size bound",
                            BackupCheckState.Skipped,
                            "The configuration was not opened because the per-file or total metadata-byte limit would be exceeded.",
                            "File History Configuration directory"));
                        continue;
                    }

                    var read = await ValidateXmlAsync(path, cancellationToken).ConfigureAwait(false);
                    metadataBytes += read.BytesRead;
                    if (read.IsValid)
                    {
                        validConfigurationCount++;
                        checks.Add(Check(
                            $"Configuration manifest: {name}",
                            BackupCheckState.Passed,
                            $"The bounded XML document is well-formed (root element '{read.RootElement}'); payload data was not read.",
                            "File History Configuration directory"));
                    }
                    else
                    {
                        failedConfigurationCount++;
                        checks.Add(Check(
                            $"Configuration manifest: {name}",
                            BackupCheckState.Failed,
                            read.Error ?? "The configuration metadata could not be validated.",
                            "File History Configuration directory"));
                    }
                }

                if (metadataFilesSeen >= MaximumMetadataFiles)
                {
                    truncated = true;
                    break;
                }
            }

            if (metadataFilesSeen >= MaximumMetadataFiles)
            {
                break;
            }
        }

        var recognized = profileCount > 0 && validConfigurationCount > 0;
        checks.Insert(0, Check(
            "File History profile structure",
            recognized ? BackupCheckState.Passed : BackupCheckState.Warning,
            recognized
                ? $"Found {profileCount} bounded user/computer directory group(s) and readable configuration metadata."
                : profileCount > 0
                    ? $"Found {profileCount} directory group(s), but no supported readable Config1.xml or Config2.xml was found."
                    : "No user/computer directory groups were found.",
            "FileHistory bounded structure inspection"));
        checks.Add(Check(
            "File History payload integrity",
            BackupCheckState.Skipped,
            "No supported non-elevated integrity interface is implemented for the versioned payload and catalog format.",
            "WinForge"));

        if (truncated)
        {
            limitations.Add("The bounded user/computer directory limit was reached; some profiles may not be represented.");
        }

        var state = failedConfigurationCount > 0
            ? validConfigurationCount > 0
                ? BackupInspectionState.Warning
                : BackupInspectionState.Failed
            : truncated || !recognized
                ? BackupInspectionState.Incomplete
                : BackupInspectionState.Warning;
        var summary = state switch
        {
            BackupInspectionState.Warning when recognized =>
                "File History configuration metadata is readable, but payload/index integrity is not verified.",
            BackupInspectionState.Warning =>
                "Some File History metadata checks failed; payload/index integrity is not verified.",
            BackupInspectionState.Failed =>
                "File History configuration metadata was malformed or unreadable.",
            _ => "File History structure or metadata was incomplete within the bounded inspection scope."
        };

        return Result(
            candidate,
            inspectionId,
            startedAt,
            state,
            summary,
            recognized,
            profileCount,
            metadataBytes,
            null,
            checks,
            limitations);
    }

    private static async Task<XmlValidation> ValidateXmlAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotReparsePoint(path);
        var file = new FileInfo(path);
        var length = file.Length;
        if (length <= 0 || length > MaximumMetadataFileBytes)
        {
            return new XmlValidation(
                false,
                0,
                null,
                $"Metadata file size {length:N0} bytes is empty or exceeds the {MaximumMetadataFileBytes:N0}-byte inspection limit.");
        }

        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumMetadataFileBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = XmlReader.Create(stream, settings);
            string? rootElement = null;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rootElement is null && reader.NodeType == XmlNodeType.Element)
                {
                    rootElement = reader.LocalName;
                }
            }

            return rootElement is null
                ? new XmlValidation(false, stream.Position, null, "The XML document had no root element.")
                : new XmlValidation(true, stream.Position, rootElement, null);
        }
        catch (XmlException exception)
        {
            return new XmlValidation(false, Math.Min(length, MaximumMetadataFileBytes), null, exception.Message);
        }
    }

    private static async Task<bool> ValidateVirtualDiskHeaderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotReparsePoint(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            512,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length < 512)
        {
            return false;
        }

        var signature = new byte[8];
        if (string.Equals(Path.GetExtension(path), ".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            var bytesRead = await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false);
            return bytesRead == signature.Length &&
                   Encoding.ASCII.GetString(signature).Equals(VhdxSignature, StringComparison.Ordinal);
        }

        stream.Seek(-512, SeekOrigin.End);
        var footerBytesRead = await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false);
        return footerBytesRead == signature.Length &&
               Encoding.ASCII.GetString(signature).Equals(VhdFooterSignature, StringComparison.Ordinal);
    }

    private static string ValidateCandidateRoot(BackupCandidate candidate)
    {
        var fullPath = Path.GetFullPath(candidate.Location!);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathRooted(fullPath))
        {
            throw new ArgumentException("Only local, rooted backup candidate locations can be inspected.");
        }

        var leaf = candidate.Provider == BackupProvider.WindowsImageBackup
            ? "WindowsImageBackup"
            : "FileHistory";
        var expectedIdentity = $"backup-folder:{Path.GetPathRoot(fullPath)!.TrimEnd('\\', ':')}:{leaf.ToUpperInvariant()}";
        if (!string.Equals(Path.GetFileName(fullPath), leaf, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Identity, expectedIdentity, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The candidate identity does not match the expected local '{leaf}' root.");
        }

        var current = new DirectoryInfo(fullPath);
        while (current is not null)
        {
            EnsureNotReparsePoint(current.FullName);
            current = current.Parent;
        }

        return fullPath;
    }

    private static string[] EnumerateEntries(
        string directory,
        int maximum,
        CancellationToken cancellationToken,
        out bool truncated)
    {
        var results = new List<string>(Math.Min(maximum, 64));
        truncated = false;
        using var enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        while (results.Count < maximum)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                return results.ToArray();
            }

            results.Add(enumerator.Current);
        }

        cancellationToken.ThrowIfCancellationRequested();
        truncated = enumerator.MoveNext();
        return results.ToArray();
    }

    private static string[] EnumerateDirectories(
        string directory,
        int maximum,
        CancellationToken cancellationToken,
        out bool truncated)
    {
        var entries = EnumerateEntries(directory, maximum * 4, cancellationToken, out truncated);
        var directories = new List<string>(Math.Min(maximum, 32));
        foreach (var path in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetDirectory(path))
            {
                continue;
            }

            directories.Add(path);
            if (directories.Count == maximum)
            {
                truncated = true;
                break;
            }
        }

        return directories.ToArray();
    }

    private static bool TryGetDirectory(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetFile(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Inspection stopped because a candidate path is a reparse point.");
        }
    }

    private static bool IsMetadataFile(string path) =>
        string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinMetadataBudget(string path, long alreadyRead)
    {
        var length = new FileInfo(path).Length;
        return length is > 0 and <= MaximumMetadataFileBytes &&
               alreadyRead <= MaximumTotalMetadataBytes - length;
    }

    private static bool IsVhdContainer(string path) =>
        string.Equals(Path.GetExtension(path), ".vhd", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".vhdx", StringComparison.OrdinalIgnoreCase);

    private static void AddManifestResult(
        ICollection<BackupInspectionCheck> checks,
        XmlValidation validation,
        string path) =>
        checks.Add(Check(
            $"Manifest: {Path.GetFileName(path)}",
            validation.IsValid ? BackupCheckState.Passed : BackupCheckState.Failed,
            validation.IsValid
                ? $"The bounded XML document is well-formed (root element '{validation.RootElement}'); its backup references were not interpreted."
                : validation.Error ?? "The manifest could not be validated.",
            path));

    private static BackupInspectionResult Inaccessible(
        BackupCandidate candidate,
        Guid inspectionId,
        DateTimeOffset startedAt,
        string message) =>
        Result(
            candidate,
            inspectionId,
            startedAt,
            BackupInspectionState.Inaccessible,
            "The candidate could not be read within the supported inspection scope.",
            false,
            null,
            null,
            null,
            [Check("Read access", BackupCheckState.Inaccessible, message, "Windows file access")],
            ["Access failure is not evidence that the backup is absent or invalid."]);

    private static BackupInspectionResult Result(
        BackupCandidate candidate,
        Guid inspectionId,
        DateTimeOffset startedAt,
        BackupInspectionState state,
        string summary,
        bool recognized,
        int? groups,
        long? metadataBytes,
        long? containerBytes,
        IReadOnlyList<BackupInspectionCheck> checks,
        IReadOnlyList<string> limitations) =>
        new(
            inspectionId,
            candidate.Identity,
            candidate.Provider,
            candidate.Location,
            startedAt,
            DateTimeOffset.UtcNow,
            state,
            summary,
            recognized,
            groups,
            metadataBytes,
            containerBytes,
            checks,
            limitations);

    private static BackupInspectionCheck Check(
        string name,
        BackupCheckState state,
        string explanation,
        string source) =>
        new(name, state, explanation, source, DateTimeOffset.UtcNow);

    private sealed record XmlValidation(bool IsValid, long BytesRead, string? RootElement, string? Error);
}
