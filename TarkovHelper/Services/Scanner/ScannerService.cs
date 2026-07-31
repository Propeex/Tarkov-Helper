using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RatEye;
using RatStash;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;
using TarkovHelper.Windows.Scanner;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;
using FormsScreen = System.Windows.Forms.Screen;

namespace TarkovHelper.Services.Scanner;

/// <summary>
/// RatScanner의 이름 인식 경로만 Helper에 맞게 분리한 한국어 전용 스캐너입니다.
/// 아이콘 스캔과 검색 오버레이는 포함하지 않으며, 가격은 tarkov.dev 정적 데이터 캐시를 사용합니다.
/// </summary>
public sealed class ScannerService : IDisposable
{
    private const string EnabledSettingKey = "scanner.nameScanEnabled";
    private const string OpacitySettingKey = "scanner.minimalOpacity";
    private const string LeftSettingKey = "scanner.minimalLeft";
    private const string TopSettingKey = "scanner.minimalTop";
    private const string ClickThroughSettingKey = "scanner.minimalClickThrough";
    private const string ShowNameSettingKey = "scanner.showName";
    private const string ShowAveragePriceSettingKey = "scanner.showAveragePrice";
    private const string ShowPricePerSlotSettingKey = "scanner.showPricePerSlot";
    private const string ShowTraderPriceSettingKey = "scanner.showTraderPrice";
    private const string ShowKappaSettingKey = "scanner.showKappa";
    private const string ShowNeededSettingKey = "scanner.showNeeded";
    private const string ValidationEnvironmentVariable = "TARKOV_SCANNER_SELF_TEST";

    private static readonly ILogger Log = TarkovHelper.Services.Logging.Log.For<ScannerService>();
    private static readonly Lazy<ScannerService> LazyInstance = new(() => new ScannerService());

    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _engineSync = new();
    private readonly ScannerMouseHook _mouseHook;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly ScannerPriceService _priceService = new();

    private RatEyeEngine? _engine;
    private Database? _itemDatabase;
    private Dictionary<string, string> _officialNamesById = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TarkovItem> _itemsById = new(StringComparer.OrdinalIgnoreCase);
    private ScannerItemDisplayData? _lastDisplayData;
    private ScannerMinimalWindow? _minimalWindow;
    private int _engineScreenWidth;
    private int _engineScreenHeight;
    private bool _initialized;
    private bool _hookStarted;
    private bool _disposed;
    private string _status = "스캐너를 초기화하지 않았습니다.";

    public static ScannerService Instance => LazyInstance.Value;
    internal static bool IsInstanceCreated => LazyInstance.IsValueCreated;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ItemNameRecognized;
    public event EventHandler<bool>? EnabledChanged;
    public event EventHandler<int>? MinimalOpacityChanged;
    public event EventHandler? MinimalStateChanged;
    public event EventHandler? DisplaySettingsChanged;

    private ScannerService()
    {
        _mouseHook = new ScannerMouseHook();
        _mouseHook.LeftButtonReleased += OnLeftButtonReleased;
    }

    public bool Enabled
    {
        get => bool.TryParse(_settings.GetValue(EnabledSettingKey, bool.TrueString), out var value) && value;
        set
        {
            if (Enabled == value)
                return;

            _settings.SetValue(EnabledSettingKey, value.ToString());
            ApplyHookState();
            EnabledChanged?.Invoke(this, value);
            SetStatus(value ? "한국어 이름 스캔이 활성화되었습니다." : "이름 스캔이 비활성화되었습니다.");
        }
    }

    public int MinimalOpacity
    {
        get => int.TryParse(_settings.GetValue(OpacitySettingKey, "90"), out var value)
            ? Math.Clamp(value, 35, 100)
            : 90;
        set
        {
            var clamped = Math.Clamp(value, 35, 100);
            if (MinimalOpacity == clamped)
                return;

            _settings.SetValue(OpacitySettingKey, clamped.ToString());
            if (_minimalWindow != null)
                _minimalWindow.Opacity = clamped / 100.0;
            MinimalOpacityChanged?.Invoke(this, clamped);
        }
    }

    public string Status => _status;

    public bool IsMinimalVisible => _minimalWindow?.IsVisible == true;

