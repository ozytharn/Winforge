using WinForge.Application.Actions;
using WinForge.Domain.Actions;

namespace WinForge.UnitTests;

public sealed class ActionBrokerCoreTests
{
    [Fact]
    public async Task CreateRequest_approve_and_execute_simulation_succeeds_and_snapshots_actual_state()
    {
        var state = new SimulatedSystemState();
        var store = new InMemoryJournalStore();
        var service = CreateService(state, store);
        var request = await CreatePreferenceRequestAsync(service);
        var approval = await service.CreateApprovalAsync(request, "user-1");

        var execution = await service.ExecuteAsync(request, approval);

        Assert.Equal(ActionExecutionState.Succeeded, execution.State);
        Assert.True(execution.VerificationPassed);
        Assert.True(execution.SimulationOnly);
        Assert.Equal("dark", state.Preferences["theme"]);
        Assert.Equal("dark", store.LastSnapshot!.Preferences["theme"]);
        Assert.Equal(
            new[] { "proposed", "approved", "started", "completed" },
            store.Entries.Select(entry => entry.EventType));
    }

    [Fact]
    public async Task ExecuteAsync_rejects_expired_approval_using_controlled_clock()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var service = CreateService(new SimulatedSystemState(), new InMemoryJournalStore(), clock);
        var request = await CreatePreferenceRequestAsync(service);
        var approval = await service.CreateApprovalAsync(request, "user-1");
        clock.Advance(TimeSpan.FromMinutes(11));

        var execution = await service.ExecuteAsync(request, approval);

