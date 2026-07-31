using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using TarkovHelper.Services.Scanner;

namespace TarkovHelper.Windows.Scanner;

public partial class ScannerMinimalWindow : Window
{
    private readonly ScannerService _scanner;
    private bool _allowClose;
    private bool _positionReady;

    public ScannerMinimalWindow(ScannerService scanner)
    {
        _scanner = scanner;
        InitializeComponent();
        Loaded += (_, _) => _positionReady = true;
    }

    public void SetItemName(string officialKoreanName)
    {
        TxtItemName.Text = officialKoreanName;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            _scanner.RestoreMainWindow();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (_positionReady)
            _scanner.SaveMinimalPosition(Left, Top);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
    }

    internal void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }
}
