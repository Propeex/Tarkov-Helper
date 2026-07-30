using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;

namespace TarkovHelper.Windows.Dialogs;

/// <summary>
/// 오버레이 미니맵의 표시 방식과 전역 단축키를 구성합니다.
/// </summary>
public partial class OverlaySettingsWindow : Window
{
    private readonly OverlayMiniMapSettings _settings;
    private readonly OverlayMiniMapService _overlayService;
    private readonly Dictionary<OverlayMiniMapHotkeyAction, Button> _hotkeyButtons;
    private bool _isInitializing = true;
    private OverlayMiniMapHotkeyAction? _captureAction;

    public event Action<OverlayMiniMapSettings>? SettingsApplied;

    public OverlaySettingsWindow(OverlayMiniMapSettings settings, OverlayMiniMapService overlayService)
    {
        InitializeComponent();

        _settings = settings.Clone();
        _overlayService = overlayService ?? throw new ArgumentNullException(nameof(overlayService));
        _overlayService.SettingsChanged += OnOverlaySettingsChanged;
        _hotkeyButtons = new Dictionary<OverlayMiniMapHotkeyAction, Button>
        {
            [OverlayMiniMapHotkeyAction.ZoomIn] = BtnZoomInKey,
            [OverlayMiniMapHotkeyAction.ZoomOut] = BtnZoomOutKey,
            [OverlayMiniMapHotkeyAction.FloorUp] = BtnFloorUpKey,
            [OverlayMiniMapHotkeyAction.FloorDown] = BtnFloorDownKey,
            [OverlayMiniMapHotkeyAction.OpacityIncrease] = BtnOpacityIncreaseKey,
            [OverlayMiniMapHotkeyAction.OpacityDecrease] = BtnOpacityDecreaseKey,
            [OverlayMiniMapHotkeyAction.CenterPlayer] = BtnCenterPlayerKey,
            [OverlayMiniMapHotkeyAction.ToggleViewMode] = BtnToggleViewModeKey,
            [OverlayMiniMapHotkeyAction.ToggleClickThrough] = BtnToggleClickThroughKey,
            [OverlayMiniMapHotkeyAction.ResetView] = BtnResetViewKey,
            [OverlayMiniMapHotkeyAction.ResumeAutoFloor] = BtnResumeAutoFloorKey
        };

        LoadSettings();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        SliderOpacity.Value = Math.Clamp(_settings.Opacity, OverlayMiniMapSettings.MinOpacity, OverlayMiniMapSettings.MaxOpacity) * 100;
        SliderOtherFloorOpacity.Value = Math.Clamp(_settings.OtherFloorOpacity, OverlayMiniMapSettings.MinOtherFloorOpacity, OverlayMiniMapSettings.MaxOtherFloorOpacity) * 100;
        SliderZoom.Value = Math.Clamp(_settings.ZoomLevel, OverlayMiniMapSettings.MinZoom, OverlayMiniMapSettings.MaxZoom) * 100;
        SliderMarkerSize.Value = Math.Clamp(_settings.PlayerMarkerSize, 0.5, 3.0) * 100;

        ChkAutoFloorSelection.IsChecked = _settings.AutoFloorSelection;
        RbFixed.IsChecked = _settings.ViewMode == MiniMapViewMode.Fixed;
        RbTracking.IsChecked = _settings.ViewMode == MiniMapViewMode.PlayerTracking;
        ChkClickThrough.IsChecked = _settings.ClickThrough;

        UpdateDisplays();
        UpdateKeyDisplays();
    }

