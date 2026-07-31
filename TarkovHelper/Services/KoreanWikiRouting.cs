using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TarkovHelper.Pages;
using TarkovHelper.Pages.Map;

namespace TarkovHelper.Services;

internal static class KoreanWikiRouting
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(Button),
            ButtonBase.ClickEvent,
            new RoutedEventHandler(OnButtonClick));
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !IsWikiButton(button))
            return;

        if (!HasAncestor<QuestListPage>(button) &&
            !HasAncestor<ItemsPage>(button) &&
            !HasAncestor<MapPage>(button))
        {
            return;
        }

        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = KoreanWikiLinks.Root,
                UseShellExecute = true
            });
        }
        catch
        {
            // 브라우저 실행 실패는 기존 동작과 동일하게 무시합니다.
        }
    }

    private static bool IsWikiButton(Button button)
    {
        if (button.Name.Contains("Wiki", StringComparison.OrdinalIgnoreCase))
            return true;

        return button.Content?.ToString()?.Contains("위키", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool HasAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is T)
                return true;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
