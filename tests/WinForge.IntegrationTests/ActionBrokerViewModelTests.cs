using WinForge.App.ViewModels;
using WinForge.Application.Actions;
using WinForge.Domain.Actions;

namespace WinForge.IntegrationTests;

public sealed class ActionBrokerViewModelTests
{
    [Fact]
    public async Task ViewModel_completes_explicit_simulation_workflow_and_refreshes_journal()
    {
        var state = new SimulatedSystemState();
        var service = CreateService(state);
        var viewModel = new ActionBrokerViewModel(service);
        await viewModel.InitializeAsync();
        viewModel.SelectedActionId = "user-preferences.update";

        await viewModel.GeneratePreviewAsync();

        Assert.NotNull(viewModel.CurrentRequest);
        Assert.Null(viewModel.CurrentApproval);
        Assert.Contains(viewModel.CurrentRequest!.PreviewDigest, viewModel.PreviewText, StringComparison.Ordinal);
        Assert.Contains("Expected simulated effect", viewModel.PreviewText, StringComparison.Ordinal);
        Assert.Contains("Policy: allowed by simulation-only policy", viewModel.PreviewText, StringComparison.Ordinal);

        await viewModel.ApproveAsync();

        Assert.NotNull(viewModel.CurrentApproval);
        Assert.Contains("one-time", viewModel.ApprovalText, StringComparison.OrdinalIgnoreCase);

        await viewModel.ExecuteAsync();

        Assert.Equal(ActionExecutionState.Succeeded, viewModel.LastExecution!.State);
        Assert.Null(viewModel.CurrentApproval);
        Assert.Contains(viewModel.Journal, entry => entry.EventType == "completed");
        Assert.Equal(
            new[] { "proposed", "approved", "started", "completed" },
            viewModel.Journal
                .Where(entry => entry.RequestId == viewModel.CurrentRequest!.RequestId)
                .Reverse()
                .Select(entry => entry.EventType));
        Assert.Equal("dark", state.Preferences["theme"]);
        Assert.Contains("No Windows settings were changed", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("simulated verification passed=True", viewModel.ExecutionText, StringComparison.Ordinal);
        Assert.Contains("does not prove real rollback capability", viewModel.ExecutionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModel_invalidates_preview_and_approval_when_inputs_change()
    {
        var viewModel = new ActionBrokerViewModel(CreateService(new SimulatedSystemState()));
        await viewModel.InitializeAsync();
        viewModel.SelectedActionId = "user-preferences.update";
        await viewModel.GeneratePreviewAsync();
        await viewModel.ApproveAsync();

        viewModel.ParameterJson = "{\"name\":\"theme\",\"value\":\"light\"}";

        Assert.Null(viewModel.CurrentRequest);
        Assert.Null(viewModel.CurrentApproval);
        Assert.False(viewModel.ExecuteCommand.CanExecute(null));
        Assert.Contains("generate a new preview", viewModel.PreviewText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ViewModel_shutdown_cancels_and_waits_for_in_flight_simulation()
    {
        var state = new SimulatedSystemState();
        var store = new GatedJournalStore();
        var viewModel = new ActionBrokerViewModel(CreateService(state, store));
        await viewModel.InitializeAsync();
        viewModel.SelectedActionId = "user-preferences.update";
        await viewModel.GeneratePreviewAsync();
        await viewModel.ApproveAsync();

        var executionTask = viewModel.ExecuteAsync();
        await store.Started.Task;
        var shutdownTask = viewModel.StopAsync();
        Assert.False(shutdownTask.IsCompleted);

        store.ReleaseStarted.TrySetResult();
        await shutdownTask;
        await executionTask;

        Assert.Equal(ActionExecutionState.Interrupted, viewModel.LastExecution!.State);
        Assert.Equal("light", state.Preferences["theme"]);
        Assert.Contains(store.Entries, entry => entry.EventType == "cancelled");
    }

    [Fact]
    public async Task ViewModel_preserves_successful_simulation_result_when_journal_refresh_fails()
    {
        var viewModel = new ActionBrokerViewModel(
            CreateService(new SimulatedSystemState(), new FailJournalRefreshStore()));
        await viewModel.InitializeAsync();
        viewModel.SelectedActionId = "user-preferences.update";
        await viewModel.GeneratePreviewAsync();
        await viewModel.ApproveAsync();

        await viewModel.ExecuteAsync();

        Assert.Equal(ActionExecutionState.Succeeded, viewModel.LastExecution!.State);
        Assert.Contains("result: Succeeded", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("journal refresh failed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be refreshed", viewModel.ValidationError, StringComparison.OrdinalIgnoreCase);
    }

    private static ActionBrokerService CreateService(
        SimulatedSystemState state,
        IActionBrokerJournalStore? store = null) =>
        new(new TrustedActionCatalog(), new ActionBrokerPolicyEngine(), state, store ?? new InMemoryJournalStore());

    private sealed class InMemoryJournalStore : IActionBrokerJournalStore
    {
        private readonly List<ActionJournalEntry> _entries = new();

        public Task<IReadOnlyList<ActionJournalEntry>> LoadJournalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ActionJournalEntry>>(_entries.ToArray());
        }

        public Task SaveJournalEntryAsync(ActionJournalEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task SaveStateSnapshotAsync(ActionBrokerStateSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class GatedJournalStore : IActionBrokerJournalStore
    {
        private readonly List<ActionJournalEntry> _entries = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ActionJournalEntry> Entries => _entries.ToArray();

        public Task<IReadOnlyList<ActionJournalEntry>> LoadJournalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ActionJournalEntry>>(_entries.ToArray());
        }

        public async Task SaveJournalEntryAsync(ActionJournalEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.Add(entry);
            if (entry.EventType == "started")
            {
                Started.TrySetResult();
                await ReleaseStarted.Task.ConfigureAwait(false);
            }
        }

        public Task SaveStateSnapshotAsync(ActionBrokerStateSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FailJournalRefreshStore : IActionBrokerJournalStore
    {
        private readonly List<ActionJournalEntry> _entries = new();

        public Task<IReadOnlyList<ActionJournalEntry>> LoadJournalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.Any(entry => entry.EventType == "completed"))
            {
                throw new IOException("Synthetic journal refresh failure.");
            }

            return Task.FromResult<IReadOnlyList<ActionJournalEntry>>(_entries.ToArray());
        }

        public Task SaveJournalEntryAsync(ActionJournalEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task SaveStateSnapshotAsync(ActionBrokerStateSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
