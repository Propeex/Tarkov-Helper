using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TarkovHelper.Pages.Map;

namespace TarkovHelper;

public partial class MainWindow
{
    private static readonly bool MiniMapTabNavigationRegistered =
        RegisterMiniMapTabNavigation();

    private static bool RegisterMiniMapTabNavigation()
    {
        EventManager.RegisterClassHandler(
            typeof(RadioButton),
            ToggleButton.CheckedEvent,
            new RoutedEventHandler(OnNavigationTabChecked),
            handledEventsToo: true);
        return true;
    }

    private static void OnNavigationTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radioButton ||
            !radioButton.Name.StartsWith("Tab", StringComparison.Ordinal) ||
            Window.GetWindow(radioButton) is not MainWindow window ||
            ReferenceEquals(radioButton, window.TabMap) ||
            window.PageContent.Content is not MapPage mapPage)
        {
            return;
        }

        // RadioButton의 클래스 핸들러는 기존 탭 핸들러보다 먼저 실행됩니다.
        // PageContent가 교체되기 전에 현재 지도 페이지에 다음 Unloaded의 목적을 알립니다.
        mapPage.PreserveMiniMapAcrossNextTabUnload();
    }
}
