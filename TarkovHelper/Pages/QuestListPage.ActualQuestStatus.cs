using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Pages;

public partial class QuestListPage
{
    private bool _actualStatusEventsSubscribed;

    private void InitializeActualQuestStatusTracking()
    {
        Loaded += ActualQuestStatus_Loaded;
        Unloaded += ActualQuestStatus_Unloaded;
    }

    private async void ActualQuestStatus_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeActualStatusEvents();
        await WaitForQuestDataAsync();
        if (_isUnloaded)
            return;

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
            viewModel.StatusText = GetStatusText(status, viewModel.Task);
            viewModel.StatusBackground = GetStatusBrush(status);
            viewModel.CompleteButtonVisibility = status == QuestStatus.Active
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        ApplyFilters();
        ApplySelectedQuestDetailTitlePolicy();
        UpdateActualQuestStatistics();
    }

    private static void ApplyQuestTitlePolicy(QuestViewModel viewModel)
    {
        var koreanTitle = viewModel.Task.NameKo;
        var hasOfficialKoreanTitle = QuestKoreanSourcePolicy.ContainsHangul(koreanTitle);

        viewModel.DisplayName = QuestKoreanSourcePolicy.SelectQuestTitle(
            viewModel.Task.Name,
            koreanTitle);

        var showEnglishSubtitle = hasOfficialKoreanTitle &&
            !string.Equals(viewModel.DisplayName, viewModel.Task.Name, StringComparison.Ordinal);
        viewModel.SubtitleName = showEnglishSubtitle ? viewModel.Task.Name : string.Empty;
        viewModel.SubtitleVisibility = showEnglishSubtitle
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplySelectedQuestDetailTitlePolicy()
    {
        if (LstQuests.SelectedItem is not QuestViewModel selected)
            return;

        var koreanTitle = selected.Task.NameKo;
        var hasOfficialKoreanTitle = QuestKoreanSourcePolicy.ContainsHangul(koreanTitle);
        var displayedTitle = QuestKoreanSourcePolicy.SelectQuestTitle(
            selected.Task.Name,
            koreanTitle);

        TxtDetailName.Text = displayedTitle;

        var showEnglishSubtitle = hasOfficialKoreanTitle &&
            !string.Equals(displayedTitle, selected.Task.Name, StringComparison.Ordinal);
        TxtDetailSubtitle.Text = showEnglishSubtitle ? selected.Task.Name : string.Empty;
        TxtDetailSubtitle.Visibility = showEnglishSubtitle
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateActualQuestStatistics()
    {
        if (_isUnloaded || !_isDataLoaded)
            return;

        var filteredCount = LstQuests.ItemsSource is IEnumerable<QuestViewModel> filtered
            ? filtered.Count()
            : 0;

        var active = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Active);
        var locked = _allQuestViewModels.Count(vm =>
            vm.Status is QuestStatus.Locked or QuestStatus.LevelLocked);
        var done = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Done);
        var failed = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Failed);
        var unavailable = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Unavailable);
        var playerLevel = SettingsService.Instance.PlayerLevel;

        TxtStats.Text =
            $"레벨 {playerLevel} | {_allQuestViewModels.Count}개 중 {filteredCount}개 표시 중 | " +
            $"진행 중: {active} | 잠김: {locked} | " +
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
        _loc.LanguageChanged += QuestLocalization_LanguageChanged;

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
        _loc.LanguageChanged -= QuestLocalization_LanguageChanged;

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

    private void QuestLocalization_LanguageChanged(object? sender, AppLanguage language)
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