    private void OnOverlaySettingsChanged(OverlayMiniMapSettings settings)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnOverlaySettingsChanged(settings));
            return;
        }

        _settings.CopyFrom(settings);
        _isInitializing = true;
        LoadSettings();
        _isInitializing = false;
    }

    private void UpdateDisplays()
    {
        if (TxtOpacity != null)
            TxtOpacity.Text = $"{(int)SliderOpacity.Value}%";
        if (TxtOtherFloorOpacity != null)
            TxtOtherFloorOpacity.Text = $"{(int)SliderOtherFloorOpacity.Value}%";
        if (TxtZoom != null)
            TxtZoom.Text = $"{SliderZoom.Value / 100:F2}x";
        if (TxtMarkerSize != null)
            TxtMarkerSize.Text = $"{SliderMarkerSize.Value / 100:F1}x";
    }

    private void UpdateKeyDisplays()
    {
        foreach (var (action, button) in _hotkeyButtons)
        {
            if (_captureAction == action)
            {
                button.Content = "입력 대기...";
                continue;
            }

            var virtualKey = _settings.GetHotkey(action);
            button.Content = virtualKey == 0
                ? "미지정"
                : KeyInterop.KeyFromVirtualKey(virtualKey).ToString();
        }
    }

    private void ApplySettings()
{
    if (_isInitializing)
        return;

    _settings.Opacity = SliderOpacity.Value / 100.0;
    _settings.OtherFloorOpacity = SliderOtherFloorOpacity.Value / 100.0;
    _settings.ZoomLevel = SliderZoom.Value / 100.0;
    _settings.PlayerMarkerSize = SliderMarkerSize.Value / 100.0;
    _settings.AutoFloorSelection = ChkAutoFloorSelection.IsChecked == true;
    _settings.ViewMode = RbTracking.IsChecked == true
        ? MiniMapViewMode.PlayerTracking
        : MiniMapViewMode.Fixed;
    _settings.ClickThrough = ChkClickThrough.IsChecked == true;

    SettingsApplied?.Invoke(_settings);
}

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDisplays();
        ApplySettings();
    }

    private void SliderOtherFloorOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDisplays();
        ApplySettings();
    }

    private void SliderZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDisplays();
        ApplySettings();
    }

    private void SliderMarkerSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDisplays();
        ApplySettings();
    }

    private void AutoFloorSelection_Changed(object sender, RoutedEventArgs e) => ApplySettings();

    private void ViewMode_Changed(object sender, RoutedEventArgs e) => ApplySettings();

    private void ClickThrough_Changed(object sender, RoutedEventArgs e) => ApplySettings();

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.Tag is not string actionName ||
            !Enum.TryParse<OverlayMiniMapHotkeyAction>(actionName, out var action))
        {
            return;
        }

        _captureAction = action;
        GlobalKeyboardHookService.Instance.OverlayHotkeysSuppressed = true;
        UpdateKeyDisplays();
        Focus();
        Keyboard.Focus(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_captureAction.HasValue)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var action = _captureAction.Value;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            FinishCapture();
            return;
        }

        if (key is Key.Delete or Key.Back)
        {
            _settings.SetHotkey(action, 0);
            FinishCapture(apply: true);
            return;
        }

        if (IsModifierKey(key))
            return;

        if (IsReservedKey(key))
        {
            MessageBox.Show(
                "Ctrl+M, Ctrl+L 및 NumPad 0~5와 충돌할 수 있는 키는 미니맵 동작에 지정할 수 없습니다.",
                "예약된 단축키",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
            return;

        _settings.SetHotkey(action, virtualKey);
        FinishCapture(apply: true);
    }

    private void FinishCapture(bool apply = false)
    {
        _captureAction = null;
        GlobalKeyboardHookService.Instance.OverlayHotkeysSuppressed = false;
        UpdateKeyDisplays();
        if (apply)
            ApplySettings();
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;

    private static bool IsReservedKey(Key key) =>
        key is Key.M or Key.L or
        Key.NumPad0 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or Key.NumPad4 or Key.NumPad5;

    private void BtnFloorUp_Click(object sender, RoutedEventArgs e) => _overlayService.MoveFloorUp();

    private void BtnFloorDown_Click(object sender, RoutedEventArgs e) => _overlayService.MoveFloorDown();

    private void BtnCenterPlayer_Click(object sender, RoutedEventArgs e) => _overlayService.CenterPlayer();

    private void BtnResumeAutoFloor_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoFloorSelection = true;
        _isInitializing = true;
        ChkAutoFloorSelection.IsChecked = true;
        _isInitializing = false;
        SettingsApplied?.Invoke(_settings);
        _overlayService.ResumeAutomaticFloorTracking();
    }

    private void BtnResetView_Click(object sender, RoutedEventArgs e) => _overlayService.ResetView();

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.ResetToDefaults();
        _isInitializing = true;
        LoadSettings();
        _isInitializing = false;
        ApplySettings();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
{
    GlobalKeyboardHookService.Instance.OverlayHotkeysSuppressed = false;
    _overlayService.SettingsChanged -= OnOverlaySettingsChanged;
    base.OnClosed(e);
}
}
