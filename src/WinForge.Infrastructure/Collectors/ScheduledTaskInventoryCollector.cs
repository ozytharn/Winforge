using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;

namespace WinForge.Infrastructure.Collectors;

public sealed class ScheduledTaskInventoryCollector : IInventoryCollector
{
    private const int TaskStateDisabled = 1;
    private const int TaskStateQueued = 2;
    private const int TaskStateReady = 3;
    private const int TaskStateRunning = 4;
    private const int TriggerTypeBoot = 8;
    private const int TriggerTypeLogon = 9;
    private const int ActionTypeExecute = 0;
    private readonly ILogger<ScheduledTaskInventoryCollector> _logger;

    public ScheduledTaskInventoryCollector(ILogger<ScheduledTaskInventoryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "scheduled-tasks";

    public InventoryScanGroup ScanGroup => InventoryScanGroup.Software;

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    private CollectorResult Collect(CancellationToken cancellationToken)
    {
        var components = new List<InventoryComponent>();
        var issues = new List<CollectorIssue>();
        if (!OperatingSystem.IsWindows())
        {
            return new CollectorResult(
                CollectorId,
                components,
                [new CollectorIssue(CollectorId, "unsupported_platform", "Scheduled task inventory is only available on Windows.")]);
        }

        object? service = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null)
            {
                return new CollectorResult(
                    CollectorId,
                    components,
                    [new CollectorIssue(CollectorId, "task_scheduler_unsupported", "The Windows Task Scheduler API is unavailable.")]);
            }

            service = Activator.CreateInstance(serviceType);
            if (service is null)
            {
                return new CollectorResult(
                    CollectorId,
                    components,
                    [new CollectorIssue(CollectorId, "task_scheduler_unavailable", "The Windows Task Scheduler API could not be opened.")]);
            }

            Invoke(service, "Connect");
            object? rootFolder = Invoke(service, "GetFolder", "\\");
            if (rootFolder is null)
            {
                return new CollectorResult(
                    CollectorId,
                    components,
                    [new CollectorIssue(CollectorId, "task_scheduler_root_unavailable", "The Task Scheduler root folder could not be read.")]);
            }

            try
            {
                ReadFolder(rootFolder, 0, components, issues, cancellationToken);
            }
            finally
            {
                Release(rootFolder);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (COMException exception)
        {
            _logger.LogWarning(exception, "Task Scheduler inventory could not be read.");
            issues.Add(new CollectorIssue(
                CollectorId,
                "task_scheduler_inaccessible",
                "Windows denied access to some or all scheduled tasks."));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is COMException comException)
        {
            _logger.LogWarning(comException, "Task Scheduler inventory could not be read.");
            issues.Add(new CollectorIssue(
                CollectorId,
                "task_scheduler_inaccessible",
                "Windows denied access to some or all scheduled tasks."));
        }
        catch (InvalidComObjectException exception)
        {
            _logger.LogWarning(exception, "Task Scheduler returned an invalid COM object.");
            issues.Add(new CollectorIssue(
                CollectorId,
                "task_scheduler_unavailable",
                "The Task Scheduler returned incomplete or unavailable task data."));
        }
        finally
        {
            Release(service);
        }

        if (components.Count == 0 && issues.Count == 0)
        {
            issues.Add(new CollectorIssue(CollectorId, "no_tasks_reported", "The Task Scheduler reported no readable tasks."));
        }

        return new CollectorResult(CollectorId, components, issues);
    }

    private void ReadFolder(
        object folder,
        int depth,
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > 32)
        {
            issues.Add(new CollectorIssue(
                CollectorId,
                "task_folder_depth_exceeded",
                "A scheduled-task folder exceeded the supported nesting depth."));
            return;
        }

        object? tasks = null;
        try
        {
            tasks = Invoke(folder, "GetTasks", 1);
            var count = ReadInteger(tasks, "Count") ?? 0;
            for (var index = 1; index <= count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? task = null;
                try
                {
                    task = Invoke(tasks!, "Item", index);
                    if (task is not null)
                    {
                        components.Add(CreateComponent(task));
                    }
                }
                catch (COMException exception)
                {
                    _logger.LogDebug(exception, "Scheduled task at index {TaskIndex} could not be read.", index);
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "scheduled_task_inaccessible",
                        "A scheduled task changed or denied access during collection."));
                }
                finally
                {
                    Release(task);
                }
            }
        }
        catch (COMException exception)
        {
            _logger.LogDebug(exception, "Tasks in a Task Scheduler folder could not be read.");
            issues.Add(new CollectorIssue(
                CollectorId,
                "scheduled_task_folder_inaccessible",
                "A scheduled-task folder could not be read."));
        }
        finally
        {
            Release(tasks);
        }

        object? folders = null;
        try
        {
            folders = Invoke(folder, "GetFolders", 0);
            var count = ReadInteger(folders, "Count") ?? 0;
            for (var index = 1; index <= count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? child = null;
                try
                {
                    child = Invoke(folders!, "Item", index);
                    if (child is not null)
                    {
                        ReadFolder(child, depth + 1, components, issues, cancellationToken);
                    }
                }
                catch (COMException exception)
                {
                    _logger.LogDebug(exception, "A Task Scheduler folder could not be read.");
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "scheduled_task_folder_inaccessible",
                        "A scheduled-task folder could not be read."));
                }
                finally
                {
                    Release(child);
                }
            }
        }
        catch (COMException exception)
        {
            _logger.LogDebug(exception, "Task Scheduler folders could not be enumerated.");
            issues.Add(new CollectorIssue(
                CollectorId,
                "scheduled_task_folders_unavailable",
                "Some scheduled-task folders could not be enumerated."));
        }
        finally
        {
            Release(folders);
        }
    }

    private static InventoryComponent CreateComponent(object task)
    {
        object? definition = null;
        object? registrationInfo = null;
        object? principal = null;
        object? triggers = null;
        object? actions = null;
        try
        {
            var taskPath = ReadString(task, "Path");
            var name = ReadString(task, "Name");
            var state = ReadInteger(task, "State");
            definition = ReadProperty(task, "Definition");
            registrationInfo = ReadProperty(definition, "RegistrationInfo");
            principal = ReadProperty(definition, "Principal");
            triggers = ReadProperty(definition, "Triggers");
            actions = ReadProperty(definition, "Actions");

            var triggerDetails = ReadTriggers(triggers, out var isStartupTask);
            var actionDetails = ReadActions(actions);
            var lastRun = ReadString(task, "LastRunTime");
            var nextRun = ReadString(task, "NextRunTime");
            var details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["taskName"] = name,
                ["taskPath"] = taskPath,
                ["author"] = ReadString(registrationInfo, "Author"),
                ["description"] = null,
                ["state"] = MapTaskState(state),
                ["stateCode"] = state?.ToString(CultureInfo.InvariantCulture),
                ["triggers"] = triggerDetails,
                ["actions"] = actionDetails,
                ["principalUserId"] = ReadString(principal, "UserId"),
                ["principalGroupId"] = ReadString(principal, "GroupId"),
                ["principalLogonType"] = ReadString(principal, "LogonType"),
                ["principalRunLevel"] = ReadString(principal, "RunLevel"),
                ["lastRunTime"] = lastRun,
                ["nextRunTime"] = nextRun,
                ["isStartupTask"] = isStartupTask ? "true" : "false"
            };
            var availability = details.ToDictionary(
                detail => detail.Key,
                detail => detail.Key switch
                {
                    "description" => InventoryFieldAvailability.Unsupported,
                    "actions" => string.IsNullOrWhiteSpace(detail.Value)
                        ? InventoryFieldAvailability.Unknown
                        : InventoryFieldAvailability.Available,
                    "isStartupTask" => InventoryFieldAvailability.Available,
                    _ => string.IsNullOrWhiteSpace(detail.Value)
                        ? InventoryFieldAvailability.Missing
                        : InventoryFieldAvailability.Available
                },
                StringComparer.Ordinal);

            return new InventoryComponent(
                InventoryComponentType.ScheduledTask,
                string.IsNullOrWhiteSpace(taskPath) ? name ?? Guid.NewGuid().ToString("N") : taskPath,
                name ?? taskPath ?? "Scheduled task",
                "Windows Task Scheduler COM API",
                2,
                DateTimeOffset.UtcNow,
                details)
            {
                Category = isStartupTask ? "Startup tasks" : "Scheduled tasks",
                FieldAvailability = availability
            };
        }
        finally
        {
            Release(actions);
            Release(triggers);
            Release(principal);
            Release(registrationInfo);
            Release(definition);
        }
    }

    private static string? ReadTriggers(object? triggers, out bool isStartupTask)
    {
        isStartupTask = false;
        var count = ReadInteger(triggers, "Count") ?? 0;
        var values = new List<string>(count);
        for (var index = 1; index <= count; index++)
        {
            object? trigger = null;
            try
            {
                trigger = Invoke(triggers!, "Item", index);
                var type = ReadInteger(trigger, "Type");
                if (type is TriggerTypeBoot or TriggerTypeLogon)
                {
                    isStartupTask = true;
                }

                var id = ReadString(trigger, "Id");
                var startBoundary = ReadString(trigger, "StartBoundary");
                var typeName = type switch
                {
                    TriggerTypeBoot => "Boot",
                    TriggerTypeLogon => "Logon",
                    7 => "Registration",
                    11 => "Session state change",
                    _ => type?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"
                };
                values.Add(string.Join(
                    ", ",
                    new[] { typeName, id, startBoundary }.Where(value => !string.IsNullOrWhiteSpace(value))));
            }
            catch (COMException)
            {
                values.Add("Unavailable");
            }
            finally
            {
                Release(trigger);
            }
        }

        return values.Count == 0 ? null : string.Join(" | ", values);
    }

    private static string? ReadActions(object? actions)
    {
        var count = ReadInteger(actions, "Count") ?? 0;
        var values = new List<string>(count);
        for (var index = 1; index <= count; index++)
        {
            object? action = null;
            try
            {
                action = Invoke(actions!, "Item", index);
                var type = ReadInteger(action, "Type");
                var typeName = type == ActionTypeExecute ? "Execute" : type?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
                var path = type == ActionTypeExecute ? ReadString(action, "Path") : null;
                values.Add(string.Join(", ", new[] { typeName, path }.Where(value => !string.IsNullOrWhiteSpace(value))));
            }
            catch (COMException)
            {
                values.Add("Unavailable");
            }
            finally
            {
                Release(action);
            }
        }

        return values.Count == 0 ? null : string.Join(" | ", values);
    }

    private static string? MapTaskState(int? state) =>
        state switch
        {
            TaskStateDisabled => "Disabled",
            TaskStateQueued => "Queued",
            TaskStateReady => "Ready",
            TaskStateRunning => "Running",
            null => null,
            _ => $"Unknown ({state.Value})"
        };

    private static object? Invoke(object target, string member, params object?[] arguments)
    {
        try
        {
            return target.GetType().InvokeMember(
                member,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                target,
                arguments,
                CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is COMException comException)
        {
            throw comException;
        }
    }

    private static object? ReadProperty(object? target, string propertyName)
    {
        if (target is null)
        {
            return null;
        }

        try
        {
            return target.GetType().InvokeMember(
                propertyName,
                BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                target,
                args: null,
                CultureInfo.InvariantCulture);
        }
        catch (COMException)
        {
            return null;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is COMException)
        {
            return null;
        }
    }

    private static string? ReadString(object? target, string propertyName) =>
        Convert.ToString(ReadProperty(target, propertyName), CultureInfo.InvariantCulture);

    private static int? ReadInteger(object? target, string propertyName)
    {
        var value = ReadProperty(target, propertyName);
        return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
