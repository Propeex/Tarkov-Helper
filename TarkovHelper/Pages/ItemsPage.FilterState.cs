using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TarkovHelper.Pages;

public partial class ItemsPage
{
    private static readonly ItemsFilterState PersistedItemsFilters = new();
    private bool _persistentItemsFilterHooksAttached;

    static ItemsPage()
    {
        EventManager.RegisterClassHandler(
            typeof(ItemsPage),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnPersistentItemsFiltersLoaded));
    }

    private static void OnPersistentItemsFiltersLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ItemsPage page)
            page.Dispatcher.BeginInvoke(
                page.RestorePersistentItemsFiltersWhenReady,
                DispatcherPriority.ContextIdle);
    }

    private async void RestorePersistentItemsFiltersWhenReady()
    {
        for (var attempt = 0; attempt < 120 && !_isDataLoaded; attempt++)
            await Task.Delay(25);

        if (!_isDataLoaded)
            return;

        _isInitializing = true;
        try
        {
            SelectComboItemByTag(CmbSource, PersistedItemsFilters.Source);
            SelectComboItemByTag(CmbCategory, PersistedItemsFilters.Category);
            SelectComboItemByTag(CmbFulfillment, PersistedItemsFilters.Fulfillment);
            ChkFirOnly.IsChecked = PersistedItemsFilters.FirOnly;
            ChkHideFulfilled.IsChecked = PersistedItemsFilters.HideFulfilled;
            SelectComboItemByTag(CmbSort, PersistedItemsFilters.Sort);
        }
        finally
        {
            _isInitializing = false;
        }

        AttachPersistentItemsFilterHooks();
        ApplyFilters();
    }

    private void AttachPersistentItemsFilterHooks()
    {
        if (_persistentItemsFilterHooksAttached)
            return;

        _persistentItemsFilterHooksAttached = true;
        CmbSource.SelectionChanged += SavePersistentItemsFilters;
        CmbCategory.SelectionChanged += SavePersistentItemsFilters;
        CmbFulfillment.SelectionChanged += SavePersistentItemsFilters;
        ChkFirOnly.Checked += SavePersistentItemsFilters;
        ChkFirOnly.Unchecked += SavePersistentItemsFilters;
        ChkHideFulfilled.Checked += SavePersistentItemsFilters;
        ChkHideFulfilled.Unchecked += SavePersistentItemsFilters;
        CmbSort.SelectionChanged += SavePersistentItemsFilters;
    }

    private void SavePersistentItemsFilters(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        PersistedItemsFilters.Source = GetSelectedTag(CmbSource, "All");
        PersistedItemsFilters.Category = GetSelectedTag(CmbCategory, "All");
        PersistedItemsFilters.Fulfillment = GetSelectedTag(CmbFulfillment, "All");
        PersistedItemsFilters.FirOnly = ChkFirOnly.IsChecked == true;
        PersistedItemsFilters.HideFulfilled = ChkHideFulfilled.IsChecked == true;
        PersistedItemsFilters.Sort = GetSelectedTag(CmbSort, "Name");
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

    private sealed class ItemsFilterState
    {
        public string Source { get; set; } = "All";
        public string Category { get; set; } = "All";
        public string Fulfillment { get; set; } = "All";
        public bool FirOnly { get; set; }
        public bool HideFulfilled { get; set; }
        public string Sort { get; set; } = "Name";
    }
}
