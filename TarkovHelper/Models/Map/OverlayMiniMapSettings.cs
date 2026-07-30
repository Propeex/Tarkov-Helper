namespace TarkovHelper.Models.Map;

/// <summary>
/// 오버레이 미니맵 뷰 모드
/// </summary>
public enum MiniMapViewMode
{
    /// <summary>
    /// 고정 뷰 - 전체 맵 표시, 줌으로 조절
    /// </summary>
    Fixed = 0,

    /// <summary>
    /// 플레이어 추적 뷰 - 플레이어가 항상 중앙
    /// </summary>
    PlayerTracking = 1
}

/// <summary>
/// 설정 창에서 지정할 수 있는 오버레이 미니맵 동작입니다.
/// </summary>
public enum OverlayMiniMapHotkeyAction
{
    ZoomIn,
    ZoomOut,
    FloorUp,
    FloorDown,
    OpacityIncrease,
    OpacityDecrease,
    CenterPlayer,
    ToggleViewMode,
    ToggleClickThrough,
    ResetView,
    ResumeAutoFloor
}

/// <summary>
/// 오버레이 미니맵 설정
/// </summary>
public sealed class OverlayMiniMapSettings
{
    private static readonly OverlayMiniMapHotkeyAction[] ConfigurableHotkeyActions =
        Enum.GetValues<OverlayMiniMapHotkeyAction>();

    /// <summary>
    /// 오버레이 활성화 여부
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 오버레이 X 위치 (화면 기준)
    /// </summary>
    public double PositionX { get; set; } = -1;

    /// <summary>
    /// 오버레이 Y 위치 (화면 기준)
    /// </summary>
    public double PositionY { get; set; } = -1;

    /// <summary>
    /// 오버레이 너비
    /// </summary>
    public double Width { get; set; } = 300;

    /// <summary>
    /// 오버레이 높이
    /// </summary>
    public double Height { get; set; } = 300;

    /// <summary>
    /// 전체 오버레이 투명도 (0.1 ~ 1.0)
    /// </summary>
    public double Opacity { get; set; } = 0.8;

    /// <summary>
    /// 선택하지 않은 층의 지도와 마커 투명도 (0.0 ~ 1.0)
    /// </summary>
    public double OtherFloorOpacity { get; set; } = 0.3;

    /// <summary>
    /// 위치 정보로 현재 층을 자동 선택할지 여부
    /// </summary>
    public bool AutoFloorSelection { get; set; } = true;

    /// <summary>
    /// 줌 레벨 (0.01 ~ 4.0)
    /// </summary>
    public double ZoomLevel { get; set; } = 1.0;

    /// <summary>
    /// 플레이어 마커 크기 배율 (0.5 ~ 3.0)
    /// </summary>
    public double PlayerMarkerSize { get; set; } = 1.0;

    /// <summary>
    /// 뷰 모드 (고정/플레이어 추적)
    /// </summary>
    public MiniMapViewMode ViewMode { get; set; } = MiniMapViewMode.PlayerTracking;

    /// <summary>
    /// Click-through 모드 (마우스 클릭 통과)
    /// </summary>
    public bool ClickThrough { get; set; } = false;

    /// <summary>
    /// 줌 인 단축키 (기본값: NumPad +)
    /// </summary>
    public int ZoomInKey { get; set; } = 0x6B;

    /// <summary>
    /// 줌 아웃 단축키 (기본값: NumPad -)
    /// </summary>
    public int ZoomOutKey { get; set; } = 0x6D;

    /// <summary>
    /// 위층 이동 단축키 (기본값: PageUp)
    /// </summary>
    public int FloorUpKey { get; set; } = 0x21;

    /// <summary>
    /// 아래층 이동 단축키 (기본값: PageDown)
    /// </summary>
    public int FloorDownKey { get; set; } = 0x22;

    /// <summary>
    /// 전체 투명도 증가 단축키. 0이면 미지정입니다.
    /// </summary>
    public int OpacityIncreaseKey { get; set; } = 0;

    /// <summary>
    /// 전체 투명도 감소 단축키. 0이면 미지정입니다.
    /// </summary>
    public int OpacityDecreaseKey { get; set; } = 0;

    /// <summary>
    /// 플레이어 중앙 맞춤 단축키. 0이면 미지정입니다.
    /// </summary>
    public int CenterPlayerKey { get; set; } = 0;

    /// <summary>
    /// 고정/플레이어 추적 뷰 전환 단축키. 0이면 미지정입니다.
    /// </summary>
    public int ToggleViewModeKey { get; set; } = 0;

