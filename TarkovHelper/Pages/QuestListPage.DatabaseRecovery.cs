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

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += ApplySafeDefaultsAndRecoverDatabaseAsync;
    }

    private async void ApplySafeDefaultsAndRecoverDatabaseAsync(object sender, RoutedEventArgs e)
    {
        // Showing only Active quests by default can make a correctly loaded
        // database appear empty. Start from the complete list instead.
        if (CmbStatus.SelectedIndex != 1)
            CmbStatus.SelectedIndex = 1;

        if (_databaseRecoveryAttempted)
            return;

        _databaseRecoveryAttempted = true;

        // Let the page's normal Loaded handler finish first. Recovery is only a
        // fallback for the case where it retained an empty pre-update snapshot.
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);

        if (_isUnloaded || _progressService.AllTasks.Count > 0)
        {
            if (!_isUnloaded)
                ApplyFilters();
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
            CmbStatus.SelectedIndex = 1;
            _isDataLoaded = true;
        }
        finally
        {
            _isInitializing = false;
        }

        ApplyFilters();
        UpdateRecommendations();
        UpdateKappaGauge();

        _databaseRecoveryLog.Info(
            $"Quest page recovered {_progressService.AllTasks.Count} quests from the rebuilt database.");
    }
}