        Assert.Equal(ActionExecutionState.Rejected, execution.State);
        Assert.Contains("expired", execution.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_preview_tampering_even_when_digest_is_recomputed()
    {
        var state = new SimulatedSystemState();
        var service = CreateService(state);
        var request = await CreatePreferenceRequestAsync(service);
        var approval = await service.CreateApprovalAsync(request, "user-1");
        var tampered = RebindPreview(request with
        {
            ParameterJson = "{\"name\":\"theme\",\"value\":\"dark\"}",
            UserIntent = "A changed intent"
        });

        var execution = await service.ExecuteAsync(tampered, approval);

        Assert.Equal(ActionExecutionState.Rejected, execution.State);
        Assert.Contains("exact request", execution.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("light", state.Preferences["theme"]);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_modified_target_and_parameter_after_approval()
    {
        var state = new SimulatedSystemState();
        var service = CreateService(state);
        var request = await CreatePreferenceRequestAsync(service);
        var approval = await service.CreateApprovalAsync(request, "user-1");
        var tampered = RebindPreview(request with
        {
            Target = request.Target with { Identity = "notification-level" },
            ParameterJson = "{\"name\":\"notification-level\",\"value\":\"off\"}"
        });

        var execution = await service.ExecuteAsync(tampered, approval);

        Assert.Equal(ActionExecutionState.Rejected, execution.State);
        Assert.Equal("light", state.Preferences["theme"]);
        Assert.Equal("balanced", state.Preferences["notification-level"]);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_modified_or_cross_request_approval()
    {
        var service = CreateService(new SimulatedSystemState());
        var firstRequest = await CreatePreferenceRequestAsync(service);
        var firstApproval = await service.CreateApprovalAsync(firstRequest, "user-1");
        var secondRequest = await service.CreateRequestAsync(
            "user-preferences.update",
            "preference",
            "notification-level",
            "Notification level",
            "{\"name\":\"notification-level\",\"value\":\"quiet\"}",
            "Update notification setting in simulation.",
            "corr-second");
        var secondApproval = await service.CreateApprovalAsync(secondRequest, "user-1");

        var alteredApprovals = new[]
        {
            firstApproval with { ApprovalId = "forged" },
            firstApproval with { ActionId = "driver.inspect" },
            firstApproval with { PreviewDigest = "altered-digest" },
            firstApproval with { PolicyVersion = "1.0.1" },
            firstApproval with { ExpiresAtUtc = firstApproval.ExpiresAtUtc.AddSeconds(-1) },
            firstApproval with { State = ActionApprovalState.Consumed }
        };
        var alteredResults = new List<ActionExecutionRecord>();
        foreach (var alteredApproval in alteredApprovals)
        {
            alteredResults.Add(await service.ExecuteAsync(firstRequest, alteredApproval));
        }

        var crossRequestResult = await service.ExecuteAsync(firstRequest, secondApproval);

        Assert.All(alteredResults, result => Assert.Equal(ActionExecutionState.Rejected, result.State));
        Assert.Equal(ActionExecutionState.Rejected, crossRequestResult.State);
        Assert.Equal(ActionExecutionState.Succeeded, (await service.ExecuteAsync(firstRequest, firstApproval)).State);
    }

    [Fact]
    public async Task CreateApprovalAsync_rejects_request_not_issued_by_this_broker_instance()
    {
        var service = CreateService(new SimulatedSystemState());
        var request = await CreatePreferenceRequestAsync(service);
        var restartedService = CreateService(new SimulatedSystemState());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restartedService.CreateApprovalAsync(request, "user-1"));
    }

    [Fact]
    public async Task ExecuteAsync_rejects_replay_after_service_restart()
    {
        var store = new InMemoryJournalStore();
        var state = new SimulatedSystemState();
        var firstService = CreateService(state, store);
        var request = await CreatePreferenceRequestAsync(firstService);
        var approval = await firstService.CreateApprovalAsync(request, "user-1");
        Assert.Equal(ActionExecutionState.Succeeded, (await firstService.ExecuteAsync(request, approval)).State);

        var restartedService = CreateService(new SimulatedSystemState(), store);
        var replay = await restartedService.ExecuteAsync(request, approval);

        Assert.Equal(ActionExecutionState.Rejected, replay.State);
        Assert.Equal("light", restartedService.CurrentState.Preferences["theme"]);
    }

    [Fact]
    public async Task ReadJournalAsync_marks_a_persisted_started_execution_interrupted_after_restart()
    {
        var store = new InMemoryJournalStore();
        store.Seed(new ActionJournalEntry(
            "started-entry",
            "request-crashed",
            "user-preferences.update",
            "started",
            "Executing",
            DateTimeOffset.UtcNow,
            "Started",
            "Synthetic interrupted execution."));
        var restartedService = CreateService(new SimulatedSystemState(), store);

        var history = await restartedService.ReadJournalAsync();

        Assert.Contains(history, entry =>
            entry.RequestId == "request-crashed" &&
            entry.EventType == "interrupted" &&
            entry.State == ActionExecutionState.Interrupted.ToString());
    }

    [Fact]
    public async Task ReadJournalAsync_does_not_mark_a_completed_execution_interrupted()
    {
        var store = new InMemoryJournalStore();
        store.Seed(new ActionJournalEntry(
            "started-entry",
            "request-completed",
            "user-preferences.update",
            "started",
            "Executing",
            DateTimeOffset.UtcNow,
            "Started",
            "Synthetic execution."));
        store.Seed(new ActionJournalEntry(
            "completed-entry",
            "request-completed",
            "user-preferences.update",
            "completed",
            "Succeeded",
            DateTimeOffset.UtcNow.AddSeconds(1),
            "Completed",
            "Synthetic terminal record."));
        var service = CreateService(new SimulatedSystemState(), store);

        var history = await service.ReadJournalAsync();

        Assert.DoesNotContain(history, entry =>
            entry.RequestId == "request-completed" && entry.EventType == "interrupted");
    }

    [Fact]
    public async Task Concurrent_execute_calls_consume_approval_once()
    {
        var state = new SimulatedSystemState();
        var service = CreateService(state);
        var request = await CreatePreferenceRequestAsync(service);
        var approval = await service.CreateApprovalAsync(request, "user-1");

        var results = await Task.WhenAll(
            service.ExecuteAsync(request, approval),
            service.ExecuteAsync(request, approval));

        Assert.Single(results, result => result.State == ActionExecutionState.Succeeded);
        Assert.Single(results, result => result.State == ActionExecutionState.Rejected);
        Assert.Equal("dark", state.Preferences["theme"]);
    }

    [Fact]
    public async Task CreateRequest_rejects_unknown_action_policy_version_and_unexpected_parameter_types()
    {
        var service = CreateService(new SimulatedSystemState());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CreateRequestAsync("not-registered", "preference", "theme", "Theme", "{}", "intent", "corr"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePreferenceRequestAsync(service, policyVersion: "2.0.0"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateRequestAsync(
                "startup-entry.toggle.current-user",
                "startup-entry",
                "observability-panel",
                "Observability panel",
                "{\"name\":\"observability-panel\",\"enabled\":\"true\"}",
                "Toggle a simulated startup entry.",
                "corr-invalid-type"));
    }

    [Fact]
    public async Task CreateRequest_rejects_target_parameter_mismatch_and_unavailable_targets()
    {
        var service = CreateService(new SimulatedSystemState());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateRequestAsync(
                "user-preferences.update",
                "preference",
                "theme",
                "Theme",
                "{\"name\":\"notification-level\",\"value\":\"off\"}",
                "Update simulated preference.",
                "corr-mismatch"));
        var unavailableTargetRequest = await service.CreateRequestAsync(
            "service.inspect",
            "service",
            "unregistered-service",
            "Unknown service",
            "{}",
            "Inspect simulation entry.",
            "corr-unknown-target");
        var unavailableTargetApproval = await service.CreateApprovalAsync(unavailableTargetRequest, "user-1");
        var unavailableTargetResult = await service.ExecuteAsync(unavailableTargetRequest, unavailableTargetApproval);
        Assert.Equal(ActionExecutionState.Rejected, unavailableTargetResult.State);
    }

    [Fact]
    public async Task CreateRequest_rejects_unsupported_policy_version_after_preview_would_be_formed()
    {
        var service = CreateService(new SimulatedSystemState());
        var request = await CreatePreferenceRequestAsync(service);
        var alteredPolicyRequest = RebindPreview(request with { PolicyVersion = "2.0.0" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateApprovalAsync(alteredPolicyRequest, "user-1"));
    }

    [Fact]
    public async Task ExecuteAsync_rejects_cancelled_execution_and_records_terminal_event()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new InMemoryJournalStore(entry =>
        {
            if (entry.EventType == "started")
            {
                cancellation.Cancel();
            }
        });
        var state = new SimulatedSystemState();
        var service = CreateService(state, store);
        var request = await CreatePreferenceRequestAsync(service);
        var approval = await service.CreateApprovalAsync(request, "user-1");

        var result = await service.ExecuteAsync(request, approval, cancellation.Token);

        Assert.Equal(ActionExecutionState.Interrupted, result.State);
        Assert.Equal("light", state.Preferences["theme"]);
        Assert.Contains(store.Entries, entry => entry.EventType == "cancelled" && entry.State == "Interrupted");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_mutate_simulation_when_start_journal_write_fails()
    {
        var failStartWrite = true;
        var store = new InMemoryJournalStore(entry =>
        {
            if (entry.EventType == "started" && failStartWrite)
            {
                failStartWrite = false;
                throw new IOException("Synthetic journal failure.");
            }
        });
        var state = new SimulatedSystemState();
        var service = CreateService(state, store);
        var request = await CreatePreferenceRequestAsync(service);
        var approval = await service.CreateApprovalAsync(request, "user-1");

        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(request, approval));

        Assert.Equal("light", state.Preferences["theme"]);
        Assert.DoesNotContain(store.Entries, entry => entry.EventType == "started");

        var retry = await service.ExecuteAsync(request, approval);

        Assert.Equal(ActionExecutionState.Succeeded, retry.State);
        Assert.Equal("dark", state.Preferences["theme"]);
        Assert.Equal(
            new[] { "proposed", "approved", "started", "completed" },
            store.Entries.Select(entry => entry.EventType));
    }

    [Fact]
    public async Task Duplicate_execution_is_prevented()
    {
        var state = new SimulatedSystemState();
        var service = CreateService(state);
        var request = await service.CreateRequestAsync(
            "workspace.cleanup.transient-artifacts",
            "workspace",
            "tmp",
            "Temporary workspace artifacts",
            "{\"artifact\":\"tmp\\\\analysis-summary.txt\"}",
            "Remove the stale analysis artifact in simulation.",
            "corr-workspace");
        var approval = await service.CreateApprovalAsync(request, "user-1");

        var first = await service.ExecuteAsync(request, approval);
        var second = await service.ExecuteAsync(request, approval);

        Assert.True(first.State == ActionExecutionState.Succeeded, $"{first.Summary}: {first.Details}");
        Assert.Equal(ActionExecutionState.Rejected, second.State);
        Assert.DoesNotContain("tmp\\analysis-summary.txt", state.TransientArtifacts);
    }

    private static async Task<ActionRequest> CreatePreferenceRequestAsync(
        ActionBrokerService service,
        string policyVersion = ActionRequestValidator.SupportedPolicyVersion) =>
        await service.CreateRequestAsync(
            "user-preferences.update",
            "preference",
            "theme",
            "Theme",
            "{\"value\":\"dark\",\"name\":\"theme\"}",
            "Set the theme to dark mode for the current user.",
            "corr-preference",
            policyVersion);

    private static ActionRequest RebindPreview(ActionRequest request)
    {
        var canonicalPreview = ActionRequestValidator.ComputeCanonicalPreview(request);
        return request with
        {
            CanonicalPreview = canonicalPreview,
            PreviewDigest = ActionRequestValidator.ComputePreviewDigest(canonicalPreview)
        };
    }

    private static ActionBrokerService CreateService(
        SimulatedSystemState state,
        InMemoryJournalStore? store = null,
        TimeProvider? timeProvider = null) =>
        new(new TrustedActionCatalog(), new ActionBrokerPolicyEngine(), state, store ?? new InMemoryJournalStore(), timeProvider);

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        public DateTimeOffset CurrentTime { get; private set; } = initialTime;

        public override DateTimeOffset GetUtcNow() => CurrentTime;

        public void Advance(TimeSpan amount) => CurrentTime += amount;
    }

    private sealed class InMemoryJournalStore(Action<ActionJournalEntry>? onSave = null) : IActionBrokerJournalStore
    {
        private readonly object _sync = new();
        private readonly List<ActionJournalEntry> _entries = new();

        public ActionBrokerStateSnapshot? LastSnapshot { get; private set; }

        public IReadOnlyList<ActionJournalEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToArray();
                }
            }
        }

        public Task<IReadOnlyList<ActionJournalEntry>> LoadJournalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Entries);
        }

        public Task SaveJournalEntryAsync(ActionJournalEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onSave?.Invoke(entry);
            lock (_sync)
            {
                _entries.Add(entry);
            }

            return Task.CompletedTask;
        }

        public Task SaveStateSnapshotAsync(ActionBrokerStateSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public void Seed(ActionJournalEntry entry)
        {
            lock (_sync)
            {
                _entries.Add(entry);
            }
        }
    }
}
