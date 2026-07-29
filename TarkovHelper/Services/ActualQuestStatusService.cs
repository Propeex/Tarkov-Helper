using System.IO;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Shared source of truth for the application's calculated quest status.
/// A quest is Active only when the game log says it was started and every
/// prerequisite, level, faction, edition and branch condition is still valid.
/// </summary>
public sealed class ActualQuestStatusService
{
    private static readonly ILogger _log = Log.For<ActualQuestStatusService>();
    private static ActualQuestStatusService? _instance;
    public static ActualQuestStatusService Instance => _instance ??= new ActualQuestStatusService();

    private readonly object _sync = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly HashSet<string> _startedQuestKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public event EventHandler? StatusChanged;

    private ActualQuestStatusService()
    {
        LogSyncService.Instance.QuestEventDetected += OnQuestEventDetected;
    }

    public async Task EnsureInitializedAsync()
    {
        if (_initialized)
            return;

        await _initializationGate.WaitAsync();
        try
        {
            if (_initialized)
                return;

            await RefreshFromLogsCoreAsync();
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task RefreshFromLogsAsync()
    {
        await _initializationGate.WaitAsync();
        try
        {
            await RefreshFromLogsCoreAsync();
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    internal ActualQuestStatusEvaluator CreateEvaluator()
    {
        string[] startedKeys;
        lock (_sync)
            startedKeys = _startedQuestKeys.ToArray();

        return new ActualQuestStatusEvaluator(
            QuestProgressService.Instance,
            startedKeys);
    }

    public QuestStatus GetStatus(TarkovTask task) => CreateEvaluator().Evaluate(task);

    private async Task RefreshFromLogsCoreAsync()
    {
        var logFolder = SettingsService.Instance.LogFolderPath;
        if (string.IsNullOrWhiteSpace(logFolder) || !Directory.Exists(logFolder))
        {
            lock (_sync)
                _startedQuestKeys.Clear();

            _log.Warning(
                "Quest log folder is unavailable; map markers will not treat eligible quests as active.");
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            var result = await LogSyncService.Instance.SyncFromLogsAsync(
                logFolder,
                progress: null,
                daysRange: 0);

            lock (_sync)
            {
                _startedQuestKeys.Clear();
                foreach (var task in result.InProgressQuests)
                    AddTaskKeys(task);
            }

            _log.Info(
                $"Calculated shared quest status from {result.TotalEventsFound} log events: " +
                $"{result.InProgressQuests.Count} active quests.");
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            // A game log can be temporarily locked while EFT is writing it. Clearing
            // the last valid set here made every marker disappear until the next full
            // scan. Preserve the last successful status and do not publish a false
            // empty-state update on transient I/O or parsing failures.
            _log.Error(
                "Failed to calculate shared quest status from logs; preserving the last successful active set.",
                exception);
        }
    }

    private void OnQuestEventDetected(object? sender, QuestLogEvent e)
    {
        var progressService = QuestProgressService.Instance;
        var task = progressService.GetTaskByBsgId(e.QuestId) ??
                   progressService.GetTaskById(e.QuestId);
        if (task == null)
            return;

        lock (_sync)
        {
            if (e.EventType == QuestEventType.Started)
                AddTaskKeys(task);
            else
                RemoveTaskKeys(task);
        }

        _initialized = true;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddTaskKeys(TarkovTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.NormalizedName))
            _startedQuestKeys.Add(task.NormalizedName);

        foreach (var id in task.Ids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
                _startedQuestKeys.Add(id);
        }
    }

    private void RemoveTaskKeys(TarkovTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.NormalizedName))
            _startedQuestKeys.Remove(task.NormalizedName);

        foreach (var id in task.Ids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
                _startedQuestKeys.Remove(id);
        }
    }
}