    /// <summary>
    /// 클릭 투과 전환 단축키. 0이면 미지정입니다.
    /// </summary>
    public int ToggleClickThroughKey { get; set; } = 0;

    /// <summary>
    /// 확대율과 위치 초기화 단축키. 0이면 미지정입니다.
    /// </summary>
    public int ResetViewKey { get; set; } = 0;

    /// <summary>
    /// 자동 층 추적 복귀 단축키. 0이면 미지정입니다.
    /// </summary>
    public int ResumeAutoFloorKey { get; set; } = 0;

    /// <summary>
    /// 퀘스트 마커 표시 여부
    /// </summary>
    public bool ShowQuestMarkers { get; set; } = true;

    /// <summary>
    /// 탈출구 마커 표시 여부
    /// </summary>
    public bool ShowExtractMarkers { get; set; } = true;

    /// <summary>
    /// 맵 오프셋 X (고정 뷰 모드에서 팬 위치)
    /// </summary>
    public double MapOffsetX { get; set; } = 0;

    /// <summary>
    /// 맵 오프셋 Y (고정 뷰 모드에서 팬 위치)
    /// </summary>
    public double MapOffsetY { get; set; } = 0;

    public const double MinWidth = 200;
    public const double MaxWidth = 800;
    public const double MinHeight = 200;
    public const double MaxHeight = 800;
    public const double MinOpacity = 0.1;
    public const double MaxOpacity = 1.0;
    public const double MinOtherFloorOpacity = 0.0;
    public const double MaxOtherFloorOpacity = 1.0;
    public const double MinZoom = 0.01;
    public const double MaxZoom = 4.0;
    public const double ZoomStep = 0.05;
    public const double OpacityStep = 0.05;

    public void ResetToDefaults()
    {
        Enabled = false;
        PositionX = -1;
        PositionY = -1;
        Width = 300;
        Height = 300;
        Opacity = 0.8;
        OtherFloorOpacity = 0.3;
        AutoFloorSelection = true;
        ZoomLevel = 1.0;
        PlayerMarkerSize = 1.0;
        ViewMode = MiniMapViewMode.PlayerTracking;
        ClickThrough = false;
        ZoomInKey = 0x6B;
        ZoomOutKey = 0x6D;
        FloorUpKey = 0x21;
        FloorDownKey = 0x22;
        OpacityIncreaseKey = 0;
        OpacityDecreaseKey = 0;
        CenterPlayerKey = 0;
        ToggleViewModeKey = 0;
        ToggleClickThroughKey = 0;
        ResetViewKey = 0;
        ResumeAutoFloorKey = 0;
        ShowQuestMarkers = true;
        ShowExtractMarkers = true;
        MapOffsetX = 0;
        MapOffsetY = 0;
    }

    public void ZoomIn() => ZoomLevel = Math.Min(MaxZoom, ZoomLevel + ZoomStep);

    public void ZoomOut() => ZoomLevel = Math.Max(MinZoom, ZoomLevel - ZoomStep);

    public void IncreaseOpacity() =>
        Opacity = Math.Min(MaxOpacity, Opacity + OpacityStep);

    public void DecreaseOpacity() =>
        Opacity = Math.Max(MinOpacity, Opacity - OpacityStep);

    public void ToggleViewMode()
    {
        ViewMode = ViewMode == MiniMapViewMode.Fixed
            ? MiniMapViewMode.PlayerTracking
            : MiniMapViewMode.Fixed;
    }

    public void ToggleClickThrough() => ClickThrough = !ClickThrough;

    /// <summary>
    /// 동일한 키는 한 동작에만 배정합니다. 새 동작에 지정하면 기존 배정을 해제합니다.
    /// </summary>
    public void SetHotkey(OverlayMiniMapHotkeyAction action, int virtualKey)
    {
        virtualKey = Math.Max(0, virtualKey);
        if (virtualKey != 0)
        {
            foreach (var other in ConfigurableHotkeyActions)
            {
                if (other != action && GetHotkey(other) == virtualKey)
                    SetHotkeyCore(other, 0);
            }
        }

        SetHotkeyCore(action, virtualKey);
    }

