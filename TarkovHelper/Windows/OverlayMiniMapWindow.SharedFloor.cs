using System.Windows;
using System.Windows.Threading;
using TarkovHelper.Services.Map;

namespace TarkovHelper.Windows;

public partial class OverlayMiniMapWindow
{
    private static readonly bool SharedFloorOverlayHandlersRegistered = RegisterSharedFloorOverlayHandlers();
    private readonly SharedMapFloorStateService _sharedFloorState = SharedMapFloorStateService.Instance;
    private bool _sharedFloorAttached;

    private static bool RegisterSharedFloorOverlayHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(OverlayMiniMapWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnSharedFloorOverlayLoaded));
        EventManager.RegisterClassHandler(
            typeof(OverlayMiniMapWindow),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnSharedFloorOverlayUnloaded));
        return true;
    }

    private static void OnSharedFloorOverlayLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is OverlayMiniMapWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.AttachSharedFloorState,
                DispatcherPriority.ContextIdle);
        }
    }

    private static void OnSharedFloorOverlayUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is OverlayMiniMapWindow window)
            window.DetachSharedFloorState();
    }

    private void AttachSharedFloorState()
    {
        if (_sharedFloorAttached)
            return;

        _sharedFloorAttached = true;
        _sharedFloorState.FloorChanged += OnSharedFloorChanged;
        var snapshot = _sharedFloorState.Capture();
        if (!string.IsNullOrWhiteSpace(snapshot.MapKey))
            ApplySharedFloor(snapshot.MapKey!, snapshot.FloorId, snapshot.IsAutomatic);
    }

    private void DetachSharedFloorState()
    {
        if (!_sharedFloorAttached)
            return;

        _sharedFloorAttached = false;
        _sharedFloorState.FloorChanged -= OnSharedFloorChanged;
    }

    private void OnSharedFloorChanged(object? sender, SharedMapFloorChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, this))
            return;

        Dispatcher.BeginInvoke(() => ApplySharedFloor(
            e.MapKey,
            e.FloorId,
            e.IsAutomatic));
    }

    private void ApplySharedFloor(string mapKey, string? floorId, bool isAutomatic)
    {
        if (!string.Equals(_currentMapKey, mapKey, StringComparison.OrdinalIgnoreCase))
            LoadMap(mapKey);

        if (!string.Equals(_currentMapKey, mapKey, StringComparison.OrdinalIgnoreCase))
            return;

        var floorExists = string.IsNullOrWhiteSpace(floorId) ||
                          GetOrderedFloors().Any(floor => string.Equals(
                              floor.LayerId,
                              floorId,
                              StringComparison.OrdinalIgnoreCase));
        if (!floorExists)
            return;

        var floorChanged = !string.Equals(
            _selectedFloorId,
            floorId,
            StringComparison.OrdinalIgnoreCase);
        var modeChanged = _settings.AutoFloorSelection != isAutomatic ||
                          _manualFloorSelection == isAutomatic;

        _selectedFloorId = floorId;
        _settings.AutoFloorSelection = isAutomatic;
        _appliedAutoFloorSelection = isAutomatic;
        _manualFloorSelection = !isAutomatic;
        _settings.OtherFloorOpacity = 0.0;
        UpdateFloorIndicator();

        if (floorChanged && _currentMapConfig != null)
            QueueFloorRender(fitMap: false);
        else
            QueueMarkerRefresh();

        if (floorChanged || modeChanged)
            SettingsChanged?.Invoke(_settings);
    }
}
