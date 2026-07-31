using System.Windows;
using TarkovHelper.Pages;
using TarkovHelper.Pages.Scanner;

namespace TarkovHelper;

public partial class MainWindow
{
    private AmmoPage? _ammoPage;
    private ScannerPage? _scannerPage;

    private void ScannerNavigation_TabChecked(object sender, RoutedEventArgs e)
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
        }
    }
}
