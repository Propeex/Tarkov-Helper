using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TarkovHelper.Services.Scanner;

namespace TarkovHelper.Windows.Scanner;

public partial class ScannerMinimalWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    private readonly ScannerService _scanner;
    private bool _allowClose;
    private bool _positionReady;
    private bool _clickThrough;
    private IntPtr _windowHandle;

    public ScannerMinimalWindow(ScannerService scanner)
    {
        _scanner = scanner;
        InitializeComponent();
        Loaded += Window_Loaded;
    }

    public void SetDisplay(
        ScannerItemDisplayData? data,
        bool showName,
        bool showAveragePrice,
        bool showPricePerSlot,
        bool showTraderPrice,
        bool showKappa,
        bool showNeeded)
    {
        if (data == null)
        {
            TxtWaiting.Visibility = Visibility.Visible;
            TxtItemName.Visibility = Visibility.Collapsed;
            RowAveragePrice.Visibility = Visibility.Collapsed;
            RowPricePerSlot.Visibility = Visibility.Collapsed;
            RowTraderPrice.Visibility = Visibility.Collapsed;
            RowKappa.Visibility = Visibility.Collapsed;
            RowNeeded.Visibility = Visibility.Collapsed;
            TxtUpdatedAt.Visibility = Visibility.Collapsed;
            SepPrice.Visibility = Visibility.Collapsed;
            SepTracking.Visibility = Visibility.Collapsed;
            return;
        }

        TxtWaiting.Visibility = Visibility.Collapsed;
        TxtItemName.Text = data.OfficialKoreanName;
        TxtItemName.Visibility = showName ? Visibility.Visible : Visibility.Collapsed;

        RowAveragePrice.Visibility = showAveragePrice ? Visibility.Visible : Visibility.Collapsed;
        TxtAveragePrice.Text = FormatPrice(data.AverageFleaPrice);

        RowPricePerSlot.Visibility = showPricePerSlot ? Visibility.Visible : Visibility.Collapsed;
        TxtPricePerSlot.Text = FormatPrice(data.FleaPricePerSlot);

        RowTraderPrice.Visibility = showTraderPrice ? Visibility.Visible : Visibility.Collapsed;
        TxtTraderLabel.Text = string.IsNullOrWhiteSpace(data.BestTraderName)
            ? "최고 상인 판매가"
            : $"{data.BestTraderName} 판매가";
        TxtTraderPrice.Text = FormatPrice(data.BestTraderPrice);

        RowKappa.Visibility = showKappa ? Visibility.Visible : Visibility.Collapsed;
        TxtKappa.Text = data.IsKappaRequired ? "필요" : "해당 없음";

        RowNeeded.Visibility = showNeeded ? Visibility.Visible : Visibility.Collapsed;
        TxtAdditionalNeeded.Text = $"{data.AdditionalNeeded:N0}개";
        TxtRequirementBreakdown.Text =
            $"퀘스트 요구 {data.QuestRequired:N0} · 은신처 요구 {data.HideoutRequired:N0} · 현재 보유 {data.Owned:N0}";

        var hasPriceRows = showAveragePrice || showPricePerSlot || showTraderPrice;
        var hasTrackingRows = showKappa || showNeeded;
        SepPrice.Visibility = hasPriceRows && showName ? Visibility.Visible : Visibility.Collapsed;
        SepTracking.Visibility = hasPriceRows && hasTrackingRows ? Visibility.Visible : Visibility.Collapsed;

        TxtUpdatedAt.Text = data.PriceUpdatedAt.HasValue
            ? $"가격 기준 {data.PriceUpdatedAt.Value.ToLocalTime():MM-dd HH:mm}"
            : "가격 정보 없음";
        TxtUpdatedAt.Visibility = hasPriceRows ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        if (_windowHandle == IntPtr.Zero)
            return;

        var style = GetWindowLong(_windowHandle, GwlExStyle);
        style = enabled
            ? style | WsExTransparent | WsExLayered
            : style & ~WsExTransparent;
        SetWindowLong(_windowHandle, GwlExStyle, style);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        SetClickThrough(_scanner.MinimalClickThrough);
        _positionReady = true;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough)
            return;

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

    private static string FormatPrice(int? price) => price.HasValue ? $"₽ {price.Value:N0}" : "정보 없음";

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr window, int index, int newStyle);
}
