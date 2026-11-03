using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;
using WmiEnumerationOptions = System.Management.EnumerationOptions;

namespace WinForge.Infrastructure.Collectors;

public sealed class RecoveryDiscoveryCollector : IInventoryCollector
{
    private const string RecoveryPartitionType = "de94bba4-06d1-4d40-a16a-bfd50179d6ac";
    private const int MaximumRecoveryEvents = 50;
    private readonly ILogger<RecoveryDiscoveryCollector> _logger;

    public RecoveryDiscoveryCollector(ILogger<RecoveryDiscoveryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "recovery-discovery";

    public InventoryScanGroup ScanGroup => InventoryScanGroup.Recovery;

    public async Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var components = new List<InventoryComponent>();
        var issues = new List<CollectorIssue>();
        if (!OperatingSystem.IsWindows())
        {
            return new CollectorResult(
                CollectorId,
                components,
                [new CollectorIssue(CollectorId, "unsupported_platform", "Recovery discovery is only available on Windows.")]);
        }

        await CollectWindowsRecoveryEnvironmentAsync(components, issues, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        CollectSystemRestore(components, issues, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CollectStorageAndBackupCandidates(components, issues, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CollectRecoveryPartitions(components, issues, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CollectBitLockerStatus(components, issues, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CollectRecoveryServices(components, issues, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CollectRecoveryEvents(components, issues, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CollectWindowsBootConfiguration(components, issues, cancellationToken);

        return new CollectorResult(CollectorId, components, issues);
    }

    private async Task CollectWindowsRecoveryEnvironmentAsync(
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "reagentc.exe");
        if (!File.Exists(executable))
        {
            components.Add(CreateItem(
                InventoryComponentType.RecoveryEnvironment,
                "winre",
                "Windows Recovery Environment",
                "Windows RE status",
                InventoryFieldAvailability.Unsupported,
                "REAgentC (reagentc.exe)",
                "The Windows recovery configuration utility is not present.",
                "Unavailable",
                "No configuration was changed.",
                "The Windows Recovery Environment status could not be queried."));
            issues.Add(new CollectorIssue(
                CollectorId,
                "reagentc_unavailable",
                "The Windows Recovery Environment utility is unavailable."));
            return;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "/info",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
                }
            };
            if (!process.Start())
            {
                throw new InvalidOperationException("REAgentC did not start.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException("The read-only REAgentC query exceeded its time limit.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var output = await standardOutput.ConfigureAwait(false);
            var errorOutput = await standardError.ConfigureAwait(false);
            var status = ParseWinReStatus(output);
            var location = ParseWinReValue(output, "Windows RE location");
            var bcdIdentifier = ParseWinReValue(output, "Boot Configuration Data (BCD) identifier");
            var imageLocation = ParseWinReValue(output, "Recovery image location");
            var customImageLocation = ParseWinReValue(output, "Custom image location");
            var availability = status switch
            {
                "Enabled" or "Disabled" => InventoryFieldAvailability.Available,
                _ => process.ExitCode == 0
                    ? InventoryFieldAvailability.Unavailable
                    : InventoryFieldAvailability.Inaccessible
            };
            var warnings = availability == InventoryFieldAvailability.Available
                ? null
                : process.ExitCode == 0
                    ? "The command completed, but its localized output could not be interpreted."
                    : "Windows did not return recovery configuration; access may require elevation.";
            var details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["windowsReStatus"] = status,
                ["windowsReLocation"] = location,
                ["bcdIdentifier"] = bcdIdentifier,
                ["recoveryImageLocation"] = imageLocation,
                ["customImageLocation"] = customImageLocation,
                ["commandExitCode"] = process.ExitCode.ToString(CultureInfo.InvariantCulture),
                ["stderrReported"] = string.IsNullOrWhiteSpace(errorOutput) ? "false" : "true"
            };
            var winReComponent = CreateDetailedItem(
                InventoryComponentType.RecoveryEnvironment,
                "winre",
                "Windows Recovery Environment",
                availability == InventoryFieldAvailability.Available ? status! : "Status unavailable",
                "REAgentC /info (read-only)",
                availability,
                details,
                availability == InventoryFieldAvailability.Available ? "Source-reported" : "Unavailable",
                warnings,
                "REAgentC output is localized and may omit fields on some Windows builds. A status query does not validate that recovery can complete.");
            var winReAvailability = winReComponent.FieldAvailability.ToDictionary(
                field => field.Key,
                field => field.Value,
                StringComparer.Ordinal);
            winReAvailability["windowsReStatus"] = availability;
            components.Add(winReComponent with { FieldAvailability = winReAvailability });
            if (availability != InventoryFieldAvailability.Available)
            {
                issues.Add(new CollectorIssue(
                    CollectorId,
                    availability == InventoryFieldAvailability.Inaccessible
                        ? "winre_status_inaccessible"
                        : "winre_status_unavailable",
                    warnings ?? "Windows Recovery Environment status was unavailable."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            _logger.LogWarning(exception, "Windows Recovery Environment discovery timed out.");
            components.Add(CreateItem(
                InventoryComponentType.RecoveryEnvironment,
                "winre",
                "Windows Recovery Environment",
                "Status unavailable",
                InventoryFieldAvailability.Unavailable,
                "REAgentC /info (read-only)",
                exception.Message,
                "Low",
                "A timed-out query does not indicate that WinRE is disabled.",
                "The Windows recovery configuration query did not finish."));
            issues.Add(new CollectorIssue(CollectorId, "winre_query_timeout", "The Windows recovery status query timed out."));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "Windows Recovery Environment discovery failed.");
            components.Add(CreateItem(
                InventoryComponentType.RecoveryEnvironment,
                "winre",
                "Windows Recovery Environment",
                "Status unavailable",
                exception is System.ComponentModel.Win32Exception
                    ? InventoryFieldAvailability.Inaccessible
                    : InventoryFieldAvailability.Unavailable,
                "REAgentC /info (read-only)",
                "The Windows recovery configuration query failed.",
                "Unavailable",
                "A query failure is not evidence that WinRE is missing.",
                "Windows did not provide the requested status."));
            issues.Add(new CollectorIssue(
                CollectorId,
                "winre_query_failed",
                "Windows Recovery Environment status could not be read."));
        }
    }

    private void CollectSystemRestore(
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        var restorePointsFound = false;
        try
        {
            foreach (var row in QueryWmi(@"\\.\root\default", "SELECT * FROM SystemRestore"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (row)
                {
                    restorePointsFound = true;
                    var sequence = ReadWmiString(row, "SequenceNumber");
                    var description = ReadWmiString(row, "Description");
                    var created = ReadWmiString(row, "CreationTime");
                    var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["sequenceNumber"] = sequence,
                        ["description"] = description,
                        ["creationTime"] = ParseWmiDateTime(created),
                        ["restorePointType"] = ReadWmiString(row, "RestorePointType"),
                        ["eventType"] = ReadWmiString(row, "EventType"),
                        ["scope"] = "System Restore checkpoint"
                    };
                    components.Add(CreateDetailedItem(
                        InventoryComponentType.RestorePoint,
                        $"restore-point:{sequence ?? Guid.NewGuid().ToString("N")}",
                        string.IsNullOrWhiteSpace(description) ? $"Restore point {sequence ?? "unknown"}" : description,
                        "Discovered; restore was not tested",
                        "SystemRestore WMI class (root\\default)",
                        InventoryFieldAvailability.Available,
                        details,
                        "Source-reported",
                        "A restore point's presence does not establish that it is complete, accessible, or sufficient for a recovery.",
                        "Windows System Restore points are not full disk images or personal-file backups."));
                }
            }

            if (!restorePointsFound)
            {
                components.Add(CreateItem(
                    InventoryComponentType.RestorePoint,
                    "restore-point-catalog",
                    "System Restore points",
                    "No restore points reported",
                    InventoryFieldAvailability.Missing,
                    "SystemRestore WMI class (root\\default)",
                    null,
                    "Source query completed",
                    "An empty catalog can result from disabled protection, retention, or provider behavior.",
                    "No restore points were returned by the local WMI provider."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsWmiAccessFailure(exception))
        {
            AddUnavailableSource(
                components,
                issues,
                "restore-point-catalog",
                "System Restore points",
                "SystemRestore WMI class (root\\default)",
                exception,
                "restore_points");
        }

        try
        {
            var configFound = false;
            foreach (var row in QueryWmi(@"\\.\root\default", "SELECT * FROM SystemRestoreConfig"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (row)
                {
                    configFound = true;
                    var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["sessionIntervalSeconds"] = ReadWmiString(row, "RPSessionInterval"),
                        ["globalIntervalSeconds"] = ReadWmiString(row, "RPGlobalInterval"),
                        ["restorePointLifetimeSeconds"] = ReadWmiString(row, "RPLifeInterval"),
                        ["maximumDiskPercent"] = ReadWmiString(row, "DiskPercent")
                    };
                    components.Add(CreateDetailedItem(
                        InventoryComponentType.RecoveryEnvironment,
                        "system-restore-configuration",
                        "System Restore configuration",
                        "Configuration reported",
                        "SystemRestoreConfig WMI class (root\\default)",
                        InventoryFieldAvailability.Available,
                        details,
                        "Source-reported",
                        null,
                        "Configuration values do not prove that volume protection is enabled or a restore will succeed."));
                }
            }

            if (!configFound)
            {
                components.Add(CreateItem(
                    InventoryComponentType.RecoveryEnvironment,
                    "system-restore-configuration",
                    "System Restore configuration",
                    "Configuration unavailable",
                    InventoryFieldAvailability.Unavailable,
                    "SystemRestoreConfig WMI class (root\\default)",
                    "The provider returned no configuration instance.",
                    "Unavailable",
                    "An unavailable configuration is not evidence that System Restore is disabled.",
                    "The provider returned no SystemRestoreConfig instance."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsWmiAccessFailure(exception))
        {
            AddUnavailableSource(
                components,
                issues,
                "system-restore-configuration",
                "System Restore configuration",
                "SystemRestoreConfig WMI class (root\\default)",
                exception,
                "system_restore_configuration");
        }
    }

    private void CollectStorageAndBackupCandidates(
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        var drivesFound = false;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    drivesFound = true;
                    var total = drive.TotalSize;
                    var free = drive.AvailableFreeSpace;
                    var root = drive.RootDirectory.FullName;
                    var volumeDetails = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["driveName"] = drive.Name,
                        ["volumeLabel"] = SafeVolumeLabel(drive),
                        ["driveType"] = drive.DriveType.ToString(),
                        ["fileSystem"] = SafeFileSystem(drive),
                        ["totalBytes"] = total.ToString(CultureInfo.InvariantCulture),
                        ["availableFreeBytes"] = free.ToString(CultureInfo.InvariantCulture),
                        ["freePercent"] = total > 0
                            ? (free * 100d / total).ToString("F1", CultureInfo.InvariantCulture)
                            : null,
                        ["isRecoveryVolumeByLabel"] =
                            string.Equals(SafeVolumeLabel(drive), "Recovery", StringComparison.OrdinalIgnoreCase)
                                ? "true"
                                : "false"
                    };
                    components.Add(CreateDetailedItem(
                        InventoryComponentType.RecoveryVolume,
                        $"volume:{drive.Name.TrimEnd('\\', ':').ToUpperInvariant()}",
                        $"{drive.Name} {SafeVolumeLabel(drive)}".Trim(),
                        "Accessible volume; capacity reported",
                        "System.IO.DriveInfo",
                        InventoryFieldAvailability.Available,
                        volumeDetails,
                        "Direct volume query",
                        null,
                        "Free space is a point-in-time value and does not by itself identify reserved recovery capacity."));

                    foreach (var candidateDirectory in new[] { "WindowsImageBackup", "FileHistory" })
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var candidatePath = Path.Combine(root, candidateDirectory);
                        try
                        {
                            var attributes = File.GetAttributes(candidatePath);
                            if ((attributes & FileAttributes.Directory) == 0)
                            {
                                continue;
                            }
                        }
                        catch (DirectoryNotFoundException)
                        {
                            continue;
                        }
                        catch (FileNotFoundException)
                        {
                            continue;
                        }
                        catch (Exception exception) when (
                            exception is UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            _logger.LogDebug(exception, "Backup candidate {CandidatePath} denied access.", candidatePath);
                            components.Add(CreateItem(
                                InventoryComponentType.BackupCatalogItem,
                                $"backup-folder:{drive.Name.TrimEnd('\\', ':').ToUpperInvariant()}:{candidateDirectory.ToUpperInvariant()}",
                                candidateDirectory == "WindowsImageBackup"
                                    ? "Windows system image backup candidate"
                                    : "File History backup candidate",
                                "Candidate status inaccessible",
                                InventoryFieldAvailability.Inaccessible,
                                "Read-only volume root directory query",
                                "Windows denied access; absence cannot be determined.",
                                "Unknown",
                                "An inaccessible candidate must not be interpreted as a missing backup.",
                                "Candidate contents were not read."));
                            issues.Add(new CollectorIssue(
                                CollectorId,
                                "backup_candidate_inaccessible",
                                "A backup folder candidate could not be inspected."));
                            continue;
                        }
                        catch (IOException exception)
                        {
                            _logger.LogDebug(exception, "Backup candidate {CandidatePath} could not be inspected.", candidatePath);
                            components.Add(CreateItem(
                                InventoryComponentType.BackupCatalogItem,
                                $"backup-folder:{drive.Name.TrimEnd('\\', ':').ToUpperInvariant()}:{candidateDirectory.ToUpperInvariant()}",
                                candidateDirectory == "WindowsImageBackup"
                                    ? "Windows system image backup candidate"
                                    : "File History backup candidate",
                                "Candidate status unavailable",
                                InventoryFieldAvailability.Unavailable,
                                "Read-only volume root directory query",
                                "The directory query failed; absence cannot be determined.",
                                "Unknown",
                                "A failed query must not be interpreted as a missing backup.",
                                "Candidate contents were not read."));
                            issues.Add(new CollectorIssue(
                                CollectorId,
                                "backup_candidate_unavailable",
                                "A backup folder candidate could not be inspected."));
                            continue;
                        }

                        components.Add(CreateItem(
                            InventoryComponentType.BackupCatalogItem,
                            $"backup-folder:{drive.Name.TrimEnd('\\', ':').ToUpperInvariant()}:{candidateDirectory.ToUpperInvariant()}",
                            candidateDirectory == "WindowsImageBackup"
                                ? "Windows system image backup candidate"
                                : "File History backup candidate",
                            "Folder detected; backup not verified",
                            InventoryFieldAvailability.Available,
                            "Read-only volume root directory check",
                            null,
                            "Folder presence only",
                            "The backup contents, completeness, integrity, date, and restoreability have not been checked.",
                            candidatePath));
                    }
                }
                catch (UnauthorizedAccessException exception)
                {
                    _logger.LogDebug(exception, "Recovery volume {DriveName} denied access.", drive.Name);
                    components.Add(CreateItem(
                        InventoryComponentType.RecoveryVolume,
                        $"volume:{drive.Name.TrimEnd('\\', ':').ToUpperInvariant()}",
                        drive.Name,
                        "Volume capacity inaccessible",
                        InventoryFieldAvailability.Inaccessible,
                        "System.IO.DriveInfo",
                        "Windows denied access to volume capacity metadata.",
                        "Unknown",
                        "An inaccessible volume is not missing.",
                        "Capacity information could not be read."));
                    issues.Add(new CollectorIssue(CollectorId, "volume_capacity_inaccessible", "Windows denied access to a volume's capacity metadata."));
                }
                catch (IOException exception)
                {
                    _logger.LogDebug(exception, "Recovery volume {DriveName} changed during collection.", drive.Name);
                    issues.Add(new CollectorIssue(CollectorId, "volume_changed", "A volume became unavailable during recovery discovery."));
                }
            }

            if (!drivesFound)
            {
                components.Add(CreateItem(
                    InventoryComponentType.RecoveryVolume,
                    "available-volume-catalog",
                    "Mounted volume capacity",
                    "No ready volumes reported",
                    InventoryFieldAvailability.Missing,
                    "System.IO.DriveInfo",
                    null,
                    "Source query completed",
                    "Volumes without mount points are not included here; see the partition inventory.",
                    "Windows reported no ready mounted volumes."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(exception, "Mounted volume discovery failed.");
            AddUnavailableSource(
                components,
                issues,
                "available-volume-catalog",
                "Mounted volume capacity",
                "System.IO.DriveInfo",
                exception,
                "volume_catalog");
        }

        components.Add(CreateItem(
            InventoryComponentType.BackupCatalogItem,
            "windows-backup-version-catalog",
            "Windows Backup version catalog",
            "Not queried; elevated access is required",
            InventoryFieldAvailability.Inaccessible,
            "wbadmin get versions (not invoked)",
            "The documented Windows Backup version listing requires an elevated command prompt or delegated Backup Operators permissions. WinForge did not elevate or invoke it.",
            "Not assessed",
            "Backup directories may be detected separately, but their catalog metadata is not available in this read-only non-elevated scan.",
            "Catalog listing and integrity remain unverified."));
    }

    private void CollectRecoveryPartitions(
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        var found = false;
        try
        {
            foreach (var row in QueryWmi(
                         @"\\.\root\Microsoft\Windows\Storage",
                         "SELECT DiskNumber, PartitionNumber, DriveLetter, AccessPaths, Size, GptType, Guid, IsHidden, NoDefaultDriveLetter, IsOffline FROM MSFT_Partition"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (row)
                {
                    found = true;
                    var gptType = ReadWmiString(row, "GptType");
                    var recoveryPartition = string.Equals(
                        gptType?.Trim('{', '}'),
                        RecoveryPartitionType,
                        StringComparison.OrdinalIgnoreCase);
                    var diskNumber = ReadWmiString(row, "DiskNumber");
                    var partitionNumber = ReadWmiString(row, "PartitionNumber");
                    var accessPaths = ReadWmiString(row, "AccessPaths");
                    var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["diskNumber"] = diskNumber,
                        ["partitionNumber"] = partitionNumber,
                        ["driveLetter"] = ReadWmiString(row, "DriveLetter"),
                        ["accessPaths"] = accessPaths,
                        ["sizeBytes"] = ReadWmiString(row, "Size"),
                        ["gptType"] = gptType,
                        ["partitionGuid"] = ReadWmiString(row, "Guid"),
                        ["isRecoveryPartition"] = recoveryPartition ? "true" : "false",
                        ["isHidden"] = ReadWmiString(row, "IsHidden"),
                        ["noDefaultDriveLetter"] = ReadWmiString(row, "NoDefaultDriveLetter"),
                        ["isOffline"] = ReadWmiString(row, "IsOffline"),
                        ["freeBytes"] = null
                    };
                    components.Add(CreateDetailedItem(
                        InventoryComponentType.RecoveryPartition,
                        $"partition:{diskNumber}:{partitionNumber}",
                        recoveryPartition
                            ? $"Recovery partition on disk {diskNumber}"
                            : $"Partition {partitionNumber} on disk {diskNumber}",
                        recoveryPartition ? "Recovery GPT partition reported" : "Partition reported",
                        "MSFT_Partition WMI class (root\\Microsoft\\Windows\\Storage)",
                        InventoryFieldAvailability.Available,
                        details,
                        recoveryPartition ? "High: GPT type matches Windows recovery partition GUID" : "Source-reported",
                        null,
                        "Partition type and size do not prove that a recovery image is present or usable. Free space is not exposed for an unmounted partition."));
                }
            }

            if (!found)
            {
                components.Add(CreateItem(
                    InventoryComponentType.RecoveryPartition,
                    "partition-catalog",
                    "Disk partition inventory",
                    "No partition records reported",
                    InventoryFieldAvailability.Missing,
                    "MSFT_Partition WMI class (root\\Microsoft\\Windows\\Storage)",
                    null,
                    "Source query completed",
                    "The storage provider may be unavailable or may expose no records to this process.",
                    "No partition instances were returned."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsWmiAccessFailure(exception))
        {
            AddUnavailableSource(
                components,
                issues,
                "partition-catalog",
                "Disk partition inventory",
                "MSFT_Partition WMI class (root\\Microsoft\\Windows\\Storage)",
                exception,
                "partition_inventory");
        }
    }

    private void CollectBitLockerStatus(
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        var found = false;
        try
        {
            foreach (var volume in QueryWmi(
                         @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption",
                         "SELECT DeviceID, DriveLetter, PersistentVolumeID FROM Win32_EncryptableVolume"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (volume)
                {
                    found = true;
                    var deviceId = ReadWmiString(volume, "DeviceID");
                    var driveLetter = ReadWmiString(volume, "DriveLetter");
                    var protectionStatus = (string?)null;
                    var statusAvailability = InventoryFieldAvailability.Unavailable;
                    var warning = (string?)null;
                    try
                    {
                        using var result = volume.InvokeMethod("GetProtectionStatus", null, null);
                        if (result is not null)
                        {
                            var returnCode = Convert.ToUInt32(
                                result["ReturnValue"],
                                CultureInfo.InvariantCulture);
                            var code = Convert.ToUInt32(
                                result["ProtectionStatus"],
                                CultureInfo.InvariantCulture);
                            if (returnCode == 0)
                            {
                                protectionStatus = code switch
                                {
                                    0 => "Protection off or not protecting",
                                    1 => "Protected",
                                    2 => "Unknown",
                                    _ => $"Unknown status ({code})"
                                };
                                statusAvailability = code <= 2
                                    ? code == 2
                                        ? InventoryFieldAvailability.Unknown
                                        : InventoryFieldAvailability.Available
                                    : InventoryFieldAvailability.Unavailable;
                            }
                            else
                            {
                                statusAvailability = returnCode is 5 or 5_000
                                    ? InventoryFieldAvailability.Inaccessible
                                    : InventoryFieldAvailability.Unavailable;
                                warning = $"BitLocker provider returned code {returnCode}.";
                            }
                        }
                    }
                    catch (ManagementException exception)
                    {
                        _logger.LogDebug(exception, "BitLocker status could not be read for {DeviceId}.", deviceId);
                        statusAvailability = exception.ErrorCode == ManagementStatus.AccessDenied
                            ? InventoryFieldAvailability.Inaccessible
                            : InventoryFieldAvailability.Unavailable;
                        warning = "The BitLocker protection status method could not be read.";
                    }
                    catch (Exception exception) when (
                        exception is FormatException or InvalidCastException or OverflowException)
                    {
                        _logger.LogWarning(exception, "BitLocker status metadata was incomplete for {DeviceId}.", deviceId);
                        statusAvailability = InventoryFieldAvailability.Unavailable;
                        warning = "The BitLocker provider returned incomplete status metadata.";
                    }

                    var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["deviceId"] = deviceId,
                        ["driveLetter"] = driveLetter,
                        ["persistentVolumeId"] = ReadWmiString(volume, "PersistentVolumeID"),
                        ["protectionStatus"] = protectionStatus,
                        ["protectionStatusCode"] = null,
                        ["recoveryKeyCollected"] = "false"
                    };
                    var component = CreateDetailedItem(
                        InventoryComponentType.BitLockerVolume,
                        $"bitlocker:{deviceId ?? driveLetter ?? Guid.NewGuid().ToString("N")}",
                        string.IsNullOrWhiteSpace(driveLetter) ? "BitLocker volume" : $"BitLocker volume {driveLetter}",
                        protectionStatus ?? "Protection status unavailable",
                        "Win32_EncryptableVolume.GetProtectionStatus",
                        statusAvailability,
                        details,
                        "Source-reported",
                        warning,
                        "BitLocker status can change after the query. Recovery keys and key protectors are never collected.");
                    var fieldAvailability = component.FieldAvailability.ToDictionary(
                        field => field.Key,
                        field => field.Value,
                        StringComparer.Ordinal);
                    fieldAvailability["protectionStatus"] = statusAvailability;
                    fieldAvailability["protectionStatusCode"] = InventoryFieldAvailability.Unsupported;
                    components.Add(component with { FieldAvailability = fieldAvailability });
                }
            }

            if (!found)
            {
                components.Add(CreateItem(
                    InventoryComponentType.BitLockerVolume,
                    "bitlocker-volume-catalog",
                    "BitLocker volume status",
                    "No encryptable volumes reported",
                    InventoryFieldAvailability.Missing,
                    "Win32_EncryptableVolume WMI provider",
                    null,
                    "Source query completed",
                    "An empty provider result does not prove that volumes are unencrypted; provider availability and permissions vary.",
                    "No encryptable volume instances were returned by the provider."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsWmiAccessFailure(exception))
        {
            AddUnavailableSource(
                components,
                issues,
                "bitlocker-volume-catalog",
                "BitLocker volume status",
                "Win32_EncryptableVolume WMI provider",
                exception,
                "bitlocker_status");
        }
    }

    private void CollectRecoveryServices(
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        var found = false;
        try
        {
            const string query =
                "SELECT Name, DisplayName, Description, State, Status, StartMode, StartName, PathName, ServiceType " +
                "FROM Win32_Service WHERE Name = 'VSS' OR Name = 'swprv' OR Name = 'wbengine' OR Name = 'SDRSVC' OR Name = 'ReAgentSvc'";
            foreach (var service in QueryWmi(@"\\.\root\cimv2", query))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (service)
                {
                    found = true;
                    var name = ReadWmiString(service, "Name");
                    var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["serviceName"] = name,
                        ["displayName"] = ReadWmiString(service, "DisplayName"),
                        ["description"] = ReadWmiString(service, "Description"),
                        ["state"] = ReadWmiString(service, "State"),
                        ["status"] = ReadWmiString(service, "Status"),
                        ["startupType"] = ReadWmiString(service, "StartMode"),
                        ["serviceAccount"] = ReadWmiString(service, "StartName"),
                        ["serviceType"] = ReadWmiString(service, "ServiceType"),
                        ["executablePath"] = ExtractExecutablePath(ReadWmiString(service, "PathName"))
                    };
                    components.Add(CreateDetailedItem(
                        InventoryComponentType.RecoveryService,
                        $"recovery-service:{name ?? Guid.NewGuid().ToString("N")}",
                        details["displayName"] ?? name ?? "Recovery-related service",
                        details["state"] ?? "State not reported",
                        "Win32_Service WMI class",
                        InventoryFieldAvailability.Available,
                        details,
                        "Source-reported",
                        null,
                        "Service state is observational only; stopped or disabled services are not changed."));
                }
            }

            if (!found)
            {
                components.Add(CreateItem(
                    InventoryComponentType.RecoveryService,
                    "recovery-service-catalog",
                    "Recovery-related services",
                    "No matching services reported",
                    InventoryFieldAvailability.Missing,
                    "Win32_Service WMI class",
                    null,
                    "Source query completed",
                    "Service names differ across Windows editions; this list covers selected Windows recovery and shadow-copy services only.",
                    "No configured service with the selected names was returned."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsWmiAccessFailure(exception))
        {
            AddUnavailableSource(
                components,
                issues,
                "recovery-service-catalog",
                "Recovery-related services",
                "Win32_Service WMI class",
                exception,
                "recovery_services");
        }
    }

    private void CollectRecoveryEvents(
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        var found = false;
        try
        {
            const string query =
                "*[System[Provider[@Name='VSS' or @Name='volsnap' or @Name='Microsoft-Windows-Backup' " +
                "or @Name='Microsoft-Windows-SystemRestore' or @Name='Microsoft-Windows-ReAgent']]]";
            var eventQuery = new EventLogQuery("System", PathType.LogName, query)
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };
            using var reader = new EventLogReader(eventQuery);
            for (var index = 0; index < MaximumRecoveryEvents; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                found = true;
                var provider = record.ProviderName ?? "Unknown provider";
                var eventId = record.Id.ToString(CultureInfo.InvariantCulture);
                var time = record.TimeCreated?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["logName"] = record.LogName,
                    ["provider"] = provider,
                    ["eventId"] = eventId,
                    ["level"] = record.LevelDisplayName,
                    ["recordId"] = record.RecordId?.ToString(CultureInfo.InvariantCulture),
                    ["eventTimeUtc"] = time,
                    ["eventMessage"] = null
                };
                var component = CreateDetailedItem(
                    InventoryComponentType.RecoveryEvent,
                    $"recovery-event:{record.LogName}:{record.RecordId?.ToString(CultureInfo.InvariantCulture) ?? Guid.NewGuid().ToString("N")}",
                    $"{provider} event {eventId}",
                    record.LevelDisplayName ?? "Event level unavailable",
                    "Windows Event Log API; System channel",
                    InventoryFieldAvailability.Available,
                    details,
                    "Windows event metadata",
                    null,
                    "Event messages are not collected to limit sensitive data. This is a bounded recent-event sample, not a complete recovery history.");
                var fieldAvailability = component.FieldAvailability.ToDictionary(
                    field => field.Key,
                    field => field.Value,
                    StringComparer.Ordinal);
                fieldAvailability["eventMessage"] = InventoryFieldAvailability.Unsupported;
                components.Add(component with { FieldAvailability = fieldAvailability });
            }

            if (!found)
            {
                components.Add(CreateItem(
                    InventoryComponentType.RecoveryEvent,
                    "recovery-event-catalog",
                    "Recovery-related event records",
                    "No matching recent events reported",
                    InventoryFieldAvailability.Missing,
                    "Windows Event Log API; System channel",
                    null,
                    "Source query completed",
                    $"Only the most recent matching events in the System log are sampled (maximum {MaximumRecoveryEvents}).",
                    "No matching event records were returned."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is EventLogException or UnauthorizedAccessException or
                System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(exception, "Recovery-related Windows event records could not be read.");
            AddUnavailableSource(
                components,
                issues,
                "recovery-event-catalog",
                "Recovery-related event records",
                "Windows Event Log API; System channel",
                exception,
                "recovery_events");
        }
    }

    private void CollectWindowsBootConfiguration(
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        try
        {
            var found = false;
            foreach (var row in QueryWmi(
                         @"\\.\root\cimv2",
                         "SELECT Name, BootDirectory, ConfigurationPath, LastDrive, SettingID FROM Win32_BootConfiguration"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (row)
                {
                    found = true;
                    var settingId = ReadWmiString(row, "SettingID");
                    var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["name"] = ReadWmiString(row, "Name"),
                        ["bootDirectory"] = ReadWmiString(row, "BootDirectory"),
                        ["configurationPath"] = ReadWmiString(row, "ConfigurationPath"),
                        ["lastDrive"] = ReadWmiString(row, "LastDrive"),
                        ["settingId"] = settingId
                    };
                    components.Add(CreateDetailedItem(
                        InventoryComponentType.RecoveryEnvironment,
                        $"windows-boot-configuration:{settingId ?? "current"}",
                        "Windows boot configuration metadata",
                        "Boot metadata reported",
                        "Win32_BootConfiguration WMI class",
                        InventoryFieldAvailability.Available,
                        details,
                        "Source-reported",
                        null,
                        "This is descriptive metadata only; WinForge does not read or modify BCD entries."));
                }
            }

            if (!found)
            {
                components.Add(CreateItem(
                    InventoryComponentType.RecoveryEnvironment,
                    "windows-boot-configuration",
                    "Windows boot configuration metadata",
                    "No boot configuration records reported",
                    InventoryFieldAvailability.Missing,
                    "Win32_BootConfiguration WMI class",
                    null,
                    "Source query completed",
                    "WMI metadata is not a full BCD inventory.",
                    "The provider returned no boot configuration instances."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsWmiAccessFailure(exception))
        {
            AddUnavailableSource(
                components,
                issues,
                "windows-boot-configuration",
                "Windows boot configuration metadata",
                "Win32_BootConfiguration WMI class",
                exception,
                "boot_configuration");
        }
    }

    private void AddUnavailableSource(
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        string identity,
        string name,
        string source,
        Exception exception,
        string issuePrefix)
    {
        var accessDenied = exception is ManagementException managementException &&
                           managementException.ErrorCode == ManagementStatus.AccessDenied;
        var availability = accessDenied
            ? InventoryFieldAvailability.Inaccessible
            : exception is ManagementException wmiException &&
              wmiException.ErrorCode == ManagementStatus.InvalidClass
                ? InventoryFieldAvailability.Unsupported
                : InventoryFieldAvailability.Unavailable;
        var status = availability switch
        {
            InventoryFieldAvailability.Inaccessible => "Access denied; status unknown",
            InventoryFieldAvailability.Unsupported => "Provider class unsupported",
            _ => "Source unavailable"
        };
        components.Add(CreateItem(
            RecoveryItemTypeForSource(identity),
            identity,
            name,
            status,
            availability,
            source,
            "Windows did not expose this information to the current process.",
            "Unknown",
            "Inaccessible or unsupported recovery information must not be interpreted as missing.",
            exception is ManagementException providerException
                ? $"WMI provider error: {providerException.ErrorCode}."
                : "The source could not be queried."));
        issues.Add(new CollectorIssue(
            CollectorId,
            $"{issuePrefix}_{(accessDenied ? "access_denied" : "unavailable")}",
            $"{name} could not be read."));
        _logger.LogWarning(exception, "Recovery source {Source} could not be queried.", source);
    }

    private static InventoryComponentType RecoveryItemTypeForSource(string identity) =>
        identity switch
        {
            "restore-point-catalog" => InventoryComponentType.RestorePoint,
            "bitlocker-volume-catalog" => InventoryComponentType.BitLockerVolume,
            "recovery-service-catalog" => InventoryComponentType.RecoveryService,
            "recovery-event-catalog" => InventoryComponentType.RecoveryEvent,
            "partition-catalog" => InventoryComponentType.RecoveryPartition,
            "available-volume-catalog" => InventoryComponentType.RecoveryVolume,
            _ => InventoryComponentType.RecoveryEnvironment
        };

    private static InventoryComponent CreateItem(
        InventoryComponentType type,
        string identity,
        string displayName,
        string status,
        InventoryFieldAvailability availability,
        string source,
        string? warning,
        string confidence,
        string limitations,
        string? additionalInfo)
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["status"] = status,
            ["availability"] = availability.ToString(),
            ["dataSource"] = source,
            ["collectionTimestampUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["warnings"] = warning,
            ["confidence"] = confidence,
            ["limitations"] = limitations,
            ["additionalInformation"] = additionalInfo
        };
        return CreateDetailedItem(
            type,
            identity,
            displayName,
            status,
            source,
            availability,
            details,
            confidence,
            warning,
            limitations);
    }

    private static InventoryComponent CreateDetailedItem(
        InventoryComponentType type,
        string identity,
        string displayName,
        string status,
        string source,
        InventoryFieldAvailability availability,
        IReadOnlyDictionary<string, string?> specificDetails,
        string confidence,
        string? warning,
        string limitations)
    {
        var details = new Dictionary<string, string?>(specificDetails, StringComparer.Ordinal)
        {
            ["status"] = status,
            ["availability"] = availability.ToString(),
            ["dataSource"] = source,
            ["collectionTimestampUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["warnings"] = warning,
            ["confidence"] = confidence,
            ["limitations"] = limitations
        };
        var fieldAvailability = details.ToDictionary(
            detail => detail.Key,
            detail => detail.Key switch
            {
                "availability" => InventoryFieldAvailability.Available,
                "status" => availability,
                "dataSource" or "collectionTimestampUtc" or "confidence" or "limitations" =>
                    InventoryFieldAvailability.Available,
                _ when detail.Value is null => InventoryFieldAvailability.Missing,
                _ => InventoryFieldAvailability.Available
            },
            StringComparer.Ordinal);
        if (warning is not null)
        {
            fieldAvailability["warnings"] = InventoryFieldAvailability.Available;
        }

        return new InventoryComponent(
            type,
            identity,
            string.IsNullOrWhiteSpace(displayName) ? type.ToString() : displayName,
            source,
            2,
            DateTimeOffset.UtcNow,
            details)
        {
            Category = CategoryFor(type),
            FieldAvailability = fieldAvailability
        };
    }

    private static string CategoryFor(InventoryComponentType type) =>
        type switch
        {
            InventoryComponentType.RestorePoint => "Restore points",
            InventoryComponentType.BackupCatalogItem => "Backup discovery",
            InventoryComponentType.RecoveryPartition or InventoryComponentType.RecoveryVolume =>
                "Recovery storage",
            InventoryComponentType.BitLockerVolume => "BitLocker",
            InventoryComponentType.RecoveryService => "Recovery services",
            InventoryComponentType.RecoveryEvent => "Recovery events",
            _ => "Recovery configuration"
        };

    private static IEnumerable<ManagementObject> QueryWmi(string path, string query)
    {
        var options = new WmiEnumerationOptions
        {
            ReturnImmediately = false,
            Rewindable = false,
            Timeout = TimeSpan.FromSeconds(15)
        };
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(path),
            new ObjectQuery(query),
            options);
        using var rows = searcher.Get();
        foreach (ManagementObject row in rows)
        {
            yield return row;
        }
    }

    private static string? ReadWmiString(ManagementBaseObject row, string propertyName)
    {
        var value = ReadWmiValue(row, propertyName);
        return value is string stringValue
            ? stringValue
            : value is null
                ? null
                : value is System.Collections.IEnumerable values
                    ? string.Join(
                        "; ",
                        values.Cast<object?>()
                            .Where(item => item is not null)
                            .Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)))
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static object? ReadWmiValue(ManagementBaseObject row, string propertyName) =>
        row.Properties[propertyName]?.Value;

    private static string? ParseWmiDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(value)
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return value;
        }
    }

    private static string? ParseWinReStatus(string output)
    {
        var match = Regex.Match(
            output,
            @"Windows\s+RE\s+status\s*:\s*(?<status>[^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var status = match.Groups["status"].Value.Trim();
        return status.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
            ? "Enabled"
            : status.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                ? "Disabled"
                : null;
    }

    private static string? ParseWinReValue(string output, string label)
    {
        var match = Regex.Match(
            output,
            $@"{Regex.Escape(label)}\s*:\s*(?<value>[^\r\n]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? NullIfEmpty(match.Groups["value"].Value) : null;
    }

    private static bool IsWmiAccessFailure(Exception exception) =>
        exception is ManagementException or UnauthorizedAccessException or System.Security.SecurityException or IOException;

    private static string? SafeVolumeLabel(DriveInfo drive)
    {
        try
        {
            return NullIfEmpty(drive.VolumeLabel);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? SafeFileSystem(DriveInfo drive)
    {
        try
        {
            return NullIfEmpty(drive.DriveFormat);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ExtractExecutablePath(string? serviceCommand)
    {
        if (string.IsNullOrWhiteSpace(serviceCommand))
        {
            return null;
        }

        var command = serviceCommand.Trim();
        if (command[0] == '"')
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : command.Trim('"');
        }

        var executableEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0
            ? command[..(executableEnd + 4)]
            : command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
