using System.Runtime.CompilerServices;
using System.Windows;

namespace TarkovHelper.Services.Scanner;

internal static class ScannerBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("TARKOV_SCANNER_SELF_TEST") == "1")
        {
            Environment.Exit(ScannerService.RunBuildValidation());
            return;
        }

        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMainWindowLoaded));
    }

    private static void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
            return;

        window.InitializeScannerIntegration();
        if (Application.Current != null)
        {
            Application.Current.Exit -= OnApplicationExit;
            Application.Current.Exit += OnApplicationExit;
        }
    }

    private static void OnApplicationExit(object? sender, ExitEventArgs e)
    {
        if (ScannerService.IsInstanceCreated)
            ScannerService.Instance.Dispose();
    }
}
