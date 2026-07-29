using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Pages;

public partial class QuestListPage
{
    private static readonly ILogger _actualStatusLog = Log.For<QuestListPage>();
    private static readonly Brush AvailableBrush =
        new SolidColorBrush(Color.FromRgb(0x00, 0x96, 0x88));

    private readonly HashSet<string> _actuallyStartedQuestKeys =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _actualStatusEventsSubscribed;
    private bool _actualStatusRefreshRunning;

    private void InitializeActualQuestStatusTracking()
    {
        Loaded += ActualQuestStatus_Loaded;
        Unloaded += ActualQuestStatus_Unloaded;
    }

    private async void ActualQuestStatus_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeActualStatusEvents();
        EnsureAvailableStatusFilter();

        await WaitForQuestDataAsync();
        if (_isUnloaded)
            return;

        // Until the log scan completes, do not mislabel every eligible quest as
        // actually in progress.
        ApplyActualQuestStatuses();
        await RefreshActuallyStartedQuestsAsync();
    }

    private void ActualQuestStatus_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeActualStatusEvents();
    }

    private async Task WaitForQuestDataAsync()
    {
        for (var attempt = 0; attempt < 100 && !_isDataLoaded && !_isUnloaded; attempt++)
            await Task.Delay(50);
    }

    private async Task RefreshActuallyStartedQuestsAsync()
    {
        if (_actualStatusRefreshRunning)
            return;

        _actualStatusRefreshRunning = true;
        try
        {
            var logFolder = SettingsService.Instance.LogFolderPath;
            if (string.IsNullOrWhiteSpace(logFolder) || !Directory.Exists(logFolder))
            {
                _actuallyStartedQuestKeys.Clear();
                ApplyActualQuestStatuses();
                _actualStatusLog.Warning(
                    "Quest log folder is unavailable; eligible quests are shown as Available rather than Active.");
                return;
            }

            var result = await LogSyncService.Instance.SyncFromLogsAsync(
                logFolder,
                progress: null,
                daysRange: 0);

            _actuallyStartedQuestKeys.Clear();
            foreach (var task in result.InProgressQuests)
                AddStartedQuestKeys(task);

            ApplyActualQuestStatuses();
            _actualStatusLog.Info(
                $"Actual quest status scan found {result.InProgressQuests.Count} in-progress quests " +
                $"from {result.TotalEventsFound} log events.");
        }
        catch (Exception exception)
        {
            _actualStatusLog.Error("Failed to calculate actual in-progress quests from logs.", exception);
            ApplyActualQuestStatuses();
        }
        finally
        {
            _actualStatusRefreshRunning = false;
        }
    }

    private void ApplyActualQuestStatuses()
    {
        if (_isUnloaded || !_isDataLoaded)
            return;

        var evaluator = new ActualQuestStatusEvaluator(
            _progressService,
            _actuallyStartedQuestKeys);

        foreach (var viewModel in _allQuestViewModels)
        {
            var status = evaluator.Evaluate(viewModel.Task);
            viewModel.Status = status;
            viewModel.StatusText = status == QuestStatus.Available
                ? "수주 가능"
                : GetStatusText(status, viewModel.Task);
            viewModel.StatusBackground = status == QuestStatus.Available
                ? AvailableBrush
                : GetStatusBrush(status);
            viewModel.CompleteButtonVisibility =
                status is QuestStatus.Active or QuestStatus.Available or
                    QuestStatus.Locked or QuestStatus.LevelLocked
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        ApplyFilters();
        UpdateActualQuestStatistics();
    }

    private void EnsureAvailableStatusFilter()
    {
        var alreadyExists = CmbStatus.Items
            .OfType<ComboBoxItem>()
            .Any(item => string.Equals(
                item.Tag?.ToString(),
                nameof(QuestStatus.Available),
                StringComparison.OrdinalIgnoreCase));

        if (!alreadyExists)
        {
            // Append to preserve the existing indices: Active=0 and All=1.
            CmbStatus.Items.Add(new ComboBoxItem
            {
                Content = "수주 가능",
                Tag = nameof(QuestStatus.Available)
            });
        }
    }

    private void UpdateActualQuestStatistics()
    {
        if (_isUnloaded || !_isDataLoaded)
            return;

        var filteredCount = LstQuests.ItemsSource is IEnumerable<QuestViewModel> filtered
            ? filtered.Count()
            : 0;

        var active = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Active);
        var available = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Available);
        var locked = _allQuestViewModels.Count(vm =>
            vm.Status is QuestStatus.Locked or QuestStatus.LevelLocked);
        var done = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Done);
        var failed = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Failed);
        var unavailable = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Unavailable);
        var playerLevel = SettingsService.Instance.PlayerLevel;

        TxtStats.Text =
            $"레벨 {playerLevel} | {_allQuestViewModels.Count}개 중 {filteredCount}개 표시 중 | " +
            $"진행 중: {active} | 수주 가능: {available} | 잠김: {locked} | " +
            $"완료: {done} | 실패: {failed} | 불가: {unavailable}";
    }

    private void AddStartedQuestKeys(TarkovTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.NormalizedName))
            _actuallyStartedQuestKeys.Add(task.NormalizedName);

        foreach (var id in task.Ids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
                _actuallyStartedQuestKeys.Add(id);
        }
    }

    private void RemoveStartedQuestKeys(TarkovTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.NormalizedName))
            _actuallyStartedQuestKeys.Remove(task.NormalizedName);

        foreach (var id in task.Ids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
                _actuallyStartedQuestKeys.Remove(id);
        }
    }

    private void SubscribeActualStatusEvents()
    {
        if (_actualStatusEventsSubscribed)
            return;

        _actualStatusEventsSubscribed = true;
        _progressService.ProgressChanged += ActualStatus_ProgressChanged;
        SettingsService.Instance.PlayerLevelChanged += ActualStatus_PlayerLevelChanged;
        SettingsService.Instance.ScavRepChanged += ActualStatus_ScavRepChanged;
        SettingsService.Instance.PlayerFactionChanged += ActualStatus_PlayerFactionChanged;
        SettingsService.Instance.HasEodEditionChanged += ActualStatus_BoolSettingChanged;
        SettingsService.Instance.HasUnheardEditionChanged += ActualStatus_BoolSettingChanged;
        SettingsService.Instance.PrestigeLevelChanged += ActualStatus_IntSettingChanged;
        SettingsService.Instance.DspDecodeCountChanged += ActualStatus_IntSettingChanged;
        LogSyncService.Instance.QuestEventDetected += ActualStatus_QuestEventDetected;

        TxtSearch.TextChanged += ActualStatus_FilterChanged;
        ChkKappaOnly.Checked += ActualStatus_FilterChanged;
        ChkKappaOnly.Unchecked += ActualStatus_FilterChanged;
        ChkItemRequired.Checked += ActualStatus_FilterChanged;
        ChkItemRequired.Unchecked += ActualStatus_FilterChanged;
        CmbTrader.SelectionChanged += ActualStatus_FilterChanged;
        CmbMap.SelectionChanged += ActualStatus_FilterChanged;
        CmbStatus.SelectionChanged += ActualStatus_FilterChanged;
        RbBear.Checked += ActualStatus_FilterChanged;
        RbBear.Unchecked += ActualStatus_FilterChanged;
        RbUsec.Checked += ActualStatus_FilterChanged;
        RbUsec.Unchecked += ActualStatus_FilterChanged;
    }

    private void UnsubscribeActualStatusEvents()
    {
        if (!_actualStatusEventsSubscribed)
            return;

        _actualStatusEventsSubscribed = false;
        _progressService.ProgressChanged -= ActualStatus_ProgressChanged;
        SettingsService.Instance.PlayerLevelChanged -= ActualStatus_PlayerLevelChanged;
        SettingsService.Instance.ScavRepChanged -= ActualStatus_ScavRepChanged;
        SettingsService.Instance.PlayerFactionChanged -= ActualStatus_PlayerFactionChanged;
        SettingsService.Instance.HasEodEditionChanged -= ActualStatus_BoolSettingChanged;
        SettingsService.Instance.HasUnheardEditionChanged -= ActualStatus_BoolSettingChanged;
        SettingsService.Instance.PrestigeLevelChanged -= ActualStatus_IntSettingChanged;
        SettingsService.Instance.DspDecodeCountChanged -= ActualStatus_IntSettingChanged;
        LogSyncService.Instance.QuestEventDetected -= ActualStatus_QuestEventDetected;

        TxtSearch.TextChanged -= ActualStatus_FilterChanged;
        ChkKappaOnly.Checked -= ActualStatus_FilterChanged;
        ChkKappaOnly.Unchecked -= ActualStatus_FilterChanged;
        ChkItemRequired.Checked -= ActualStatus_FilterChanged;
        ChkItemRequired.Unchecked -= ActualStatus_FilterChanged;
        CmbTrader.SelectionChanged -= ActualStatus_FilterChanged;
        CmbMap.SelectionChanged -= ActualStatus_FilterChanged;
        CmbStatus.SelectionChanged -= ActualStatus_FilterChanged;
        RbBear.Checked -= ActualStatus_FilterChanged;
        RbBear.Unchecked -= ActualStatus_FilterChanged;
        RbUsec.Checked -= ActualStatus_FilterChanged;
        RbUsec.Unchecked -= ActualStatus_FilterChanged;
    }

    private void ActualStatus_ProgressChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(
            new Action(ApplyActualQuestStatuses),
            DispatcherPriority.ContextIdle);
    }

    private void ActualStatus_PlayerLevelChanged(object? sender, int value) =>
        QueueActualStatusRefresh();

    private void ActualStatus_ScavRepChanged(object? sender, double value) =>
        QueueActualStatusRefresh();

    private void ActualStatus_PlayerFactionChanged(object? sender, string? value) =>
        QueueActualStatusRefresh();

    private void ActualStatus_BoolSettingChanged(object? sender, bool value) =>
        QueueActualStatusRefresh();

    private void ActualStatus_IntSettingChanged(object? sender, int value) =>
        QueueActualStatusRefresh();

    private void QueueActualStatusRefresh()
    {
        Dispatcher.BeginInvoke(
            new Action(ApplyActualQuestStatuses),
            DispatcherPriority.ContextIdle);
    }

    private void ActualStatus_FilterChanged(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            new Action(UpdateActualQuestStatistics),
            DispatcherPriority.ContextIdle);
    }

    private void ActualStatus_QuestEventDetected(object? sender, QuestLogEvent e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var task = _progressService.GetTaskByBsgId(e.QuestId) ??
                       _progressService.GetTaskById(e.QuestId);
            if (task == null)
                return;

            if (e.EventType == QuestEventType.Started)
                AddStartedQuestKeys(task);
            else
                RemoveStartedQuestKeys(task);

            ApplyActualQuestStatuses();
        });
    }
}
