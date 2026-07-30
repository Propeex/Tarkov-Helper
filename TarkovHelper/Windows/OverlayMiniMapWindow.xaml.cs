using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using TarkovHelper.Models;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;
using TarkovHelper.Services.Logging;
using TarkovHelper.Services.Map;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Windows;

/// <summary>
/// 오버레이 미니맵 윈도우 - 심플 버전 (컨트롤 없음)
/// </summary>
public partial class OverlayMiniMapWindow : Window
{
    #region Win32 API for Click-Through

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    #endregion

    private static readonly ILogger _log = Log.For<OverlayMiniMapWindow>();

    private readonly OverlayMiniMapSettings _settings;
    private MapTrackerService? _trackerService;
    private string? _currentMapKey;
    private MapConfig? _currentMapConfig;
    private readonly Dictionary<MarkerType, DrawingGroup?> _mapMarkerIconCache = new();
    private readonly string _mapMarkerIconBasePath = System.IO.Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Assets", "DB", "Icons", "Markers");
    private CancellationTokenSource? _markerLoadCts;
    private CancellationTokenSource? _floorRenderCts;
    private string? _sourceSvgPath;
    private string? _processedSvgPath;
    private string? _selectedFloorId;
    private bool _manualFloorSelection;
    private double _appliedOtherFloorOpacity;
    private bool _appliedAutoFloorSelection;
    private double _appliedZoomLevel;

    private IntPtr _hwnd;
    private bool _isClickThrough;

    // 휠 클릭 드래그용 필드
    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartOffsetX;
    private double _panStartOffsetY;

    /// <summary>
    /// 설정 변경 이벤트
    /// </summary>
    public event Action<OverlayMiniMapSettings>? SettingsChanged;

    /// <summary>
    /// 윈도우 닫힘 이벤트
    /// </summary>
    public event Action? OverlayClosed;

    public OverlayMiniMapWindow(OverlayMiniMapSettings settings)
    {
        InitializeComponent();

        _settings = settings;
        ApplySettings();

        Loaded += OnLoaded;
        Closing += OnClosing;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnLocationChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // 초기 위치 설정
        if (_settings.PositionX < 0 || _settings.PositionY < 0)
        {
            PositionToTopRight();
        }

        // Click-through 상태 적용
        if (_settings.ClickThrough)
        {
            EnableClickThrough();
        }

        // MapTrackerService 연결
        ConnectToMapTracker();

        _log.Info("OverlayMiniMapWindow loaded");
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        OverlayClosed?.Invoke();
        _log.Info("OverlayMiniMapWindow closing");
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _settings.Width = ActualWidth;
        _settings.Height = ActualHeight;
        UpdateMapView();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        _settings.PositionX = Left;
        _settings.PositionY = Top;
    }

    #region Settings

    private void ApplySettings()
    {
        Width = _settings.Width;
        Height = _settings.Height;

        if (_settings.PositionX >= 0 && _settings.PositionY >= 0)
        {
            Left = _settings.PositionX;
            Top = _settings.PositionY;
        }

        MainBorder.Opacity = Math.Clamp(
            _settings.Opacity,
            OverlayMiniMapSettings.MinOpacity,
            OverlayMiniMapSettings.MaxOpacity);
        _appliedOtherFloorOpacity = _settings.OtherFloorOpacity;
        _appliedAutoFloorSelection = _settings.AutoFloorSelection;
        _appliedZoomLevel = _settings.ZoomLevel;
    }

    private void SaveSettings()
    {
        _settings.PositionX = Left;
        _settings.PositionY = Top;
        _settings.Width = ActualWidth;
        _settings.Height = ActualHeight;
        SettingsChanged?.Invoke(_settings);
    }

