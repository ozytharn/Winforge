using System.Collections.Concurrent;
using System.Text.Json;
using WinForge.Application.Actions;
using WinForge.Domain.Actions;

namespace WinForge.Infrastructure.Actions;

public sealed class JsonFileActionBrokerJournalStore : IActionBrokerJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _journalPath;
    private readonly string _statePath;
    private readonly SemaphoreSlim _fileLock;

    public JsonFileActionBrokerJournalStore(string? journalPath = null)
    {
        _journalPath = Path.GetFullPath(journalPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinForge",
            "action-broker-journal.json"));
        _statePath = Path.Combine(Path.GetDirectoryName(_journalPath)!, "action-broker-state.json");
        _fileLock = FileLocks.GetOrAdd(_journalPath, static _ => new SemaphoreSlim(1, 1));

        var directory = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<IReadOnlyList<ActionJournalEntry>> LoadJournalAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadJournalCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveJournalEntryAsync(ActionJournalEntry entry, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = (await LoadJournalCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            entries.Add(entry);
            await WriteAtomicAsync(_journalPath, JsonSerializer.Serialize(entries, JsonOptions), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveStateSnapshotAsync(ActionBrokerStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(_statePath, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<IReadOnlyList<ActionJournalEntry>> LoadJournalCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
        {
            return Array.Empty<ActionJournalEntry>();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var json = await File.ReadAllTextAsync(_journalPath, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<List<ActionJournalEntry>>(json, JsonOptions)
                   ?? throw new InvalidDataException("Action Broker journal contains a null document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Action Broker journal is corrupt; refusing to treat it as empty.", exception);
        }
    }

    private static async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
