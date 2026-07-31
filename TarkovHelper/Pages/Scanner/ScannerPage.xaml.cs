using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TarkovHelper.Services.Scanner;

namespace TarkovHelper.Pages.Scanner;

public partial class ScannerPage : UserControl
{
    private readonly ScannerService _scanner = ScannerService.Instance;
    private bool _updating;
    private bool _subscribed;

    public ScannerPage()
    {
        InitializeComponent();
    }

    private async void ScannerPage_Loaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        await _scanner.InitializeAsync();
        RefreshUi();
    }

    private void ScannerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed)
            return;

        _subscribed = true;
        _scanner.StatusChanged += Scanner_StatusChanged;
        _scanner.EnabledChanged += Scanner_EnabledChanged;
        _scanner.MinimalOpacityChanged += Scanner_MinimalOpacityChanged;
        _scanner.MinimalStateChanged += Scanner_MinimalStateChanged;
        _scanner.DisplaySettingsChanged += Scanner_DisplaySettingsChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;

        _subscribed = false;
        _scanner.StatusChanged -= Scanner_StatusChanged;
        _scanner.EnabledChanged -= Scanner_EnabledChanged;
        _scanner.MinimalOpacityChanged -= Scanner_MinimalOpacityChanged;
        _scanner.MinimalStateChanged -= Scanner_MinimalStateChanged;
        _scanner.DisplaySettingsChanged -= Scanner_DisplaySettingsChanged;
    }

    private void RefreshUi()
    {
        _updating = true;
        try
        {
            ChkScannerEnabled.IsChecked = _scanner.Enabled;
            ChkShowName.IsChecked = _scanner.ShowName;
            ChkShowAveragePrice.IsChecked = _scanner.ShowAveragePrice;
            ChkShowPricePerSlot.IsChecked = _scanner.ShowPricePerSlot;
            ChkShowTraderPrice.IsChecked = _scanner.ShowTraderPrice;
            ChkShowKappa.IsChecked = _scanner.ShowKappa;
            ChkShowNeeded.IsChecked = _scanner.ShowNeeded;
            SliderMinimalOpacity.Value = _scanner.MinimalOpacity;
            TxtMinimalOpacity.Text = $"{_scanner.MinimalOpacity}%";
            RefreshMinimalButtons();
            UpdateStatus(_scanner.Status);
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshMinimalButtons()
    {
        BtnMinimalToggle.Content = _scanner.IsMinimalVisible ? "미니멀 UI 끄기" : "미니멀 UI 켜기";
        BtnClickThroughToggle.Content = _scanner.MinimalClickThrough ? "클릭 투과 끄기" : "클릭 투과 켜기";
        BtnClickThroughToggle.Background = _scanner.MinimalClickThrough
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("BackgroundLightBrush");
    }

    private void ChkScannerEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_updating)
            _scanner.Enabled = ChkScannerEnabled.IsChecked == true;
    }

    private void DisplayOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;

        _scanner.ShowName = ChkShowName.IsChecked == true;
        _scanner.ShowAveragePrice = ChkShowAveragePrice.IsChecked == true;
        _scanner.ShowPricePerSlot = ChkShowPricePerSlot.IsChecked == true;
        _scanner.ShowTraderPrice = ChkShowTraderPrice.IsChecked == true;
        _scanner.ShowKappa = ChkShowKappa.IsChecked == true;
        _scanner.ShowNeeded = ChkShowNeeded.IsChecked == true;
    }

    private void SliderMinimalOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtMinimalOpacity == null)
            return;

        var value = (int)Math.Round(e.NewValue);
        TxtMinimalOpacity.Text = $"{value}%";
        if (!_updating)
            _scanner.MinimalOpacity = value;
    }

    private void BtnMinimalToggle_Click(object sender, RoutedEventArgs e) => _scanner.ToggleMinimalWindow();

    private void BtnClickThroughToggle_Click(object sender, RoutedEventArgs e) => _scanner.ToggleMinimalClickThrough();

    private void Scanner_StatusChanged(object? sender, string status) => UpdateStatus(status);

    private void Scanner_EnabledChanged(object? sender, bool enabled)
    {
        _updating = true;
        ChkScannerEnabled.IsChecked = enabled;
        _updating = false;
    }

    private void Scanner_MinimalOpacityChanged(object? sender, int opacity)
    {
        _updating = true;
        SliderMinimalOpacity.Value = opacity;
        TxtMinimalOpacity.Text = $"{opacity}%";
        _updating = false;
    }

    private void Scanner_MinimalStateChanged(object? sender, EventArgs e) => RefreshMinimalButtons();

    private void Scanner_DisplaySettingsChanged(object? sender, EventArgs e) => RefreshUi();

    private void UpdateStatus(string status)
    {
        TxtScannerStatus.Text = status;
        StatusIndicator.Fill = new SolidColorBrush(_scanner.IsReady
            ? Color.FromRgb(76, 175, 80)
            : Color.FromRgb(255, 152, 0));
    }
}
