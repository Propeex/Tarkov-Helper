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
            new RoutedEventHandler(OnNavigationRadioChecked),
            handledEventsToo: true);
        return true;
    }

    private static void OnNavigationRadioChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radioButton ||
            Window.GetWindow(radioButton) is not MainWindow window)
        {
            return;
        }

        // XAML의 기본 선택 탭은 InitializeComponent 도중 Checked 이벤트를 발생시킵니다.
        // 이 시점에는 PageContent 등 이름이 지정된 컨트롤이 아직 생성되지 않았을 수 있으므로
        // 창 초기화가 끝날 때까지 미니맵 내비게이션 처리를 실행하지 않습니다.
        if (window._isLoading || !window.IsInitialized || window.PageContent == null)
            return;


        if (!radioButton.Name.StartsWith("Tab", StringComparison.Ordinal) ||
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