    private void PositionToTopRight()
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top = screen.Top + 20;
        _settings.PositionX = Left;
        _settings.PositionY = Top;
    }

    #endregion

    #region MapTracker Integration

    private void ConnectToMapTracker()
    {
        try
        {
            _trackerService = MapTrackerService.Instance;
            _trackerService.PositionUpdated += OnPositionUpdated;
            _trackerService.MapChanged += OnMapChanged;
            MapMarkerDbService.Instance.DataRefreshed += OnMapMarkerDataRefreshed;

            // 현재 맵 로드
            var currentMap = _trackerService.CurrentMapKey;
            _log.Info($"ConnectToMapTracker: CurrentMapKey = '{currentMap}'");

            if (!string.IsNullOrEmpty(currentMap))
            {
                LoadMap(currentMap);
            }
            else
            {
                _log.Warning("No current map set in MapTrackerService");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to connect to MapTrackerService", ex);
        }
    }

    private void OnPositionUpdated(object? sender, ScreenPosition position)
{
    Dispatcher.BeginInvoke(() =>
    {
        UpdatePlayerMarker(position);

        if (_settings.ViewMode == MiniMapViewMode.PlayerTracking)
            CenterOnPlayer(position);

        if (!_settings.AutoFloorSelection || _manualFloorSelection)
            return;

        var automaticFloorId = MiniMapFloorSelection.SelectAutomatic(
            _currentMapConfig?.Floors,
            DetectCurrentFloor(position));
        if (string.Equals(
                _selectedFloorId,
                automaticFloorId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedFloorId = automaticFloorId;
        UpdateFloorIndicator();
        QueueFloorRender(fitMap: false);
    });
}

    private void OnMapChanged(string mapKey)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LoadMap(mapKey);
        });
    }

    private void OnMapMarkerDataRefreshed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(QueueMarkerRefresh));
    }

    private void LoadMap(string mapKey)
    {
        try
        {
            _log.Info($"LoadMap called with mapKey: '{mapKey}'");

            _currentMapKey = mapKey;
            _currentMapConfig = _trackerService?.GetMapConfig(mapKey);
            _sourceSvgPath = null;
            _selectedFloorId = null;
            _manualFloorSelection = false;

            if (_currentMapConfig == null)
            {
                ShowMapError("지도가 선택되지 않았습니다");
                return;
            }

            if (string.IsNullOrEmpty(_currentMapConfig.SvgFileName))
            {
                ShowMapError($"SVG 파일 없음: {mapKey}");
                return;
            }

            var svgPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Assets", "DB", "Maps", _currentMapConfig.SvgFileName);
            if (!System.IO.File.Exists(svgPath))
            {
                _log.Warning($"Map SVG not found: {svgPath}");
                ShowMapError($"지도를 찾을 수 없음: {mapKey}");
                return;
            }

            _sourceSvgPath = svgPath;
            SelectInitialFloor();
            TxtNoMap.Visibility = Visibility.Collapsed;
            QueueFloorRender(fitMap: true);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load map: {mapKey}", ex);
            ShowMapError($"지도 로드 실패: {mapKey}");
        }
    }

    private void ShowMapError(string message)
    {
        TxtNoMap.Text = message;
        TxtNoMap.Visibility = Visibility.Visible;
        FloorIndicator.Visibility = Visibility.Collapsed;
        MapSvg.Source = null;
        MapMarkersContainer.Children.Clear();
        ExtractMarkersContainer.Children.Clear();
    }

    private void SelectInitialFloor()
{
    _selectedFloorId = _settings.AutoFloorSelection
        ? MiniMapFloorSelection.SelectAutomatic(
            _currentMapConfig?.Floors,
            DetectCurrentFloor())
        : MiniMapFloorSelection.SelectInitial(
            _currentMapConfig?.Floors,
            preferredFloorId: null);
    UpdateFloorIndicator();
}

    private IReadOnlyList<MapFloorConfig> GetOrderedFloors() =>
        MiniMapFloorSelection.Order(_currentMapConfig?.Floors);

    private void QueueFloorRender(bool fitMap)
    {
        _floorRenderCts?.Cancel();
        _floorRenderCts?.Dispose();
        _floorRenderCts = new CancellationTokenSource();
        _ = RenderCurrentFloorAsync(fitMap, _floorRenderCts.Token);
    }

    private async Task RenderCurrentFloorAsync(bool fitMap, CancellationToken ct)
    {
        var mapKey = _currentMapKey;
        var config = _currentMapConfig;
        var svgPath = _sourceSvgPath;
        var selectedFloorId = _selectedFloorId;
        if (string.IsNullOrWhiteSpace(mapKey) || config == null || string.IsNullOrWhiteSpace(svgPath))
            return;

        string? generatedPath = null;
        try
        {
            var floors = GetOrderedFloors();
            var sourceToDisplay = svgPath;
            if (floors.Count > 0 && !string.IsNullOrWhiteSpace(selectedFloorId))
            {
                generatedPath = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var allFloorIds = floors.Select(floor => floor.LayerId).ToArray();
                    var defaultFloor = floors.FirstOrDefault(floor => floor.IsDefault);
                    var backgroundFloorId = defaultFloor != null &&
                                            !string.Equals(
                                                defaultFloor.LayerId,
                                                selectedFloorId,
                                                StringComparison.OrdinalIgnoreCase)
                        ? defaultFloor.LayerId
                        : null;
                    var opacity = Math.Clamp(
                        _settings.OtherFloorOpacity,
                        OverlayMiniMapSettings.MinOtherFloorOpacity,
                        OverlayMiniMapSettings.MaxOtherFloorOpacity);
                    var preprocessor = new TarkovHelper.Services.Map.SvgStylePreprocessor();
                    var processed = preprocessor.ProcessSvgFile(
                        svgPath,
                        new[] { selectedFloorId },
                        allFloorIds,
                        backgroundFloorId,
                        opacity,
                        dimAllOtherFloors: true);
                    var tempPath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        $"tarkov_minimap_{Guid.NewGuid():N}.svg");
                    System.IO.File.WriteAllText(tempPath, processed);
                    return tempPath;
                }, ct);
                sourceToDisplay = generatedPath;
            }

            ct.ThrowIfCancellationRequested();
            if (!string.Equals(_currentMapKey, mapKey, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_selectedFloorId, selectedFloorId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previousProcessedPath = _processedSvgPath;
            MapSvg.Source = new Uri(sourceToDisplay, UriKind.Absolute);
            MapSvg.Visibility = Visibility.Visible;
            MapSvg.Width = config.ImageWidth;
            MapSvg.Height = config.ImageHeight;
            MapCanvas.Width = config.ImageWidth;
            MapCanvas.Height = config.ImageHeight;
            Canvas.SetLeft(MapSvg, 0);
            Canvas.SetTop(MapSvg, 0);
            _processedSvgPath = generatedPath;
            generatedPath = null;

            if (fitMap)
                FitMapToWindow();
            UpdateMapView();
            UpdateFloorIndicator();
            QueueMarkerRefresh();
            DeleteTempSvg(previousProcessedPath);
            _log.Info($"Minimap floor rendered: map={mapKey}, floor={selectedFloorId ?? "all"}");
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Minimap floor rendering cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to render minimap floor: {mapKey}", ex);
        }
        finally
        {
            DeleteTempSvg(generatedPath);
        }
    }

    private static void DeleteTempSvg(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch
        {
            // SvgViewbox가 아직 파일을 읽는 중이면 다음 교체 또는 종료 시 다시 정리합니다.
        }
    }

    private void UpdateFloorIndicator()
    {
        var floors = GetOrderedFloors();
        if (floors.Count == 0)
        {
            FloorIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var floor = floors.FirstOrDefault(item => string.Equals(
            item.LayerId,
            _selectedFloorId,
            StringComparison.OrdinalIgnoreCase));
        TxtFloorName.Text = floor?.DisplayName ?? "층 미확인";
        TxtFloorMode.Text = _settings.AutoFloorSelection && !_manualFloorSelection
            ? "자동"
            : "수동";
        FloorIndicator.Visibility = Visibility.Visible;
    }

    private void FitMapToWindow()
    {
        if (_currentMapConfig == null) return;

        var mapWidth = _currentMapConfig.ImageWidth;
        var mapHeight = _currentMapConfig.ImageHeight;
        var viewWidth = ActualWidth > 0 ? ActualWidth : 300;
        var viewHeight = ActualHeight > 0 ? ActualHeight : 300;

        // 맵이 창에 맞도록 줌 레벨 계산
        var scaleX = viewWidth / mapWidth;
        var scaleY = viewHeight / mapHeight;
        var fitZoom = Math.Min(scaleX, scaleY) * 0.95; // 5% 여백

        _settings.ZoomLevel = Math.Max(OverlayMiniMapSettings.MinZoom, Math.Min(fitZoom, OverlayMiniMapSettings.MaxZoom));
        _appliedZoomLevel = _settings.ZoomLevel;

        // 맵을 중앙에 배치
        var scaledWidth = mapWidth * _settings.ZoomLevel;
        var scaledHeight = mapHeight * _settings.ZoomLevel;
        _settings.MapOffsetX = (viewWidth - scaledWidth) / 2;
        _settings.MapOffsetY = (viewHeight - scaledHeight) / 2;

        _log.Debug($"Auto-fit zoom: {_settings.ZoomLevel:F3}, offset: ({_settings.MapOffsetX:F0}, {_settings.MapOffsetY:F0})");
    }

    private void QueueMarkerRefresh()
    {
        _markerLoadCts?.Cancel();
        _markerLoadCts?.Dispose();
        _markerLoadCts = new CancellationTokenSource();
        _ = LoadMarkersAsync(_markerLoadCts.Token);
    }

    private async Task LoadMarkersAsync(CancellationToken ct)
    {
        MapMarkersContainer.Children.Clear();
        ExtractMarkersContainer.Children.Clear();
        QuestMarkersContainer.Children.Clear();
        QuestMarkersContainer.Visibility = Visibility.Collapsed;

        if (_currentMapKey == null || _currentMapConfig == null)
        {
            _log.Debug("LoadMarkersAsync: mapKey or mapConfig is null, skipping");
            return;
        }

        try
        {
            var mapSettings = MapSettings.Instance;
            var visibility = MiniMapMarkerVisibilityState.Capture(mapSettings);
            var selectedFloorId = _selectedFloorId;

            await LoadMapMarkersAsync(visibility, selectedFloorId, ct);
            if (visibility.ShowExtracts)
                await LoadExtractMarkersAsync(visibility, selectedFloorId, ct);

            UpdateOverlayMarkerScales();
            _log.Info(
                $"Minimap markers refreshed: map={_currentMapKey}, " +
                $"mapMarkers={MapMarkersContainer.Children.Count}, " +
                $"extracts={ExtractMarkersContainer.Children.Count}");
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Minimap marker refresh cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load minimap markers", ex);
        }
    }

    private async Task LoadMapMarkersAsync(
        MiniMapMarkerVisibilityState visibility,
        string? currentFloorId,
        CancellationToken ct)
    {
        var markerService = MapMarkerDbService.Instance;
        if (!markerService.IsLoaded)
        {
            var loaded = await markerService.LoadMarkersAsync();
            if (!loaded)
            {
                _log.Warning("Minimap map-marker data could not be loaded.");
                return;
            }
        }

        ct.ThrowIfCancellationRequested();
        var addedCount = 0;

        foreach (var marker in markerService.GetMarkersForMap(_currentMapKey!))
        {
            ct.ThrowIfCancellationRequested();
            if (!visibility.IsMapMarkerVisible(marker.Type))
                continue;

            var (screenX, screenY) = _currentMapConfig!.GameToScreenForPlayer(marker.X, marker.Z);
            var isCurrentFloor = IsCurrentFloor(marker.FloorId, currentFloorId);
            var element = CreateMapMarkerElement(marker, screenX, screenY, isCurrentFloor);
            MapMarkersContainer.Children.Add(element);
            addedCount++;
        }

        _log.Debug($"Added {addedCount} standard map markers to minimap.");
    }

    private async Task LoadExtractMarkersAsync(
        MiniMapMarkerVisibilityState visibility,
        string? currentFloorId,
        CancellationToken ct)
    {
        var extractService = ExtractService.Instance;
        if (!extractService.IsLoaded)
        {
            var loaded = await extractService.LoadAsync();
            if (!loaded)
            {
                _log.Warning("Minimap extract data could not be loaded.");
                return;
            }
        }

        ct.ThrowIfCancellationRequested();
        var extracts = extractService.GetExtractsForMap(_currentMapKey!, _currentMapConfig!);
        if (extracts.Count == 0)
            return;

        var addedCount = 0;

        foreach (var extract in extracts)
        {
            ct.ThrowIfCancellationRequested();
            if (!visibility.IsExtractVisible(extract.Faction))
                continue;

            var (screenX, screenY) = _currentMapConfig!.GameToScreenForPlayer(extract.X, extract.Z);
            const double markerSize = 10.0;
            var marker = CreateMarkerEllipse(GetExtractColor(extract.Faction), markerSize);
            marker.ToolTip = extract.Name;
            marker.IsHitTestVisible = false;
            marker.Opacity = IsCurrentFloor(extract.FloorId, currentFloorId)
                ? 0.9
                : Math.Clamp(_settings.OtherFloorOpacity, 0.0, 1.0);
            Canvas.SetLeft(marker, screenX - markerSize / 2);
            Canvas.SetTop(marker, screenY - markerSize / 2);
            ExtractMarkersContainer.Children.Add(marker);
            addedCount++;
        }

        _log.Debug($"Added {addedCount} extract markers to minimap.");
    }

    private FrameworkElement CreateMapMarkerElement(
        MapMarker marker,
        double screenX,
        double screenY,
        bool isCurrentFloor)
    {
        var mapScale = _currentMapConfig?.MarkerScale ?? 1.0;
        var markerSize = 18.0 * mapScale;
        var canvas = new Canvas
        {
            Width = 0,
            Height = 0,
            IsHitTestVisible = false,
            Opacity = isCurrentFloor
                ? 0.95
                : Math.Clamp(_settings.OtherFloorOpacity, 0.0, 1.0),
            Tag = marker
        };

        var iconDrawing = GetOrLoadMapMarkerIcon(marker.Type);
        if (iconDrawing != null)
        {
            var image = new Image
            {
                Source = new DrawingImage(iconDrawing),
                Width = markerSize,
                Height = markerSize,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(image, -markerSize / 2);
            Canvas.SetTop(image, -markerSize / 2);
            canvas.Children.Add(image);
        }
        else
        {
            var (r, g, b) = MapMarker.GetMarkerColor(marker.Type);
            var color = Color.FromRgb(r, g, b);
            var fallback = CreateMarkerEllipse(color, markerSize);
            fallback.IsHitTestVisible = false;
            Canvas.SetLeft(fallback, -markerSize / 2);
            Canvas.SetTop(fallback, -markerSize / 2);
            canvas.Children.Add(fallback);
        }

        Canvas.SetLeft(canvas, screenX);
        Canvas.SetTop(canvas, screenY);
        ApplyInverseMapScale(canvas);
        return canvas;
    }

    private DrawingGroup? GetOrLoadMapMarkerIcon(MarkerType type)
    {
        if (_mapMarkerIconCache.TryGetValue(type, out var cached))
            return cached;

        var fileName = MapMarker.GetSvgIconFileName(type);
        if (string.IsNullOrEmpty(fileName))
        {
            _mapMarkerIconCache[type] = null;
            return null;
        }

        var path = System.IO.Path.Combine(_mapMarkerIconBasePath, fileName);
        if (!System.IO.File.Exists(path))
        {
            _log.Warning($"Minimap marker icon not found: {path}");
            _mapMarkerIconCache[type] = null;
            return null;
        }

        try
        {
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = true,
                TextAsGeometry = false
            };
            using var reader = new FileSvgReader(settings);
            var drawing = reader.Read(path);
            _mapMarkerIconCache[type] = drawing;
            return drawing;
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to load minimap marker icon '{path}': {ex.Message}");
            _mapMarkerIconCache[type] = null;
            return null;
        }
    }

    private string? DetectCurrentFloor(ScreenPosition? position = null)
    {
        if (string.IsNullOrEmpty(_currentMapKey))
            return null;

        var original = position?.OriginalPosition ??
                       _trackerService?.LastPosition?.OriginalPosition;
        if (original == null)
            return null;

        return FloorDetectionService.Instance.DetectFloor(
            _currentMapKey,
            original.X,
            original.Y,
            original.Z ?? 0);
    }

    private static bool IsCurrentFloor(string? markerFloorId, string? currentFloorId) =>
        MiniMapMarkerVisibilityState.IsCurrentFloor(markerFloorId, currentFloorId);

    private Ellipse CreateMarkerEllipse(Color color, double size)
    {
        return new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(color),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Opacity = 0.9
        };
    }

    private static Color GetExtractColor(ExtractFaction faction)
    {
        return faction switch
        {
            ExtractFaction.Pmc => Color.FromRgb(0x4C, 0xAF, 0x50),
            ExtractFaction.Scav => Color.FromRgb(0x8B, 0xC3, 0x4A),
            ExtractFaction.Shared => Color.FromRgb(0x00, 0xBC, 0xD4),
            ExtractFaction.Transit => Color.FromRgb(0x21, 0x96, 0xF3),
            _ => Color.FromRgb(0x4C, 0xAF, 0x50)
        };
    }

    private void ApplyInverseMapScale(FrameworkElement marker)
    {
        var inverseScale = 1.0 / Math.Max(_settings.ZoomLevel, OverlayMiniMapSettings.MinZoom);
        marker.RenderTransform = new ScaleTransform(inverseScale, inverseScale);
        marker.RenderTransformOrigin = marker is Canvas
            ? new Point(0, 0)
            : new Point(0.5, 0.5);
    }

    private void UpdateOverlayMarkerScales()
    {
        foreach (var container in new[]
                 {
                     MapMarkersContainer,
                     ExtractMarkersContainer,
                     QuestMarkersContainer
                 })
        {
            foreach (FrameworkElement marker in container.Children)
                ApplyInverseMapScale(marker);
        }
    }

    private void UpdatePlayerMarker(ScreenPosition position)
    {
        if (_currentMapConfig == null) return;

        PlayerMarkerContainer.Visibility = Visibility.Visible;
        Canvas.SetLeft(PlayerMarkerContainer, position.X - 10);
        Canvas.SetTop(PlayerMarkerContainer, position.Y - 10);

        if (position.Angle.HasValue)
        {
            PlayerRotation.Angle = position.Angle.Value;
            PlayerDirectionArrow.Visibility = Visibility.Visible;
        }
        else
        {
            PlayerDirectionArrow.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateMapView()
    {
        if (_currentMapConfig == null)
        {
            _log.Debug("UpdateMapView: _currentMapConfig is null, skipping");
            return;
        }

        var zoom = _settings.ZoomLevel;
        MapScale.ScaleX = zoom;
        MapScale.ScaleY = zoom;

        // 플레이어 마커 크기 적용
        var markerSize = _settings.PlayerMarkerSize;
        if (PlayerMarkerScale != null)
        {
            PlayerMarkerScale.ScaleX = markerSize;
            PlayerMarkerScale.ScaleY = markerSize;
        }

        // 바운더리 제한 적용 후 오프셋 설정
        var (clampedX, clampedY) = ClampMapOffset(_settings.MapOffsetX, _settings.MapOffsetY);
        _settings.MapOffsetX = clampedX;
        _settings.MapOffsetY = clampedY;

        MapTranslate.X = clampedX;
        MapTranslate.Y = clampedY;
        UpdateOverlayMarkerScales();

        _log.Debug($"UpdateMapView: Scale=({MapScale.ScaleX:F4}, {MapScale.ScaleY:F4}), Translate=({MapTranslate.X:F1}, {MapTranslate.Y:F1}), ViewMode={_settings.ViewMode}");
    }

    private void CenterOnPlayer(ScreenPosition? position = null)
    {
        if (position == null && _trackerService?.LastPosition != null)
        {
            position = _trackerService.LastPosition;
        }

        if (position == null || _currentMapConfig == null) return;

        var viewWidth = MapContainer.ActualWidth;
        var viewHeight = MapContainer.ActualHeight;
        var zoom = _settings.ZoomLevel;

        var newOffsetX = (viewWidth / 2) - (position.X * zoom);
        var newOffsetY = (viewHeight / 2) - (position.Y * zoom);

        // 바운더리 제한 적용
        (newOffsetX, newOffsetY) = ClampMapOffset(newOffsetX, newOffsetY);

        MapTranslate.X = newOffsetX;
        MapTranslate.Y = newOffsetY;

        if (_settings.ViewMode == MiniMapViewMode.Fixed)
        {
            _settings.MapOffsetX = newOffsetX;
            _settings.MapOffsetY = newOffsetY;
        }
    }

    /// <summary>
    /// 맵 오프셋을 바운더리 내로 제한합니다.
    /// 맵의 최소 25%가 항상 화면에 보이도록 합니다.
    /// </summary>
    private (double x, double y) ClampMapOffset(double offsetX, double offsetY)
    {
        if (_currentMapConfig == null) return (offsetX, offsetY);

        var viewWidth = MapContainer.ActualWidth > 0 ? MapContainer.ActualWidth : ActualWidth;
        var viewHeight = MapContainer.ActualHeight > 0 ? MapContainer.ActualHeight : ActualHeight;
        var zoom = _settings.ZoomLevel;

        var scaledMapWidth = _currentMapConfig.ImageWidth * zoom;
        var scaledMapHeight = _currentMapConfig.ImageHeight * zoom;

        // 맵의 최소 25%가 보이도록 바운더리 설정
        // 최소 오프셋: 맵이 왼쪽/위로 너무 이동하지 않도록 (맵의 오른쪽/아래가 화면에 25% 이상 보임)
        var minOffsetX = viewWidth * 0.25 - scaledMapWidth;
        var minOffsetY = viewHeight * 0.25 - scaledMapHeight;

        // 최대 오프셋: 맵이 오른쪽/아래로 너무 이동하지 않도록 (맵의 왼쪽/위가 화면에 25% 이상 보임)
        var maxOffsetX = viewWidth * 0.75;
        var maxOffsetY = viewHeight * 0.75;

        var clampedX = Math.Clamp(offsetX, minOffsetX, maxOffsetX);
        var clampedY = Math.Clamp(offsetY, minOffsetY, maxOffsetY);

        return (clampedX, clampedY);
    }

    #endregion

    #region Click-Through Mode

    private void EnableClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;

        var extendedStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);

        _isClickThrough = true;
        _settings.ClickThrough = true;

        _log.Debug("Click-through mode enabled");
    }

    private void DisableClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;

        var extendedStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);

        _isClickThrough = false;
        _settings.ClickThrough = false;

        _log.Debug("Click-through mode disabled");
    }

    public void ToggleClickThrough()
    {
        if (_isClickThrough)
            DisableClickThrough();
        else
            EnableClickThrough();
        SettingsChanged?.Invoke(_settings);
    }

    #endregion

    #region UI Event Handlers

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClickThrough) return;

        if (e.ClickCount == 2)
        {
            // 더블클릭: 기본 위치로 이동
            PositionToTopRight();
        }
        else
        {
            // 드래그 시작
            DragMove();
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClickThrough) return;

        // 휠 클릭 (중간 버튼)으로 맵 팬 시작
        if (e.ChangedButton == MouseButton.Middle)
        {
            _isPanning = true;
            _panStartPoint = e.GetPosition(MapContainer);
            _panStartOffsetX = _settings.MapOffsetX;
            _panStartOffsetY = _settings.MapOffsetY;
            Mouse.Capture(MapContainer);
            Cursor = Cursors.ScrollAll;
            e.Handled = true;
            _log.Info($"Pan START: point=({_panStartPoint.X:F0}, {_panStartPoint.Y:F0}), offset=({_panStartOffsetX:F0}, {_panStartOffsetY:F0})");
        }
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _isPanning)
        {
            _isPanning = false;
            Mouse.Capture(null);
            Cursor = Cursors.Arrow;
            e.Handled = true;
            _log.Debug("Pan ended");
        }
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            var currentPoint = e.GetPosition(MapContainer);
            var deltaX = currentPoint.X - _panStartPoint.X;
            var deltaY = currentPoint.Y - _panStartPoint.Y;

            var newOffsetX = _panStartOffsetX + deltaX;
            var newOffsetY = _panStartOffsetY + deltaY;

            // 바운더리 제한 적용
            (newOffsetX, newOffsetY) = ClampMapOffset(newOffsetX, newOffsetY);

            _settings.MapOffsetX = newOffsetX;
            _settings.MapOffsetY = newOffsetY;

            UpdateMapView();
            e.Handled = true;
        }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// 설정 창에서 변경된 모델 값을 현재 오버레이에 즉시 적용합니다.
    /// </summary>
    public void ApplyConfiguredSettings()
    {
        MainBorder.Opacity = Math.Clamp(
            _settings.Opacity,
            OverlayMiniMapSettings.MinOpacity,
            OverlayMiniMapSettings.MaxOpacity);

        var zoomChanged = Math.Abs(_appliedZoomLevel - _settings.ZoomLevel) > 0.0001;
        _appliedZoomLevel = _settings.ZoomLevel;
        if (zoomChanged)
            SetZoomLevelCore(_settings.ZoomLevel, notify: false);
        else
            UpdateMapView();

        if (_settings.ClickThrough != _isClickThrough)
        {
            if (_settings.ClickThrough)
                EnableClickThrough();
            else
                DisableClickThrough();
        }

        var floorOpacityChanged = Math.Abs(
            _appliedOtherFloorOpacity - _settings.OtherFloorOpacity) > 0.001;
        _appliedOtherFloorOpacity = _settings.OtherFloorOpacity;

        var floorSelectionChanged = false;
        if (_appliedAutoFloorSelection != _settings.AutoFloorSelection)
        {
            _appliedAutoFloorSelection = _settings.AutoFloorSelection;
            if (_settings.AutoFloorSelection)
            {
                _manualFloorSelection = false;
                floorSelectionChanged = ApplyAutomaticFloorSelection();
            }
            else
            {
                _manualFloorSelection = true;
            }
            UpdateFloorIndicator();
        }

        if ((floorOpacityChanged || floorSelectionChanged) && _currentMapConfig != null)
            QueueFloorRender(fitMap: false);
        else
            QueueMarkerRefresh();
    }

    public void ZoomIn()
    {
        _settings.ZoomIn();
        SetZoomLevelCore(_settings.ZoomLevel, notify: true);
    }

    public void ZoomOut()
    {
        _settings.ZoomOut();
        SetZoomLevelCore(_settings.ZoomLevel, notify: true);
    }

    public void IncreaseOpacity()
    {
        _settings.IncreaseOpacity();
        MainBorder.Opacity = _settings.Opacity;
        SettingsChanged?.Invoke(_settings);
    }

    public void DecreaseOpacity()
    {
        _settings.DecreaseOpacity();
        MainBorder.Opacity = _settings.Opacity;
        SettingsChanged?.Invoke(_settings);
    }

    public void SetZoomLevel(double newZoom) =>
        SetZoomLevelCore(newZoom, notify: true);

    private void SetZoomLevelCore(double newZoom, bool notify)
    {
        newZoom = Math.Clamp(
            newZoom,
            OverlayMiniMapSettings.MinZoom,
            OverlayMiniMapSettings.MaxZoom);
        _settings.ZoomLevel = newZoom;
        _appliedZoomLevel = newZoom;

        if (_currentMapConfig == null)
        {
            UpdateMapView();
            if (notify)
                SettingsChanged?.Invoke(_settings);
            return;
        }

        var viewWidth = MapContainer.ActualWidth > 0 ? MapContainer.ActualWidth : ActualWidth;
        var viewHeight = MapContainer.ActualHeight > 0 ? MapContainer.ActualHeight : ActualHeight;
        var position = _trackerService?.LastPosition;
        if (position != null)
        {
            _settings.MapOffsetX = (viewWidth / 2) - (position.X * newZoom);
            _settings.MapOffsetY = (viewHeight / 2) - (position.Y * newZoom);
        }
        else
        {
            var mapCenterX = _currentMapConfig.ImageWidth / 2.0;
            var mapCenterY = _currentMapConfig.ImageHeight / 2.0;
            _settings.MapOffsetX = (viewWidth / 2) - (mapCenterX * newZoom);
            _settings.MapOffsetY = (viewHeight / 2) - (mapCenterY * newZoom);
        }

        UpdateMapView();
        if (notify)
            SettingsChanged?.Invoke(_settings);
    }

    public void MoveFloorUp() => MoveFloor(1);

    public void MoveFloorDown() => MoveFloor(-1);

    private void MoveFloor(int direction)
    {
        var floors = GetOrderedFloors();
        if (floors.Count < 2)
            return;

        var targetFloorId = MiniMapFloorSelection.Move(floors, _selectedFloorId, direction);
        if (string.IsNullOrWhiteSpace(targetFloorId) ||
            string.Equals(targetFloorId, _selectedFloorId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedFloorId = targetFloorId;
        _manualFloorSelection = true;
        _settings.AutoFloorSelection = false;
        _appliedAutoFloorSelection = false;
        UpdateFloorIndicator();
        SettingsChanged?.Invoke(_settings);
        QueueFloorRender(fitMap: false);
    }

    public void ResumeAutomaticFloorTracking()
    {
        _settings.AutoFloorSelection = true;
        _appliedAutoFloorSelection = true;
        _manualFloorSelection = false;
        var changed = ApplyAutomaticFloorSelection();
        UpdateFloorIndicator();
        SettingsChanged?.Invoke(_settings);
        if (changed)
            QueueFloorRender(fitMap: false);
        else
            QueueMarkerRefresh();
    }

    private bool ApplyAutomaticFloorSelection()
{
    var automaticFloorId = MiniMapFloorSelection.SelectAutomatic(
        _currentMapConfig?.Floors,
        DetectCurrentFloor());
    if (string.Equals(
            _selectedFloorId,
            automaticFloorId,
            StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    _selectedFloorId = automaticFloorId;
    return true;
}

    public void CenterPlayer()
    {
        CenterOnPlayer();
        SettingsChanged?.Invoke(_settings);
    }

    public void ToggleViewMode()
    {
        _settings.ToggleViewMode();
        if (_settings.ViewMode == MiniMapViewMode.PlayerTracking)
            CenterOnPlayer();
        SettingsChanged?.Invoke(_settings);
    }

    public void ResetView()
    {
        if (_currentMapConfig == null)
            return;

        FitMapToWindow();
        UpdateMapView();
        SettingsChanged?.Invoke(_settings);
    }

    public void RefreshMap()
    {
        if (!string.IsNullOrEmpty(_currentMapKey) && _currentMapConfig != null)
            QueueFloorRender(fitMap: false);
    }

    #endregion

    #region Cleanup

    protected override void OnClosed(EventArgs e)
    {
        _markerLoadCts?.Cancel();
        _markerLoadCts?.Dispose();
        _markerLoadCts = null;
        _floorRenderCts?.Cancel();
        _floorRenderCts?.Dispose();
        _floorRenderCts = null;

        MapSvg.Source = null;
        DeleteTempSvg(_processedSvgPath);
        _processedSvgPath = null;

        if (_trackerService != null)
        {
            _trackerService.PositionUpdated -= OnPositionUpdated;
            _trackerService.MapChanged -= OnMapChanged;
            MapMarkerDbService.Instance.DataRefreshed -= OnMapMarkerDataRefreshed;
        }

        base.OnClosed(e);
    }

    #endregion
}
