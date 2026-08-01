using System.Windows;
using TarkovHelper.Services;

namespace TarkovHelper.Pages.Map;

public partial class MapPage
{
    private bool _preserveMiniMapAcrossNextUnload;
    private bool _miniMapAwareUnloadInstalled;

    /// <summary>
    /// 일반 탭 전환으로 지도 페이지가 시각 트리에서 빠질 때 사용자가 켜 둔 미니맵을 유지합니다.
    /// 프로필 전환이나 앱 종료처럼 페이지 자체가 교체되는 경우에는 호출되지 않으므로 기존 정리가 실행됩니다.
    /// </summary>
    internal void PreserveMiniMapAcrossNextTabUnload()
    {
        if (!_miniMapAwareUnloadInstalled)
        {
            Unloaded -= MapTrackerPage_Unloaded;
            Unloaded += MapTrackerPage_UnloadedWithMiniMapPersistence;
            _miniMapAwareUnloadInstalled = true;
        }

        _preserveMiniMapAcrossNextUnload = _overlayService.IsOverlayVisible;
    }

    private void MapTrackerPage_UnloadedWithMiniMapPersistence(object sender, RoutedEventArgs e)
    {
        var preserveMiniMapRuntime =
            _preserveMiniMapAcrossNextUnload && _overlayService.IsOverlayVisible;
        _preserveMiniMapAcrossNextUnload = false;

        if (!preserveMiniMapRuntime)
        {
            MapTrackerPage_Unloaded(sender, e);
            return;
        }

        _mapPageActive = false;
        _loadingCts?.Cancel();
        SaveMapState();

        // 지도 화면 자체의 UI 구독만 정리합니다. 미니맵 창, 전역 단축키,
        // 위치 추적과 레이드 감시는 탭 밖에서도 계속 동작해야 합니다.
        _progressService.ProgressChanged -= OnQuestProgressChanged;
        ActualQuestStatusService.Instance.StatusChanged -= OnQuestProgressChanged;
        ObjectiveProgressService.Instance.ObjectiveProgressChanged -= OnObjectiveProgressChanged;
        MapMarkerDbService.Instance.DataRefreshed -= OnDatabaseRefreshed;
        QuestObjectiveDbService.Instance.DataRefreshed -= OnDatabaseRefreshed;

        NormalizeMiniMapRuntimeSubscriptions();
    }

    /// <summary>
    /// 지도 탭 재진입과 탭 전환이 반복되어도 런타임 이벤트가 한 번만 실행되도록 정규화합니다.
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
