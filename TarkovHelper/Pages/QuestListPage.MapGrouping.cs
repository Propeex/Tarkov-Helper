using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TarkovHelper.Pages;

/// <summary>
/// 퀘스트 데이터가 동일한 실제 맵의 조건별 변형 키를 별도로 제공하더라도
/// 지도 필터에서는 기본 맵 하나로 묶어 표시합니다.
/// </summary>
public partial class QuestListPage
{
    private static readonly bool QuestMapGroupingHandlersRegistered = RegisterQuestMapGroupingHandlers();
    private bool _questMapGroupingAttached;
    private bool _questMapGroupingApplying;

    private static bool RegisterQuestMapGroupingHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(QuestListPage),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnQuestMapGroupingLoaded));
        EventManager.RegisterClassHandler(
            typeof(QuestListPage),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnQuestMapGroupingUnloaded));
        return true;
    }

    private static void OnQuestMapGroupingLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is QuestListPage page)
        {
            page.AttachQuestMapGrouping();
            page.Dispatcher.BeginInvoke(
                page.NormalizeQuestMapGroupsWhenReady,
                DispatcherPriority.ContextIdle);
        }
    }

    private static void OnQuestMapGroupingUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is QuestListPage page)
            page.DetachQuestMapGrouping();
    }

    private void AttachQuestMapGrouping()
    {
        if (_questMapGroupingAttached)
            return;

        _questMapGroupingAttached = true;
        QuestDbService.Instance.DataRefreshed += OnQuestMapGroupingDatabaseRefreshed;
    }

    private void DetachQuestMapGrouping()
    {
        if (!_questMapGroupingAttached)
            return;

        _questMapGroupingAttached = false;
        QuestDbService.Instance.DataRefreshed -= OnQuestMapGroupingDatabaseRefreshed;
    }

    private void OnQuestMapGroupingDatabaseRefreshed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(
            NormalizeQuestMapGroupsWhenReady,
            DispatcherPriority.ContextIdle);
    }

    private async void NormalizeQuestMapGroupsWhenReady()
    {
        if (_questMapGroupingApplying)
            return;

        for (var attempt = 0; attempt < 120 && !_isDataLoaded; attempt++)
            await Task.Delay(25);

        if (!_isDataLoaded || _questMapGroupingApplying)
            return;

        _questMapGroupingApplying = true;
        try
        {
            var selectedMap = NormalizeQuestMapKey(
                (CmbMap.SelectedItem as ComboBoxItem)?.Tag?.ToString());

            foreach (var task in _progressService.AllTasks)
            {
                if (task.Maps == null || task.Maps.Count == 0)
                    continue;

                task.Maps = task.Maps
                    .Select(NormalizeQuestMapKey)
                    .Where(map => !string.IsNullOrWhiteSpace(map))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(map => map, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            _maps = _progressService.AllTasks
                .Where(task => task.Maps != null)
                .SelectMany(task => task.Maps!)
                .Where(map => !string.IsNullOrWhiteSpace(map))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(map => map, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _isInitializing = true;
            try
            {
                PopulateMapFilter();
                SelectGroupedMapFilter(selectedMap);
                PersistedQuestFilters.Map = selectedMap;
            }
            finally
            {
                _isInitializing = false;
            }

            ApplyFilters();
        }
        finally
        {
            _questMapGroupingApplying = false;
        }
    }

    private void SelectGroupedMapFilter(string selectedMap)
    {
        if (string.IsNullOrWhiteSpace(selectedMap))
        {
            CmbMap.SelectedIndex = 0;
            return;
        }

        var target = CmbMap.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                selectedMap,
                StringComparison.OrdinalIgnoreCase));

        CmbMap.SelectedItem = target ?? CmbMap.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static string NormalizeQuestMapKey(string? mapKey)
    {
        if (string.IsNullOrWhiteSpace(mapKey))
            return string.Empty;

        var normalized = mapKey.Trim().ToLowerInvariant();

        // Ground Zero의 레벨 조건별 데이터 키는 모두 Ground Zero로 통합합니다.
        if (normalized == "ground-zero" ||
            normalized.StartsWith("ground-zero-", StringComparison.Ordinal))
        {
            return "ground-zero";
        }

        // Factory의 주·야간/구버전 데이터 키는 모두 Factory로 통합합니다.
        if (normalized == "factory" ||
            normalized == "night-factory" ||
            normalized == "factory-night" ||
            normalized == "factory-day" ||
            normalized == "factory4_day" ||
            normalized == "factory4_night")
        {
            return "factory";
        }

        return normalized;
    }
}
