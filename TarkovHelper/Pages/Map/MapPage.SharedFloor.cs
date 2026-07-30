using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SharpVectors.Converters;
using TarkovHelper.Services.Map;

namespace TarkovHelper.Pages.Map;

public partial class MapPage
{
    private static readonly bool SharedFloorHandlersRegistered = RegisterSharedFloorHandlers();

    private readonly SharedMapFloorStateService _sharedFloorState = SharedMapFloorStateService.Instance;
    private readonly SharedFloorHotkeyService _sharedFloorHotkeys = SharedFloorHotkeyService.Instance;
    private readonly HashSet<FrameworkElement> _sharedFloorHiddenMarkers = new();

    private bool _sharedFloorIntegrationAttached;
    private bool _sharedFloorApplying;
    private bool _sharedFloorHotkeyChange;
    private bool _sharedFloorSourceGuard;
    private DependencyPropertyDescriptor? _sharedMapSourceDescriptor;
    private CancellationTokenSource? _sharedFloorRenderCts;
    private string? _sharedProcessedMapPath;
    private DispatcherTimer? _sharedMarkerFilterTimer;
    private int _sharedMarkerFilterTicks;

    private static bool RegisterSharedFloorHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(MapPage),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnSharedFloorPageLoaded));
        EventManager.RegisterClassHandler(
            typeof(MapPage),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnSharedFloorPageUnloaded));
        return true;
    }

    private static void OnSharedFloorPageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MapPage page)
            page.Dispatcher.BeginInvoke(page.AttachSharedFloorIntegration, DispatcherPriority.Loaded);
    }

    private static void OnSharedFloorPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MapPage page)
            page.DetachSharedFloorIntegration();
    }

    private void AttachSharedFloorIntegration()
    {
        if (_sharedFloorIntegrationAttached)
            return;

        _sharedFloorIntegrationAttached = true;
        _sharedFloorState.FloorChanged += OnSharedFloorStateChanged;
        CmbMapSelect.SelectionChanged += OnSharedMapSelectionChanged;
        CmbFloorSelect.SelectionChanged += OnSharedNormalFloorSelectionChanged;
        if (_trackerService != null)
            _trackerService.PositionUpdated += OnSharedFloorPositionUpdated;

        _sharedFloorHotkeys.FloorUpPressed += OnSharedFloorUpPressed;
        _sharedFloorHotkeys.FloorDownPressed += OnSharedFloorDownPressed;
        _sharedFloorHotkeys.ResumeAutomaticPressed += OnSharedResumeAutomaticPressed;
        _sharedFloorHotkeys.Acquire();

        _sharedMapSourceDescriptor = DependencyPropertyDescriptor.FromProperty(
            SvgViewbox.SourceProperty,
            typeof(SvgViewbox));
        _sharedMapSourceDescriptor?.AddValueChanged(MapSvg, OnSharedMapSourceChanged);

        // 기존 NumPad 0~5 직접 층 선택은 제거하고 위층·아래층 단축키로 통일합니다.
        GlobalKeyboardHookService.Instance.FloorKeyPressed -= OnFloorKeyPressed;

        SynchronizeInitialSharedFloor();
        QueueCurrentFloorOnlyRender();
        ScheduleSharedMarkerFilter();
    }

    private void DetachSharedFloorIntegration()
    {
        if (!_sharedFloorIntegrationAttached)
            return;

        _sharedFloorIntegrationAttached = false;
        _sharedFloorState.FloorChanged -= OnSharedFloorStateChanged;
        CmbMapSelect.SelectionChanged -= OnSharedMapSelectionChanged;
        CmbFloorSelect.SelectionChanged -= OnSharedNormalFloorSelectionChanged;
        if (_trackerService != null)
            _trackerService.PositionUpdated -= OnSharedFloorPositionUpdated;

        _sharedFloorHotkeys.FloorUpPressed -= OnSharedFloorUpPressed;
        _sharedFloorHotkeys.FloorDownPressed -= OnSharedFloorDownPressed;
        _sharedFloorHotkeys.ResumeAutomaticPressed -= OnSharedResumeAutomaticPressed;
        _sharedFloorHotkeys.Release();

        _sharedMapSourceDescriptor?.RemoveValueChanged(MapSvg, OnSharedMapSourceChanged);
        _sharedMapSourceDescriptor = null;
        _sharedFloorRenderCts?.Cancel();
        _sharedFloorRenderCts?.Dispose();
        _sharedFloorRenderCts = null;
        _sharedMarkerFilterTimer?.Stop();
        _sharedMarkerFilterTimer = null;
        DeleteSharedTempMap(_sharedProcessedMapPath);
        _sharedProcessedMapPath = null;
        _sharedFloorHiddenMarkers.Clear();
    }

    private void SynchronizeInitialSharedFloor()
    {
        if (string.IsNullOrWhiteSpace(_currentMapKey))
            return;

        var snapshot = _sharedFloorState.Capture();
        if (string.Equals(snapshot.MapKey, _currentMapKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(snapshot.FloorId))
        {
            ApplySharedFloor(snapshot.MapKey!, snapshot.FloorId, snapshot.IsAutomatic);
        }
        else
        {
            _sharedFloorState.Publish(_currentMapKey, _currentFloorId, true, this);
        }
    }

    private void OnSharedMapSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (string.IsNullOrWhiteSpace(_currentMapKey))
                return;

            var snapshot = _sharedFloorState.Capture();
            if (string.Equals(snapshot.MapKey, _currentMapKey, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(snapshot.FloorId))
            {
                ApplySharedFloor(snapshot.MapKey!, snapshot.FloorId, snapshot.IsAutomatic);
            }
            else
            {
                _sharedFloorState.Publish(_currentMapKey, _currentFloorId, true, this);
            }

            QueueCurrentFloorOnlyRender();
            ScheduleSharedMarkerFilter();
        }, DispatcherPriority.ContextIdle);
    }

    private void OnSharedNormalFloorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sharedFloorApplying ||
            string.IsNullOrWhiteSpace(_currentMapKey) ||
            CmbFloorSelect.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string floorId)
        {
            return;
        }

        var userInitiated = _sharedFloorHotkeyChange ||
                            CmbFloorSelect.IsDropDownOpen ||
                            CmbFloorSelect.IsKeyboardFocusWithin;
        var snapshot = _sharedFloorState.Capture();

        if (!userInitiated &&
            string.Equals(snapshot.MapKey, _currentMapKey, StringComparison.OrdinalIgnoreCase) &&
            !snapshot.IsAutomatic &&
            !string.IsNullOrWhiteSpace(snapshot.FloorId) &&
            !string.Equals(snapshot.FloorId, floorId, StringComparison.OrdinalIgnoreCase))
        {
            ApplySharedFloor(snapshot.MapKey!, snapshot.FloorId, false);
            return;
        }

        _sharedFloorState.Publish(_currentMapKey, floorId, !userInitiated, this);
        QueueCurrentFloorOnlyRender();
        ScheduleSharedMarkerFilter();
    }

    private void OnSharedFloorPositionUpdated(object? sender, TarkovHelper.Models.Map.ScreenPosition position)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (string.IsNullOrWhiteSpace(_currentMapKey) ||
                CmbFloorSelect.Visibility != Visibility.Visible)
            {
                return;
            }

            var snapshot = _sharedFloorState.Capture();
            if (string.Equals(snapshot.MapKey, _currentMapKey, StringComparison.OrdinalIgnoreCase) &&
                !snapshot.IsAutomatic &&
                !string.IsNullOrWhiteSpace(snapshot.FloorId))
            {
                ApplySharedFloor(snapshot.MapKey!, snapshot.FloorId, false);
                return;
            }

            var original = position.OriginalPosition;
            if (original == null)
                return;

            var detected = FloorDetectionService.Instance.DetectFloor(
                _currentMapKey,
                original.X,
                original.Y,
                original.Z ?? 0);
            if (string.IsNullOrWhiteSpace(detected))
                detected = "main";

            if (HasFloor(detected))
                _sharedFloorState.Publish(_currentMapKey, detected, true, this);
        });
    }

    private void OnSharedFloorStateChanged(object? sender, SharedMapFloorChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, this))
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!string.Equals(_currentMapKey, e.MapKey, StringComparison.OrdinalIgnoreCase))
            {
                SelectSharedMap(e.MapKey);
                Dispatcher.BeginInvoke(
                    () => ApplySharedFloor(e.MapKey, e.FloorId, e.IsAutomatic),
                    DispatcherPriority.ContextIdle);
                return;
            }

            ApplySharedFloor(e.MapKey, e.FloorId, e.IsAutomatic);
        });
    }

    private void SelectSharedMap(string mapKey)
    {
        for (var index = 0; index < CmbMapSelect.Items.Count; index++)
        {
            if (CmbMapSelect.Items[index] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), mapKey, StringComparison.OrdinalIgnoreCase))
            {
                CmbMapSelect.SelectedIndex = index;
                return;
            }
        }
    }

    private void ApplySharedFloor(string mapKey, string? floorId, bool isAutomatic)
    {
        if (!string.Equals(_currentMapKey, mapKey, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(floorId))
        {
            return;
        }

        _sharedFloorApplying = true;
        try
        {
            for (var index = 0; index < CmbFloorSelect.Items.Count; index++)
            {
                if (CmbFloorSelect.Items[index] is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), floorId, StringComparison.OrdinalIgnoreCase))
                {
                    CmbFloorSelect.SelectedIndex = index;
                    break;
                }
            }
        }
        finally
        {
            _sharedFloorApplying = false;
        }

        QueueCurrentFloorOnlyRender();
        ScheduleSharedMarkerFilter();
    }

    private void OnSharedFloorUpPressed() => MoveSharedFloor(1);

    private void OnSharedFloorDownPressed() => MoveSharedFloor(-1);

    private void MoveSharedFloor(int direction)
    {
        if (!_sharedFloorIntegrationAttached ||
            CmbFloorSelect.Visibility != Visibility.Visible ||
            CmbFloorSelect.Items.Count < 2)
        {
            return;
        }

        var currentIndex = Math.Max(0, CmbFloorSelect.SelectedIndex);
        var targetIndex = Math.Clamp(currentIndex + direction, 0, CmbFloorSelect.Items.Count - 1);
        if (targetIndex == currentIndex)
            return;

        _sharedFloorHotkeyChange = true;
        try
        {
            CmbFloorSelect.SelectedIndex = targetIndex;
        }
        finally
        {
            _sharedFloorHotkeyChange = false;
        }
    }

    private void OnSharedResumeAutomaticPressed()
    {
        if (!_sharedFloorIntegrationAttached || string.IsNullOrWhiteSpace(_currentMapKey))
            return;

        var original = _trackerService?.LastPosition?.OriginalPosition;
        var detected = original == null
            ? null
            : FloorDetectionService.Instance.DetectFloor(
                _currentMapKey,
                original.X,
                original.Y,
                original.Z ?? 0);

        if (string.IsNullOrWhiteSpace(detected) || !HasFloor(detected))
            detected = _currentFloorId ?? GetFirstFloorId();

        _sharedFloorState.Publish(_currentMapKey, detected, true, this);
        ApplySharedFloor(_currentMapKey, detected, true);
    }

    private bool HasFloor(string? floorId) =>
        !string.IsNullOrWhiteSpace(floorId) &&
        CmbFloorSelect.Items.OfType<ComboBoxItem>().Any(item => string.Equals(
            item.Tag?.ToString(),
            floorId,
            StringComparison.OrdinalIgnoreCase));

    private string? GetFirstFloorId() =>
        CmbFloorSelect.Items.OfType<ComboBoxItem>().FirstOrDefault()?.Tag?.ToString();

    private void OnSharedMapSourceChanged(object? sender, EventArgs e)
    {
        if (_sharedFloorSourceGuard)
            return;

        var currentSource = MapSvg.Source?.LocalPath;
        if (!string.IsNullOrWhiteSpace(currentSource) &&
            string.Equals(currentSource, _sharedProcessedMapPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        QueueCurrentFloorOnlyRender();
    }

    private void QueueCurrentFloorOnlyRender()
    {
        if (!_sharedFloorIntegrationAttached)
            return;

        _sharedFloorRenderCts?.Cancel();
        _sharedFloorRenderCts?.Dispose();
        _sharedFloorRenderCts = new CancellationTokenSource();
        _ = RenderCurrentFloorOnlyAsync(_sharedFloorRenderCts.Token);
    }

    private async Task RenderCurrentFloorOnlyAsync(CancellationToken cancellationToken)
    {
        var mapKey = _currentMapKey;
        var floorId = _currentFloorId;
        if (string.IsNullOrWhiteSpace(mapKey) || string.IsNullOrWhiteSpace(floorId))
            return;

        var config = _trackerService?.GetMapConfig(mapKey);
        if (config?.Floors == null || config.Floors.Count == 0)
            return;

        var sourcePath = config.ImagePath;
        if (string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(config.SvgFileName))
            sourcePath = Path.Combine("Assets", "DB", "Maps", config.SvgFileName);
        if (!string.IsNullOrWhiteSpace(sourcePath) && !Path.IsPathRooted(sourcePath))
            sourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, sourcePath);
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            !string.Equals(Path.GetExtension(sourcePath), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? generatedPath = null;
        try
        {
            var allFloors = config.Floors.Select(floor => floor.LayerId).ToArray();
            generatedPath = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processed = new SvgStylePreprocessor().ProcessSvgFile(
                    sourcePath,
                    new[] { floorId },
                    allFloors,
                    backgroundFloorId: null,
                    backgroundOpacity: 0.0,
                    dimAllOtherFloors: false);
                var path = Path.Combine(
                    Path.GetTempPath(),
                    $"tarkov_map_current_floor_{Guid.NewGuid():N}.svg");
                File.WriteAllText(path, processed);
                return path;
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(_currentMapKey, mapKey, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_currentFloorId, floorId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previous = _sharedProcessedMapPath;
            _sharedFloorSourceGuard = true;
            try
            {
                MapSvg.Source = new Uri(generatedPath, UriKind.Absolute);
            }
            finally
            {
                _sharedFloorSourceGuard = false;
            }

            _sharedProcessedMapPath = generatedPath;
            generatedPath = null;
            DeleteSharedTempMap(previous);
            ScheduleSharedMarkerFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warning($"Current-floor-only map rendering failed: {ex.Message}");
        }
        finally
        {
            DeleteSharedTempMap(generatedPath);
        }
    }

    private void ScheduleSharedMarkerFilter()
    {
        _sharedMarkerFilterTicks = 12;
        _sharedMarkerFilterTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _sharedMarkerFilterTimer.Tick -= OnSharedMarkerFilterTick;
        _sharedMarkerFilterTimer.Tick += OnSharedMarkerFilterTick;
        _sharedMarkerFilterTimer.Stop();
        _sharedMarkerFilterTimer.Start();
    }

    private void OnSharedMarkerFilterTick(object? sender, EventArgs e)
    {
        ApplySharedMarkerFloorFilter();
        if (--_sharedMarkerFilterTicks <= 0)
            _sharedMarkerFilterTimer?.Stop();
    }

    private void ApplySharedMarkerFloorFilter()
    {
        if (string.IsNullOrWhiteSpace(_currentFloorId))
            return;

        var currentElements = new HashSet<FrameworkElement>();
        foreach (var container in new[]
                 {
                     QuestMarkersContainer,
                     ExtractMarkersContainer,
                     MapMarkersContainer,
                     CustomMarkersContainer
                 })
        {
            foreach (var child in container.Children.OfType<FrameworkElement>())
            {
                currentElements.Add(child);
                var markerFloor = FindMarkerFloorId(child);
                var isCurrent = string.IsNullOrWhiteSpace(markerFloor) ||
                                MiniMapMarkerVisibilityState.IsCurrentFloor(markerFloor, _currentFloorId);
                if (isCurrent)
                {
                    if (_sharedFloorHiddenMarkers.Remove(child))
                        child.Visibility = Visibility.Visible;
                }
                else
                {
                    if (child.Visibility == Visibility.Visible)
                        _sharedFloorHiddenMarkers.Add(child);
                    child.Visibility = Visibility.Collapsed;
                }
            }
        }

        _sharedFloorHiddenMarkers.RemoveWhere(element => !currentElements.Contains(element));
    }

    private static string? FindMarkerFloorId(DependencyObject element)
    {
        if (element is FrameworkElement frameworkElement && frameworkElement.Tag != null)
        {
            foreach (var propertyName in new[] { "FloorId", "Floor", "LayerId" })
            {
                var property = frameworkElement.Tag.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property?.GetValue(frameworkElement.Tag) is string value &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var found = FindMarkerFloorId(System.Windows.Media.VisualTreeHelper.GetChild(element, index));
            if (!string.IsNullOrWhiteSpace(found))
                return found;
        }

        return null;
    }

    private static void DeleteSharedTempMap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
