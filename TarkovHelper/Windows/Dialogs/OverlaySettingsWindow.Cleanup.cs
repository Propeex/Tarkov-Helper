using System.Windows;
using System.Windows.Controls;

namespace TarkovHelper.Windows.Dialogs;

public partial class OverlaySettingsWindow
{
    private bool _cleanupApplied;

    static OverlaySettingsWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(OverlaySettingsWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnCleanupLoaded));
    }

    private static void OnCleanupLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is OverlaySettingsWindow window)
            window.ApplyCleanup();
    }

    private void ApplyCleanup()
    {
        if (_cleanupApplied)
            return;

        _cleanupApplied = true;
        Height = 610;

        var previousInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            // 다른 층은 항상 숨기므로 관련 조절 UI와 레거시 상태를 제거합니다.
            _settings.OtherFloorOpacity = 0.0;
            SliderOtherFloorOpacity.Value = 0;
            ChkCurrentFloorOnly.IsChecked = true;

            _settings.OpacityIncreaseKey = 0;
            _settings.OpacityDecreaseKey = 0;
            _settings.CenterPlayerKey = 0;
            _settings.ToggleViewModeKey = 0;
            _settings.ToggleClickThroughKey = 0;
            _settings.ResetViewKey = 0;
        }
        finally
        {
            _isInitializing = previousInitializing;
        }

        if (SliderOtherFloorOpacity.Parent is Panel displayPanel)
        {
            var sliderIndex = displayPanel.Children.IndexOf(SliderOtherFloorOpacity);
            CollapseChild(displayPanel, sliderIndex - 1);
            CollapseChild(displayPanel, sliderIndex);
            CollapseChild(displayPanel, sliderIndex + 1);
        }

        if (ChkCurrentFloorOnly.Parent is Panel floorPanel)
        {
            var currentOnlyIndex = floorPanel.Children.IndexOf(ChkCurrentFloorOnly);
            CollapseChild(floorPanel, currentOnlyIndex);
            CollapseChild(floorPanel, currentOnlyIndex + 1);

            var autoIndex = floorPanel.Children.IndexOf(ChkAutoFloorSelection);
            if (autoIndex >= 0 && autoIndex + 1 < floorPanel.Children.Count &&
                floorPanel.Children[autoIndex + 1] is TextBlock autoHint)
            {
                autoHint.Text = "위층·아래층 단축키를 사용하면 수동 모드로 전환됩니다. 자동 층 추적 복귀 단축키로 다시 활성화할 수 있습니다.";
            }

            for (var index = 0; index < floorPanel.Children.Count; index++)
            {
                if (floorPanel.Children[index] is TextBlock header &&
                    string.Equals(header.Text, "즉시 작업", StringComparison.Ordinal))
                {
                    header.Visibility = Visibility.Collapsed;
                    CollapseChild(floorPanel, index + 1);
                    break;
                }
            }
        }

        CollapseParent(BtnOpacityIncreaseKey);
        CollapseParent(BtnOpacityDecreaseKey);
        CollapseParent(BtnCenterPlayerKey);
        CollapseParent(BtnToggleViewModeKey);
        CollapseParent(BtnToggleClickThroughKey);
        CollapseParent(BtnResetViewKey);

        ApplySettings();
    }

    private static void CollapseParent(FrameworkElement element)
    {
        if (element.Parent is FrameworkElement parent)
            parent.Visibility = Visibility.Collapsed;
    }

    private static void CollapseChild(Panel panel, int index)
    {
        if (index >= 0 && index < panel.Children.Count &&
            panel.Children[index] is FrameworkElement element)
        {
            element.Visibility = Visibility.Collapsed;
        }
    }
}
