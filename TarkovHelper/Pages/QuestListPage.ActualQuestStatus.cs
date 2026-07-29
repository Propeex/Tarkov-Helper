using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Pages;

public partial class QuestListPage
{
    private static readonly Brush AvailableBrush =
        new SolidColorBrush(Color.FromRgb(0x00, 0x96, 0x88));

    private bool _actualStatusEventsSubscribed;

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

        // Until the shared log scan completes, do not mislabel every eligible
        // quest as actually in progress. The map consumes this same status source.
        ApplyActualQuestStatuses();
        await ActualQuestStatusService.Instance.RefreshFromLogsAsync();
        ApplyActualQuestStatuses();
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

    private void ApplyActualQuestStatuses()
    {
        if (_isUnloaded || !_isDataLoaded)
            return;

        var evaluator = ActualQuestStatusService.Instance.CreateEvaluator();

        foreach (var viewModel in _allQuestViewModels)
        {
            ApplyQuestTitlePolicy(viewModel);

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

    private static void ApplyQuestTitlePolicy(QuestViewModel viewModel)
    {
        var koreanTitle = viewModel.Task.NameKo;
        var hasActualKoreanTitle = QuestContentTranslationService.ContainsHangul(koreanTitle);

        viewModel.DisplayName = hasActualKoreanTitle
            ? koreanTitle!.Trim()
            : viewModel.Task.Name;

        var showEnglishSubtitle = hasActualKoreanTitle &&
            !string.Equals(viewModel.DisplayName, viewModel.Task.Name, StringComparison.Ordinal);
        viewModel.SubtitleName = showEnglishSubtitle ? viewModel.Task.Name : string.Empty;
        viewModel.SubtitleVisibility = showEnglishSubtitle
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        ActualQuestStatusService.Instance.StatusChanged += ActualStatus_ProgressChanged;
        _loc.LanguageChanged += QuestTranslation_LanguageChanged;

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
        LstQuests.SelectionChanged += QuestTranslation_SelectionChanged;
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
        ActualQuestStatusService.Instance.StatusChanged -= ActualStatus_ProgressChanged;
        _loc.LanguageChanged -= QuestTranslation_LanguageChanged;

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
        LstQuests.SelectionChanged -= QuestTranslation_SelectionChanged;
    }

    private async void QuestTranslation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUnloaded || LstQuests.SelectedItem is not QuestViewModel selected)
            return;

        // 전체 퀘스트를 선행 번역하지 않고 사용자가 상세 화면을 연 퀘스트의
        // 영어 목표 문장만 번역합니다. 제목은 어떤 경우에도 수정하지 않습니다.
        await QuestContentTranslationService.Instance.TranslateMissingAsync(
            new[] { selected.Task });

        if (_isUnloaded || !ReferenceEquals(LstQuests.SelectedItem, selected))
            return;

        UpdateDetailPanel(selected);
    }

    private void QuestTranslation_LanguageChanged(object? sender, AppLanguage language)
    {
        ApplyActualQuestStatuses();
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
}