    public bool MinimalClickThrough
    {
        get => ReadBool(ClickThroughSettingKey, false);
        set
        {
            if (MinimalClickThrough == value)
                return;
            _settings.SetValue(ClickThroughSettingKey, value.ToString());
            _minimalWindow?.SetClickThrough(value);
            MinimalStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool ShowName { get => ReadBool(ShowNameSettingKey, true); set => SetDisplayOption(ShowNameSettingKey, value); }
    public bool ShowAveragePrice { get => ReadBool(ShowAveragePriceSettingKey, true); set => SetDisplayOption(ShowAveragePriceSettingKey, value); }
    public bool ShowPricePerSlot { get => ReadBool(ShowPricePerSlotSettingKey, true); set => SetDisplayOption(ShowPricePerSlotSettingKey, value); }
    public bool ShowTraderPrice { get => ReadBool(ShowTraderPriceSettingKey, true); set => SetDisplayOption(ShowTraderPriceSettingKey, value); }
    public bool ShowKappa { get => ReadBool(ShowKappaSettingKey, true); set => SetDisplayOption(ShowKappaSettingKey, value); }
    public bool ShowNeeded { get => ReadBool(ShowNeededSettingKey, true); set => SetDisplayOption(ShowNeededSettingKey, value); }

    private bool ReadBool(string key, bool defaultValue) =>
        bool.TryParse(_settings.GetValue(key, defaultValue.ToString()), out var value) ? value : defaultValue;

    private void SetDisplayOption(string key, bool value)
    {
        if (ReadBool(key, true) == value)
            return;
        _settings.SetValue(key, value.ToString());
        RefreshMinimalDisplay();
        DisplaySettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsReady => _initialized && _itemDatabase != null && File.Exists(KoreanTrainedDataPath);

    private static string ScannerDataDirectory => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Assets",
        "Scanner");

    private static string TrainedDataDirectory => Path.Combine(ScannerDataDirectory, "traineddata");

    private static string KoreanTrainedDataPath => Path.Combine(TrainedDataDirectory, "kor.traineddata");

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            ApplyHookState();
            return;
        }

        SetStatus("한국어 이름 데이터를 준비하는 중입니다...");

        try
        {
            if (!ItemDbService.Instance.IsLoaded)
                await ItemDbService.Instance.LoadItemsAsync();

            RebuildItemDatabase();
            await _priceService.InitializeAsync();
            ItemDbService.Instance.DataRefreshed += OnItemDataRefreshed;
            _initialized = true;

            if (_itemDatabase == null)
            {
                SetStatus("공식 한국어 이름이 포함된 아이템 데이터가 없습니다.");
                return;
            }

            if (!File.Exists(KoreanTrainedDataPath))
            {
                SetStatus("한국어 OCR 데이터가 없습니다. 프로그램을 새 폴더에 다시 설치해 주세요.");
                return;
            }

            ApplyHookState();
            SetStatus(Enabled
                ? "준비 완료: 타르코프에서 아이템 이름을 왼쪽 클릭하세요."
                : "준비 완료: 이름 스캔이 비활성화되어 있습니다.");
        }
        catch (Exception ex)
        {
            _initialized = true;
            SetStatus($"스캐너 초기화에 실패했습니다: {ex.Message}");
            Log.Error("Scanner initialization failed", ex);
        }
    }

    private void OnItemDataRefreshed(object? sender, EventArgs e)
    {
        lock (_engineSync)
        {
            try
            {
                RebuildItemDatabase();
                _engine = null;
                _engineScreenWidth = 0;
                _engineScreenHeight = 0;
                _priceService.RequestRefresh();
            }
            catch (Exception ex)
            {
                SetStatus($"아이템 데이터 갱신 후 스캐너를 다시 준비하지 못했습니다: {ex.Message}");
                Log.Error("Scanner database refresh failed", ex);
            }
        }
    }

