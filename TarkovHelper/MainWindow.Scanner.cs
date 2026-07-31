using System.Windows;
using System.Windows.Threading;
using TarkovHelper.Pages;
using TarkovHelper.Pages.Scanner;
using TarkovHelper.Services.Scanner;

namespace TarkovHelper;

public partial class MainWindow
{
    private AmmoPage? _ammoPage;
    private ScannerPage? _scannerPage;
    private bool _scannerIntegrationLoaded;

    private async void ScannerNavigation_TabChecked(object sender, RoutedEventArgs e)
    {
        if (sender == TabAmmo)
        {
            _ammoPage ??= new AmmoPage();
            PageContent.Content = _ammoPage;
            return;
        }

        if (sender == TabScanner)
        {
            _scannerPage ??= new ScannerPage();
            PageContent.Content = _scannerPage;
            await ScannerService.Instance.InitializeAsync();
        }
    }

    /// <summary>
    /// 미니멀 UI를 닫고 스캐너 설정 화면으로 복귀합니다.
    /// </summary>
    internal void ShowScannerTab()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowScannerTab);
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        TabContentArea.Visibility = Visibility.Visible;
        TxtWelcome.Visibility = Visibility.Collapsed;
        _scannerPage ??= new ScannerPage();
        TabScanner.IsChecked = true;
        PageContent.Content = _scannerPage;
        Activate();
    }

    internal async void InitializeScannerIntegration()
    {
        if (_scannerIntegrationLoaded)
            return;

        _scannerIntegrationLoaded = true;
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle);
        await ScannerService.Instance.InitializeAsync();
    }
}
