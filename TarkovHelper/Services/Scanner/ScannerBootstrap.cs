using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using TarkovHelper.Pages.Map;
using TarkovHelper.Services;

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

        EventManager.RegisterClassHandler(
            typeof(MapPage),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnMapPageUnloaded),
            handledEventsToo: true);

        EventManager.RegisterClassHandler(
            typeof(MapPage),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMapPageLoaded),
            handledEventsToo: true);
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

    private static void OnMapPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MapPage page || !OverlayMiniMapService.Instance.IsOverlayVisible)
            return;

        // MapPage의 기존 Unloaded 핸들러가 정리를 마친 뒤 미니맵 런타임만 복구합니다.
        page.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(page.RestoreMiniMapRuntimeAfterTabUnload));
    }

    private static void OnMapPageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MapPage page)
            return;

        // 기존 Loaded 핸들러가 구독을 마친 뒤 중복 이벤트를 한 번으로 정규화합니다.
        page.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(page.NormalizeMiniMapRuntimeSubscriptions));
    }

    private static void OnApplicationExit(object? sender, ExitEventArgs e)
    {
        if (ScannerService.IsInstanceCreated)
            ScannerService.Instance.Dispose();
    }
}
