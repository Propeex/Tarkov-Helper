using System.Windows;
using System.Windows.Threading;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Pages;

public partial class QuestListPage
{
    private static readonly ILogger _databaseRecoveryLog = Log.For<QuestListPage>();
    private bool _databaseRecoveryAttempted;
    private bool _eligibilityEventsSubscribed;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += ApplySafeDefaultsAndRecoverDatabaseAsync;
        Loaded += SubscribeEligibilityEvents;
        Unloaded += UnsubscribeEligibilityEvents;
    }

    private void SubscribeEligibilityEvents(object sender, RoutedEventArgs e)
    {
        if (_eligibilityEventsSubscribed)
            return;

        SettingsService.Instance.PlayerLevelChanged += OnEligibilitySettingChanged;
        SettingsService.Instance.ScavRepChanged += OnEligibilitySettingChanged;
        _eligibilityEventsSubscribed = true;
    }

    private void UnsubscribeEligibilityEvents(object sender, RoutedEventArgs e)
    {
        if (!_eligibilityEventsSubscribed)
            return;

        SettingsService.Instance.PlayerLevelChanged -= OnEligibilitySettingChanged;
        SettingsService.Instance.ScavRepChanged -= OnEligibilitySettingChanged;
        _eligibilityEventsSubscribed = false;
    }

    private void OnEligibilitySettingChanged(object? sender, int value)
    {
        Dispatcher.Invoke(RefreshEligibilityDisplay);
    }

    private void OnEligibilitySettingChanged(object? sender, double value)
    {
        Dispatcher.Invoke(RefreshEligibilityDisplay);
    }

    private void RefreshEligibilityDisplay()
    {
        RefreshQuestStatuses();
        ApplyStrictEligibilityStatuses();
        ApplyFilters();
        UpdateDetailPanel();
        UpdateRecommendations();
    }

    private async void ApplySafeDefaultsAndRecoverDatabaseAsync(object sender, RoutedEventArgs e)
    {
        // The normal working view is Active quests. Database recovery below is
        // responsible for distinguishing a genuinely empty cache from a filter result.
        if (CmbStatus.SelectedIndex != 0)
            CmbStatus.SelectedIndex = 0;

        if (_databaseRecoveryAttempted)
        {
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            ApplyStrictEligibilityStatuses();
            ApplyFilters();
            return;
        }

        _databaseRecoveryAttempted = true;

        // Let the page's normal Loaded handler finish first. Recovery is only a
        // fallback for the case where it retained an empty pre-update snapshot.
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);

        if (_isUnloaded || _progressService.AllTasks.Count > 0)
        {
            if (!_isUnloaded)
            {
                ApplyStrictEligibilityStatuses();
                ApplyFilters();
            }
            return;
        }

        _databaseRecoveryLog.Warning(
            $"Quest page received an empty progress snapshot. DB cache currently contains " +
            $"{QuestDbService.Instance.QuestCount} quests; attempting direct reload.");

        var loaded = await _progressService.InitializeFromDbAsync(ProfileType.Pvp);
        if (!loaded || _progressService.AllTasks.Count == 0)
        {
            var databaseCount = QuestDbService.Instance.QuestCount;
            TxtStats.Text = $"퀘스트 로드 실패 | DB: {databaseCount:N0}개 | 화면: 0개";
            _databaseRecoveryLog.Error(
                $"Quest database recovery failed. loaded={loaded}, database={databaseCount}, " +
                $"progress={_progressService.AllTasks.Count}");
            return;
        }

        _isInitializing = true;
        try
        {
            LoadQuests();
            PopulateTraderFilter();
            PopulateMapFilter();
            LoadFactionSelection();
            CmbStatus.SelectedIndex = 0;
            _isDataLoaded = true;
        }
        finally
        {
            _isInitializing = false;
        }

        ApplyStrictEligibilityStatuses();
        ApplyFilters();
        UpdateRecommendations();
        UpdateKappaGauge();

        _databaseRecoveryLog.Info(
            $"Quest page recovered {_progressService.AllTasks.Count} quests from the rebuilt database.");
    }

    /// <summary>
    /// Applies a conservative eligibility pass for the Active filter. The legacy
    /// status service previously skipped prerequisite rows whose referenced quest
    /// could not be resolved, which made downstream quests appear active too early.
    /// This pass treats unresolved or unsatisfied prerequisites as locked and also
    /// re-evaluates player-level and Scav-karma restrictions whenever settings change.
    /// </summary>
    private void ApplyStrictEligibilityStatuses()
    {
        if (_allQuestViewModels.Count == 0)
            return;

        var cache = new Dictionary<string, QuestStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var viewModel in _allQuestViewModels)
        {
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var status = EvaluateStrictStatus(viewModel.Task, cache, visiting);
            viewModel.Status = status;
            viewModel.StatusText = GetStatusText(status, viewModel.Task);
            viewModel.StatusBackground = GetStatusBrush(status);
            viewModel.CompleteButtonVisibility =
                status is QuestStatus.Active or QuestStatus.Locked or QuestStatus.LevelLocked
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private QuestStatus EvaluateStrictStatus(
        TarkovTask task,
        Dictionary<string, QuestStatus> cache,
        HashSet<string> visiting)
    {
        var key = task.Ids?.FirstOrDefault() ?? task.NormalizedName ?? task.Name;
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var baseStatus = _progressService.GetStatus(task);
        if (baseStatus is QuestStatus.Done or QuestStatus.Failed or QuestStatus.Unavailable)
        {
            cache[key] = baseStatus;
            return baseStatus;
        }

        if (!visiting.Add(key))
            return QuestStatus.Locked;

        try
        {
            if (!AreStrictPrerequisitesMet(task, cache, visiting))
                return cache[key] = QuestStatus.Locked;

            if (!_progressService.IsLevelRequirementMet(task) ||
                !_progressService.IsScavKarmaRequirementMet(task))
            {
                return cache[key] = QuestStatus.LevelLocked;
            }

            return cache[key] = baseStatus;
        }
        finally
        {
            visiting.Remove(key);
        }
    }

    private bool AreStrictPrerequisitesMet(
        TarkovTask task,
        Dictionary<string, QuestStatus> cache,
        HashSet<string> visiting)
    {
        if (task.TaskRequirements is { Count: > 0 })
        {
            var andRequirements = task.TaskRequirements.Where(requirement => requirement.GroupId == 0);
            foreach (var requirement in andRequirements)
            {
                if (!IsStrictRequirementSatisfied(requirement, cache, visiting))
                    return false;
            }

            foreach (var group in task.TaskRequirements
                         .Where(requirement => requirement.GroupId > 0)
                         .GroupBy(requirement => requirement.GroupId))
            {
                if (!group.Any(requirement => IsStrictRequirementSatisfied(requirement, cache, visiting)))
                    return false;
            }

            return true;
        }

        if (task.Previous is not { Count: > 0 })
            return true;

        foreach (var previous in task.Previous)
        {
            var prerequisite = _progressService.GetTask(previous) ?? _progressService.GetTaskById(previous);
            if (prerequisite == null ||
                EvaluateStrictStatus(prerequisite, cache, visiting) != QuestStatus.Done)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsStrictRequirementSatisfied(
        TaskRequirement requirement,
        Dictionary<string, QuestStatus> cache,
        HashSet<string> visiting)
    {
        var prerequisite = !string.IsNullOrWhiteSpace(requirement.TaskId)
            ? _progressService.GetTaskById(requirement.TaskId)
            : _progressService.GetTask(requirement.TaskNormalizedName);

        // Missing prerequisite data must never unlock a downstream quest.
        if (prerequisite == null)
            return false;

        var status = EvaluateStrictStatus(prerequisite, cache, visiting);
        var requiredStatuses = requirement.Status;
        if (requiredStatuses == null || requiredStatuses.Count == 0)
            return status == QuestStatus.Done;

        return requiredStatuses.Any(required => required.ToLowerInvariant() switch
        {
            "complete" => status == QuestStatus.Done,
            "failed" or "fail" => status == QuestStatus.Failed,
            "active" or "start" or "accept" => status is QuestStatus.Active or QuestStatus.Done,
            _ => false
        });
    }
}
