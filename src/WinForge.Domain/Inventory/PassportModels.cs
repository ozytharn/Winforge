namespace WinForge.Domain.Inventory;

public enum InventoryComponentType
{
    OperatingSystem,
    Processor,
    MemoryModule,
    MemorySummary,
    Motherboard,
    Bios,
    GraphicsAdapter,
    StorageDevice,
    NetworkAdapter,
    Display,
    AudioDevice,
    AudioEndpoint,
    Battery,
    Firmware,
    DriverPackage,
    InputDevice,
    Sensor,
    StorageHealth,
    InstalledApplication,
    StoreApplication,
    Service,
    Process,
    StartupEntry,
    ScheduledTask,
    WindowsUpdate,
    WindowsFeature,
    EnvironmentVariable,
    WindowsSystemInfo,
    RecoveryEnvironment,
    RestorePoint,
    BackupCatalogItem,
    RecoveryPartition,
    RecoveryVolume,
    BitLockerVolume,
    RecoveryService,
    RecoveryEvent,
    Device
}

public enum InventoryScanGroup
{
    All,
    Hardware,
    Software,
    System,
    Recovery
}

public enum InventoryFieldAvailability
{
    Unknown,
    Available,
    Missing,
    Unavailable,
    Unsupported,
    Inaccessible
}

public enum InventoryComparisonStatus
{
    New,
    Changed,
    Unchanged,
    NotObserved
}

public enum InventoryScanStatus
{
    InProgress,
    Completed,
    Partial,
    Failed,
    Cancelled
}

public sealed record InventoryComponent(
    InventoryComponentType Type,
    string IdentityKey,
    string DisplayName,
    string Source,
    int DetailsSchemaVersion,
    DateTimeOffset ObservedAt,
    IReadOnlyDictionary<string, string?> Details)
{
    public string Category { get; init; } = "General";

    public IReadOnlyDictionary<string, InventoryFieldAvailability> FieldAvailability { get; init; } =
        new Dictionary<string, InventoryFieldAvailability>();
}

public sealed record CollectorIssue(string CollectorId, string Code, string Message);

public sealed record CollectorResult(
    string CollectorId,
    IReadOnlyList<InventoryComponent> Components,
    IReadOnlyList<CollectorIssue> Issues);

public sealed record ScanProgress(string Message, int CompletedCollectors, int TotalCollectors);

public sealed record ScanRecord(
    Guid ScanId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Scope,
    InventoryScanStatus Status,
    int AttemptedCollectors,
    int TotalCollectors,
    IReadOnlyList<InventoryComponent> Components,
    IReadOnlyList<CollectorIssue> Issues);

public sealed record LatestScan(
    Guid ScanId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Scope,
    InventoryScanStatus Status,
    int AttemptedCollectors,
    int TotalCollectors,
    IReadOnlyList<CollectorIssue> Issues);

public sealed record PassportSnapshot(
    LatestScan? LatestScan,
    IReadOnlyList<InventoryComponent> Components)
{
    public LatestScan? ComparisonScan { get; init; }

    public LatestScan? PreviousComparisonScan { get; init; }

    public IReadOnlyList<InventoryComponent> ComparisonComponents { get; init; } = [];

    public IReadOnlyList<InventoryComponent> PreviousComparisonComponents { get; init; } = [];
}