    private void RebuildItemDatabase()
    {
        var items = ItemDbService.Instance.AllItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.NameKo))
            .Select(item => new RatStash.Item
            {
                Id = item.Id,
                Name = item.NameKo!.Trim(),
                ShortName = item.NameKo.Trim()
            })
            .ToList();

        _officialNamesById = ItemDbService.Instance.AllItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.NameKo))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().NameKo!.Trim(),
                StringComparer.OrdinalIgnoreCase);

        _itemsById = ItemDbService.Instance.AllItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        _itemDatabase = items.Count > 0 ? Database.FromItems(items) : null;
        Log.Info($"Scanner Korean item database prepared: {items.Count} items");
    }

    private void ApplyHookState()
    {
        if (!_initialized || !File.Exists(KoreanTrainedDataPath) || !Enabled)
        {
            if (_hookStarted)
            {
                _mouseHook.Stop();
                _hookStarted = false;
            }
            return;
        }

        if (_hookStarted)
            return;

        try
        {
            _mouseHook.Start();
            _hookStarted = true;
        }
        catch (Exception ex)
        {
            SetStatus($"이름 스캔 입력을 시작하지 못했습니다: {ex.Message}");
            Log.Error("Scanner mouse hook failed", ex);
        }
    }

    private void OnLeftButtonReleased(int x, int y)
    {
        if (!IsReady || !Enabled || !IsTarkovForeground())
            return;

        _ = ScanAtAsync(new DrawingPoint(x, y));
    }

    private async Task ScanAtAsync(DrawingPoint cursor)
    {
        if (!await _scanGate.WaitAsync(0))
            return;

        try
        {
            await Task.Delay(50);

            var screen = FormsScreen.FromPoint(cursor);
            var engine = EnsureEngine(screen.Bounds.Width, screen.Bounds.Height);
            var scale = engine.Config.ProcessingConfig.Scale;
            var markerScanSize = Math.Max(1, (int)(50 * scale));
            var textWidth = Math.Max(1, (int)(600 * scale));
            var captureOrigin = new DrawingPoint(
                cursor.X - markerScanSize / 2,
                cursor.Y - markerScanSize / 2);

            using var screenshot = CaptureScreen(
                captureOrigin,
                new DrawingSize(markerScanSize + textWidth, markerScanSize));

            var inspection = engine.NewInspection(screenshot);
            if (!inspection.ContainsMarker || inspection.Item == null)
                return;

            if (!_officialNamesById.TryGetValue(inspection.Item.Id, out var officialName) ||
                string.IsNullOrWhiteSpace(officialName))
            {
                SetStatus("아이템은 인식했지만 공식 한국어 이름을 찾지 못했습니다.");
                return;
            }

            if (!_itemsById.TryGetValue(inspection.Item.Id, out var item))
            {
                SetStatus("아이템은 인식했지만 최신 로컬 데이터를 찾지 못했습니다.");
                return;
            }

            var context = await ScannerItemContextService.BuildAsync(item);
            var price = _priceService.Get(item.Id);
            var displayData = new ScannerItemDisplayData(
                item.Id,
                officialName,
                price?.AverageFleaPrice,
                price?.FleaPricePerSlot,
                price?.BestTraderName,
                price?.BestTraderPrice,
                context.IsKappaRequired,
                context.QuestRequired,
                context.HideoutRequired,
                context.Owned,
                context.AdditionalNeeded,
                price?.UpdatedAt);

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _lastDisplayData = displayData;
                RefreshMinimalDisplay();
                ItemNameRecognized?.Invoke(this, officialName);
                SetStatus("아이템 정보를 인식했습니다.");
            });
        }
        catch (Exception ex)
        {
            Log.Warning($"Korean name scan failed: {ex.Message}");
            SetStatus($"이름 인식에 실패했습니다: {ex.Message}");
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private RatEyeEngine EnsureEngine(int screenWidth, int screenHeight)
    {
        lock (_engineSync)
        {
            if (_engine != null &&
                _engineScreenWidth == screenWidth &&
                _engineScreenHeight == screenHeight)
            {
                return _engine;
            }

            if (_itemDatabase == null)
                throw new InvalidOperationException("한국어 아이템 데이터베이스가 준비되지 않았습니다.");

            Directory.CreateDirectory(ScannerDataDirectory);
            Directory.CreateDirectory(TrainedDataDirectory);

            RatEye.Config.LogDebug = false;
            RatEye.Config.Path.LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scanner.log");
            RatEye.Config.Path.TesseractLibSearchPath = AppDomain.CurrentDomain.BaseDirectory;

            var config = new RatEye.Config
            {
                PathConfig = new RatEye.Config.Path
                {
                    TrainedData = TrainedDataDirectory,
                    StaticIcons = Path.Combine(ScannerDataDirectory, "icons")
                },
                ProcessingConfig = new RatEye.Config.Processing
                {
                    Scale = RatEye.Config.Processing.Resolution2Scale(screenWidth, screenHeight),
                    Language = Language.Korean,
                    IconConfig = new RatEye.Config.Processing.Icon
                    {
                        UseStaticIcons = false
                    },
                    InventoryConfig = new RatEye.Config.Processing.Inventory
                    {
                        OptimizeHighlighted = true
                    }
                }
            };

            _engine = new RatEyeEngine(config, _itemDatabase);
            _engineScreenWidth = screenWidth;
            _engineScreenHeight = screenHeight;
            return _engine;
        }
    }

    private static Bitmap CaptureScreen(DrawingPoint origin, DrawingSize size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(origin, DrawingPoint.Empty, size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    public void ToggleMinimalWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (IsMinimalVisible)
                HideMinimalWindowCore();
            else
                ShowMinimalWindowCore();
        });
    }

    public void ShowMinimalWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(ShowMinimalWindowCore);
    }

    public void HideMinimalWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(HideMinimalWindowCore);
    }

    private void ShowMinimalWindowCore()
    {
        var window = EnsureMinimalWindow();
        PositionMinimalWindow(window);
        window.Opacity = MinimalOpacity / 100.0;
        window.SetClickThrough(MinimalClickThrough);
        RefreshMinimalDisplay();
        window.Show();
        MinimalStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HideMinimalWindowCore()
    {
        _minimalWindow?.Hide();
        MinimalStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleMinimalClickThrough() => MinimalClickThrough = !MinimalClickThrough;

    private ScannerMinimalWindow EnsureMinimalWindow()
    {
        if (_minimalWindow != null)
            return _minimalWindow;

        _minimalWindow = new ScannerMinimalWindow(this)
        {
            Opacity = MinimalOpacity / 100.0
        };
        _minimalWindow.SetClickThrough(MinimalClickThrough);
        return _minimalWindow;
    }

    private void RefreshMinimalDisplay()
    {
        if (_minimalWindow == null)
            return;

        _minimalWindow.SetDisplay(
            _lastDisplayData,
            ShowName,
            ShowAveragePrice,
            ShowPricePerSlot,
            ShowTraderPrice,
            ShowKappa,
            ShowNeeded);
    }

    private void PositionMinimalWindow(ScannerMinimalWindow window)
    {
        if (double.TryParse(_settings.GetValue(LeftSettingKey), out var left) &&
            double.TryParse(_settings.GetValue(TopSettingKey), out var top) &&
            IsVisibleOnAnyScreen(left, top))
        {
            window.Left = left;
            window.Top = top;
            return;
        }

        var workArea = System.Windows.SystemParameters.WorkArea;
        window.Left = workArea.Right - Math.Max(window.Width, 280) - 24;
        window.Top = workArea.Top + 24;
    }

    public void SaveMinimalPosition(double left, double top)
    {
        if (double.IsNaN(left) || double.IsInfinity(left) || double.IsNaN(top) || double.IsInfinity(top))
            return;

        _settings.SetValue(LeftSettingKey, left.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _settings.SetValue(TopSettingKey, top.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool IsVisibleOnAnyScreen(double left, double top)
    {
        var point = new DrawingPoint((int)left, (int)top);
        return FormsScreen.AllScreens.Any(screen => screen.WorkingArea.Contains(point));
    }

    private static bool IsTarkovForeground()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;

            GetWindowThreadProcessId(foreground, out var processId);
            if (processId == 0)
                return false;

            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            return name.Contains("EscapeFromTarkov", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Tarkov", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("EFT", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        if (System.Windows.Application.Current?.Dispatcher == null)
        {
            StatusChanged?.Invoke(this, status);
            return;
        }

        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
            StatusChanged?.Invoke(this, status);
        else
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => StatusChanged?.Invoke(this, status));
    }

    public static int RunBuildValidation()
    {
        if (Environment.GetEnvironmentVariable(ValidationEnvironmentVariable) != "1")
            return 0;

        try
        {
            if (!File.Exists(KoreanTrainedDataPath) || new FileInfo(KoreanTrainedDataPath).Length < 1_000_000)
                return 31;

            RatEye.Config.LogDebug = false;
            RatEye.Config.Path.LogFile = Path.Combine(Path.GetTempPath(), "tarkov-helper-scanner-self-test.log");
            RatEye.Config.Path.TesseractLibSearchPath = AppDomain.CurrentDomain.BaseDirectory;

            var database = Database.FromItems(new[]
            {
                new RatStash.Item
                {
                    Id = "scanner-self-test",
                    Name = "스캐너 자체 검사 아이템",
                    ShortName = "스캐너 자체 검사 아이템"
                }
            });

            var config = new RatEye.Config
            {
                PathConfig = new RatEye.Config.Path
                {
                    TrainedData = TrainedDataDirectory,
                    StaticIcons = Path.Combine(ScannerDataDirectory, "icons")
                },
                ProcessingConfig = new RatEye.Config.Processing
                {
                    Scale = RatEye.Config.Processing.Resolution2Scale(1920, 1080),
                    Language = Language.Korean,
                    IconConfig = new RatEye.Config.Processing.Icon { UseStaticIcons = false },
                    InventoryConfig = new RatEye.Config.Processing.Inventory { OptimizeHighlighted = true }
                }
            };

            var engine = new RatEyeEngine(config, database);
            using var image = new Bitmap(650, 50, PixelFormat.Format24bppRgb);
            engine.NewInspection(image);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "tarkov-helper-scanner-self-test.log"),
                ex.ToString());
            return 32;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ItemDbService.Instance.DataRefreshed -= OnItemDataRefreshed;
        _mouseHook.LeftButtonReleased -= OnLeftButtonReleased;
        _mouseHook.Dispose();
        _minimalWindow?.AllowCloseAndClose();
        _minimalWindow = null;
        _engine = null;
        _scanGate.Dispose();
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
