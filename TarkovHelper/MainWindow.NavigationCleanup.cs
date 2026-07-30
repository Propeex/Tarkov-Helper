using System.Windows;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;

namespace TarkovHelper;

public partial class MainWindow
{
    private bool _navigationCleanupInitialized;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnNavigationCleanupLoaded));
    }

    private static void OnNavigationCleanupLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            window.InitializeNavigationCleanup();
    }

    private async void InitializeNavigationCleanup()
    {
        if (_navigationCleanupInitialized)
            return;

        _navigationCleanupInitialized = true;

        // 수집가 전용 화면은 아이템 화면과 기능이 중복되므로 내비게이션에서 제거합니다.
        TabCollector.Visibility = Visibility.Collapsed;
        TabCollector.IsEnabled = false;
        if (TabCollector.IsChecked == true)
            TabQuests.IsChecked = true;

        var overlayService = OverlayMiniMapService.Instance;
        await overlayService.InitializeAsync();
        NormalizeOverlaySettings(overlayService.Settings);
        overlayService.SettingsChanged += NormalizeOverlaySettings;
        overlayService.SaveSettings();
    }

    private static void NormalizeOverlaySettings(OverlayMiniMapSettings settings)
    {
        // 다른 층은 항상 숨기며, 화면에서 제거된 레거시 단축키도 실행되지 않게 정규화합니다.
        settings.OtherFloorOpacity = 0.0;
        settings.OpacityIncreaseKey = 0;
        settings.OpacityDecreaseKey = 0;
        settings.CenterPlayerKey = 0;
        settings.ToggleViewModeKey = 0;
        settings.ToggleClickThroughKey = 0;
        settings.ResetViewKey = 0;

        var hooks = GlobalKeyboardHookService.Instance;
        hooks.OpacityIncreaseKey = 0;
        hooks.OpacityDecreaseKey = 0;
        hooks.CenterPlayerKey = 0;
        hooks.ToggleViewModeKey = 0;
        hooks.ToggleClickThroughKey = 0;
        hooks.ResetViewKey = 0;
    }
}
