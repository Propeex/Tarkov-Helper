using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TarkovHelper.Pages;

public partial class QuestListPage
{
    private static readonly QuestFilterState PersistedQuestFilters = new();
    private static readonly bool PersistentQuestFiltersRegistered = RegisterPersistentQuestFilters();
    private bool _persistentQuestFilterHooksAttached;

    private static bool RegisterPersistentQuestFilters()
    {
        EventManager.RegisterClassHandler(
            typeof(QuestListPage),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnPersistentQuestFiltersLoaded));
        return true;
    }

    private static void OnPersistentQuestFiltersLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is QuestListPage page)
            page.Dispatcher.BeginInvoke(
                page.RestorePersistentQuestFiltersWhenReady,
                DispatcherPriority.ContextIdle);
    }

    private async void RestorePersistentQuestFiltersWhenReady()
    {
        for (var attempt = 0; attempt < 100 && !_isDataLoaded; attempt++)
            await Task.Delay(25);

        if (!_isDataLoaded)
            return;

        _isInitializing = true;
        try
        {
            // 카파 진행도와 목록 배지는 유지하되, 별도 체크 필터는 제거합니다.
            ChkKappaOnly.IsChecked = false;
            ChkKappaOnly.IsEnabled = false;
            ChkKappaOnly.Visibility = Visibility.Collapsed;

            ChkItemRequired.IsChecked = PersistedQuestFilters.ItemRequired;
            SelectComboItemByTag(CmbTrader, PersistedQuestFilters.Trader);
            SelectComboItemByTag(CmbMap, PersistedQuestFilters.Map);
            SelectComboItemByTag(CmbStatus, PersistedQuestFilters.Status);
        }
        finally
        {
            _isInitializing = false;
        }

        AttachPersistentQuestFilterHooks();
        ApplyFilters();
    }

    private void AttachPersistentQuestFilterHooks()
    {
        if (_persistentQuestFilterHooksAttached)
            return;

        _persistentQuestFilterHooksAttached = true;
        ChkItemRequired.Checked += SavePersistentQuestFilters;
        ChkItemRequired.Unchecked += SavePersistentQuestFilters;
        CmbTrader.SelectionChanged += SavePersistentQuestFilters;
        CmbMap.SelectionChanged += SavePersistentQuestFilters;
        CmbStatus.SelectionChanged += SavePersistentQuestFilters;
    }

    private void SavePersistentQuestFilters(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        PersistedQuestFilters.ItemRequired = ChkItemRequired.IsChecked == true;
        PersistedQuestFilters.Trader = GetSelectedTag(CmbTrader, string.Empty);
        PersistedQuestFilters.Map = GetSelectedTag(CmbMap, string.Empty);
        PersistedQuestFilters.Status = GetSelectedTag(CmbStatus, "All");
    }

    private static string GetSelectedTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectComboItemByTag(ComboBox comboBox, string tag)
    {
        var target = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString() ?? string.Empty,
                tag,
                StringComparison.OrdinalIgnoreCase));

        comboBox.SelectedItem = target ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private sealed class QuestFilterState
    {
        public bool ItemRequired { get; set; }
        public string Trader { get; set; } = string.Empty;
        public string Map { get; set; } = string.Empty;
        public string Status { get; set; } = "All";
    }
}
