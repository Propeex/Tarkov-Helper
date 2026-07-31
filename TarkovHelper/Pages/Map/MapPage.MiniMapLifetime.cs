using System.Windows;
using TarkovHelper.Services;

namespace TarkovHelper.Pages.Map;

public partial class MapPage
{
    /// <summary>
    /// 지도 탭의 기존 Unloaded 정리 이후에도 사용자가 켜 둔 미니맵의 런타임을 복구합니다.
    /// 지도 페이지 인스턴스는 탭 전환 사이에 재사용되므로 기존 설정과 위치가 유지됩니다.
    /// </summary>
    internal void RestoreMiniMapRuntimeAfterTabUnload()
    {
        var application = Application.Current;
        if (application == null ||
            application.Dispatcher.HasShutdownStarted ||
            application.MainWindow?.IsVisible != true)
        {
            return;
        }

        _overlayService.ShowOverlay();
        GlobalKeyboardHookService.Instance.IsEnabled = true;
        StartAutoTracking();
        StartRaidEventMonitoring();
        NormalizeMiniMapRuntimeSubscriptions();
    }

    /// <summary>
    /// 지도 탭 재진입과 언로드 후 복구가 반복되어도 이벤트가 한 번만 실행되도록 정규화합니다.
    /// </summary>
    internal void NormalizeMiniMapRuntimeSubscriptions()
    {
        _raidEventService.RaidEvent -= OnRaidEvent;
        _raidEventService.RaidEvent += OnRaidEvent;

        GlobalKeyboardHookService.Instance.FloorKeyPressed -= OnFloorKeyPressed;
        GlobalKeyboardHookService.Instance.FloorKeyPressed += OnFloorKeyPressed;

        _overlayService.OverlayVisibilityChanged -= OnOverlayVisibilityChanged;
        _overlayService.OverlayVisibilityChanged += OnOverlayVisibilityChanged;
    }
}
