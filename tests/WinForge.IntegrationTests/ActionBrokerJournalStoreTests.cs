using WinForge.Domain.Actions;
using WinForge.Infrastructure.Actions;

namespace WinForge.IntegrationTests;

public sealed class ActionBrokerJournalStoreTests
{
    [Fact]
    public async Task LoadJournalAsync_fails_closed_when_journal_is_corrupt()
    {
        var (directory, path) = CreateJournalPath();
        try
        {
            await File.WriteAllTextAsync(path, "{ malformed journal");
            var store = new JsonFileActionBrokerJournalStore(path);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadJournalAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveJournalEntryAsync(CreateEntry("new-entry"), CancellationToken.None));
            Assert.Equal("{ malformed journal", await File.ReadAllTextAsync(path));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task Concurrent_appends_are_serialized_without_losing_journal_entries()
    {
        var (directory, path) = CreateJournalPath();
        try
        {
            var firstStore = new JsonFileActionBrokerJournalStore(path);
            var secondStore = new JsonFileActionBrokerJournalStore(path);
            var writes = Enumerable.Range(0, 40)
                .Select(index => (index % 2 == 0 ? firstStore : secondStore)
                    .SaveJournalEntryAsync(CreateEntry($"entry-{index}"), CancellationToken.None));

            await Task.WhenAll(writes);

            var loaded = await firstStore.LoadJournalAsync(CancellationToken.None);
            Assert.Equal(40, loaded.Count);
            Assert.Equal(40, loaded.Select(entry => entry.JournalId).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task State_snapshot_is_written_atomically_to_the_expected_local_file()
    {
        var (directory, path) = CreateJournalPath();
        try
        {
            var store = new JsonFileActionBrokerJournalStore(path);
            var snapshot = new ActionBrokerStateSnapshot(
                "snapshot-1",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string> { ["theme"] = "dark" },
                new Dictionary<string, bool>(),
                new Dictionary<string, string>(),
                Array.Empty<string>());

            await store.SaveStateSnapshotAsync(snapshot, CancellationToken.None);

            var statePath = Path.Combine(directory, "action-broker-state.json");
            var content = await File.ReadAllTextAsync(statePath);
            Assert.Contains("\"dark\"", content, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static (string Directory, string JournalPath) CreateJournalPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinForge.ActionBroker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return (directory, Path.Combine(directory, "action-broker-journal.json"));
    }

    private static ActionJournalEntry CreateEntry(string id) =>
        new(id, $"request-{id}", "user-preferences.update", "completed", "Succeeded", DateTimeOffset.UtcNow, "Test", "Simulation only.");

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
