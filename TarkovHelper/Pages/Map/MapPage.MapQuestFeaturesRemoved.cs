using System.Windows;
using TarkovHelper.Services;

namespace TarkovHelper.Pages.Map;

/// <summary>
/// 지도 탭에서는 퀘스트 마커, 퀘스트 드로어, 관련 설정과 데이터 구독을 사용하지 않습니다.
/// 퀘스트 탭의 진행 상태, 번역, 완료 처리 기능에는 영향을 주지 않습니다.
/// </summary>
public partial class MapPage
{
    static MapPage()
    {
        EventManager.RegisterClassHandler(
            typeof(MapPage),
            LoadedEvent,
            new RoutedEventHandler(OnMapPageLoadedWithoutQuestFeatures));
    }

    private static void OnMapPageLoadedWithoutQuestFeatures(object sender, RoutedEventArgs e)
    {
        if (sender is not MapPage page)
            return;

        // 첫 화면부터 퀘스트 UI가 보이지 않도록 즉시 숨깁니다.
        page.HideMapQuestUi();

        // 원래 MapPage의 async Loaded 처리에서 드로어를 다시 열고 이벤트를 다시
        // 구독하므로, 초기화가 끝난 뒤 최종적으로 한 번 더 제거합니다.
        _ = page.RemoveMapQuestFeaturesAfterInitializationAsync();
    }

    private async Task RemoveMapQuestFeaturesAfterInitializationAsync()
    {
        await Task.Delay(100);

        for (var attempt = 0; attempt < 200 && IsLoaded && _isInitializing; attempt++)
        {
            HideMapQuestUi();
            await Task.Delay(50);
        }

        if (IsLoaded)
            RemoveMapQuestFeatures();
    }

    private void RemoveMapQuestFeatures()
    {
        // 기존 MapPage 코드가 다시 구독했더라도 매 로드마다 제거합니다.
        _progressService.ProgressChanged -= OnQuestProgressChanged;
        ActualQuestStatusService.Instance.StatusChanged -= OnQuestProgressChanged;
        ObjectiveProgressService.Instance.ObjectiveProgressChanged -= OnObjectiveProgressChanged;
        QuestObjectiveDbService.Instance.DataRefreshed -= OnDatabaseRefreshed;

        if (_questMarkerManager != null)
        {
            _questMarkerManager.ObjectiveSelected -= OnObjectiveSelectedFromManager;
            _questMarkerManager.FloorChangeRequested -= OnFloorChangeRequestedFromManager;
            _questMarkerManager.ClearMarkers();
            _questMarkerManager = null;
        }

        _currentMapObjectives.Clear();
        _selectedObjective = null;
        HideMapQuestUi();
        SettingsService.Instance.MapShowQuests = false;
    }

    private void HideMapQuestUi()
    {
        QuestObjectivesList.ItemsSource = null;
        QuestMarkersContainer.Children.Clear();
        QuestMarkersContainer.Visibility = Visibility.Collapsed;
        QuestMarkersContainer.IsHitTestVisible = false;

        QuestDrawerPanel.Visibility = Visibility.Collapsed;
        QuestDrawerPanel.IsEnabled = false;
        QuestDrawerColumn.MinWidth = 0;
        QuestDrawerColumn.Width = new GridLength(0);

        ChkShowQuestMarkers.IsChecked = false;
        ChkShowQuestMarkers.Visibility = Visibility.Collapsed;
        ChkShowQuestMarkers.IsEnabled = false;

        CollapseNamedElement("DrawerSplitter");
        CollapseNamedElement("BtnToggleDrawer");
        CollapseNamedElement("TxtDrawerToggleIcon");

        // 지도 설정 패널의 퀘스트 전용 항목을 제거합니다.
        CollapseNamedElement("ChkHideCompletedObjectives");
        CollapseNamedElement("TxtQuestStyleLabel");
        CollapseNamedElement("CmbQuestMarkerStyle");
        CollapseNamedElement("TxtQuestNameSizeLabel");
        CollapseNamedElement("SliderQuestNameTextSize");
        CollapseNamedElement("TxtQuestMarkerSizeLabel");
        CollapseNamedElement("SliderMarkerSize");
        CollapseNamedElement("TxtMarkerColorsLabel");
        CollapseNamedElement("TxtColorVisit");
        CollapseNamedElement("TxtColorMark");
        CollapseNamedElement("TxtColorPlant");
        CollapseNamedElement("TxtColorExtract");
        CollapseNamedElement("TxtColorFind");
        CollapseNamedElement("BtnResetColors");
    }

    private void CollapseNamedElement(string name)
    {
        if (FindName(name) is FrameworkElement element)
        {
            element.Visibility = Visibility.Collapsed;
            element.IsEnabled = false;
        }
    }
}
