using System.Windows;
using System.Windows.Threading;
using TarkovHelper.Services;

namespace TarkovHelper.Pages.Map;

/// <summary>
/// Keeps the map drawer synchronized with the exact active-objective set produced by
/// MapQuestMarkerManager. The original page refreshed marker data without rebinding
/// the drawer, which allowed stale non-active quest names to remain visible.
/// </summary>
public partial class MapPage
{
    private bool _strictQuestRefreshHookAttached;

    static MapPage()
    {
        EventManager.RegisterClassHandler(
            typeof(MapPage),
            LoadedEvent,
            new RoutedEventHandler(OnStrictQuestRefreshLoaded));
        EventManager.RegisterClassHandler(
            typeof(MapPage),
            UnloadedEvent,
            new RoutedEventHandler(OnStrictQuestRefreshUnloaded));
    }

    private static void OnStrictQuestRefreshLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MapPage page)
            return;

        page.Dispatcher.BeginInvoke(
            new Action(page.AttachStrictQuestRefreshHook),
            DispatcherPriority.Loaded);
    }

    private static void OnStrictQuestRefreshUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MapPage page)
            page.DetachStrictQuestRefreshHook();
    }

    private async void AttachStrictQuestRefreshHook()
    {
        if (_strictQuestRefreshHookAttached)
            return;

        // InitializeComponents runs from the page's normal Loaded handler. Wait until
        // the manager exists instead of competing with that initialization sequence.
        for (var attempt = 0; attempt < 100 && IsLoaded && _questMarkerManager == null; attempt++)
            await Task.Delay(50);

        if (!IsLoaded || _questMarkerManager == null || _strictQuestRefreshHookAttached)
            return;

        _strictQuestRefreshHookAttached = true;
        _questMarkerManager.StatusUpdated += OnStrictQuestMarkerStatusUpdated;

        // Re-scan the read-only game logs whenever the map tab is entered. This makes
        // the map use the same latest started/completed/failed set as the quest tab,
        // even when the map tab is opened first.
        await ActualQuestStatusService.Instance.RefreshFromLogsAsync();
    }

    private void DetachStrictQuestRefreshHook()
    {
        if (!_strictQuestRefreshHookAttached)
            return;

        if (_questMarkerManager != null)
            _questMarkerManager.StatusUpdated -= OnStrictQuestMarkerStatusUpdated;

        _strictQuestRefreshHookAttached = false;
    }

    private void OnStrictQuestMarkerStatusUpdated(string _)
    {
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_questMarkerManager == null)
                    return;

                _currentMapObjectives = _questMarkerManager.GetCurrentMapObjectives();
                if (QuestDrawerPanel?.Visibility == Visibility.Visible)
                    RefreshQuestDrawer();
            }),
            DispatcherPriority.ContextIdle);
    }
}