    public int GetHotkey(OverlayMiniMapHotkeyAction action) => action switch
    {
        OverlayMiniMapHotkeyAction.ZoomIn => ZoomInKey,
        OverlayMiniMapHotkeyAction.ZoomOut => ZoomOutKey,
        OverlayMiniMapHotkeyAction.FloorUp => FloorUpKey,
        OverlayMiniMapHotkeyAction.FloorDown => FloorDownKey,
        OverlayMiniMapHotkeyAction.OpacityIncrease => OpacityIncreaseKey,
        OverlayMiniMapHotkeyAction.OpacityDecrease => OpacityDecreaseKey,
        OverlayMiniMapHotkeyAction.CenterPlayer => CenterPlayerKey,
        OverlayMiniMapHotkeyAction.ToggleViewMode => ToggleViewModeKey,
        OverlayMiniMapHotkeyAction.ToggleClickThrough => ToggleClickThroughKey,
        OverlayMiniMapHotkeyAction.ResetView => ResetViewKey,
        OverlayMiniMapHotkeyAction.ResumeAutoFloor => ResumeAutoFloorKey,
        _ => 0
    };

    public OverlayMiniMapHotkeyAction? GetActionForHotkey(int virtualKey)
    {
        if (virtualKey == 0)
            return null;

        foreach (var action in ConfigurableHotkeyActions)
        {
            if (GetHotkey(action) == virtualKey)
                return action;
        }

        return null;
    }

    private void SetHotkeyCore(OverlayMiniMapHotkeyAction action, int virtualKey)
    {
        switch (action)
        {
            case OverlayMiniMapHotkeyAction.ZoomIn: ZoomInKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.ZoomOut: ZoomOutKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.FloorUp: FloorUpKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.FloorDown: FloorDownKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.OpacityIncrease: OpacityIncreaseKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.OpacityDecrease: OpacityDecreaseKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.CenterPlayer: CenterPlayerKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.ToggleViewMode: ToggleViewModeKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.ToggleClickThrough: ToggleClickThroughKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.ResetView: ResetViewKey = virtualKey; break;
            case OverlayMiniMapHotkeyAction.ResumeAutoFloor: ResumeAutoFloorKey = virtualKey; break;
        }
    }

    public void CopyFrom(OverlayMiniMapSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Opacity = other.Opacity;
        OtherFloorOpacity = other.OtherFloorOpacity;
        AutoFloorSelection = other.AutoFloorSelection;
        ZoomLevel = other.ZoomLevel;
        PlayerMarkerSize = other.PlayerMarkerSize;
        ViewMode = other.ViewMode;
        ClickThrough = other.ClickThrough;
        ZoomInKey = other.ZoomInKey;
        ZoomOutKey = other.ZoomOutKey;
        FloorUpKey = other.FloorUpKey;
        FloorDownKey = other.FloorDownKey;
        OpacityIncreaseKey = other.OpacityIncreaseKey;
        OpacityDecreaseKey = other.OpacityDecreaseKey;
        CenterPlayerKey = other.CenterPlayerKey;
        ToggleViewModeKey = other.ToggleViewModeKey;
        ToggleClickThroughKey = other.ToggleClickThroughKey;
        ResetViewKey = other.ResetViewKey;
        ResumeAutoFloorKey = other.ResumeAutoFloorKey;
        ShowQuestMarkers = other.ShowQuestMarkers;
        ShowExtractMarkers = other.ShowExtractMarkers;
    }

    public OverlayMiniMapSettings Clone()
    {
        return new OverlayMiniMapSettings
        {
            Enabled = Enabled,
            PositionX = PositionX,
            PositionY = PositionY,
            Width = Width,
            Height = Height,
            Opacity = Opacity,
            OtherFloorOpacity = OtherFloorOpacity,
            AutoFloorSelection = AutoFloorSelection,
            ZoomLevel = ZoomLevel,
            PlayerMarkerSize = PlayerMarkerSize,
            ViewMode = ViewMode,
            ClickThrough = ClickThrough,
            ZoomInKey = ZoomInKey,
            ZoomOutKey = ZoomOutKey,
            FloorUpKey = FloorUpKey,
            FloorDownKey = FloorDownKey,
            OpacityIncreaseKey = OpacityIncreaseKey,
            OpacityDecreaseKey = OpacityDecreaseKey,
            CenterPlayerKey = CenterPlayerKey,
            ToggleViewModeKey = ToggleViewModeKey,
            ToggleClickThroughKey = ToggleClickThroughKey,
            ResetViewKey = ResetViewKey,
            ResumeAutoFloorKey = ResumeAutoFloorKey,
            ShowQuestMarkers = ShowQuestMarkers,
            ShowExtractMarkers = ShowExtractMarkers,
            MapOffsetX = MapOffsetX,
            MapOffsetY = MapOffsetY
        };
    }
}
