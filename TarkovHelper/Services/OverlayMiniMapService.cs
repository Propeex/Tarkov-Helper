using System.Text.Json;
using TarkovHelper.Models.Map;
using TarkovHelper.Services.Logging;
using TarkovHelper.Windows;
using TarkovHelper.Windows.Dialogs;

namespace TarkovHelper.Services;

/// <summary>
/// 오버레이 미니맵 창의 초기화, 표시 상태와 설정 저장을 관리합니다.
/// </summary>
public sealed class OverlayMiniMapService : IDisposable
{
    private static readonly ILogger _log = Log.For<OverlayMiniMapService>();
    private static readonly object InstanceLock = new();
    private static OverlayMiniMapService? _instance;

    public static OverlayMiniMapService Instance
    {
        get
        {
            if (_instance != null)
                return _instance;

            lock (InstanceLock)
            {
                return _instance ??= new OverlayMiniMapService();
            }
        }
    }

    private const string SettingsKey = "overlayMiniMap.settings";

    private readonly UserDataDbService _userDataDb = UserDataDbService.Instance;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);

    private OverlayMiniMapWindow? _overlayWindow;
    private OverlaySettingsWindow? _settingsWindow;
    private OverlayMiniMapSettings _settings = new();
    private bool _isInitialized;
    private bool _disposed;

    public event Action<bool>? OverlayVisibilityChanged;
    public event Action<OverlayMiniMapSettings>? SettingsChanged;

    public OverlayMiniMapSettings Settings => _settings;
    public bool IsOverlayVisible => _overlayWindow?.IsVisible == true;

    private OverlayMiniMapService()
    {
    }

    public async Task InitializeAsync()
    {
        if (_disposed || _isInitialized)
            return;

        await _initializeGate.WaitAsync();
        try
        {
            if (_disposed || _isInitialized)
                return;

            await LoadSettingsAsync();
            SubscribeHotkeys();
            _isInitialized = true;
            _log.Info("OverlayMiniMapService initialized");

            if (_settings.Enabled)
                ShowOverlayCore();
        }
        catch (Exception ex)
        {
            _isInitialized = false;
            _log.Error("Failed to initialize OverlayMiniMapService", ex);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public void ShowOverlay()
    {
        if (_disposed)
            return;

        if (!_isInitialized)
        {
            _ = InitializeAndShowAsync();
            return;
        }

        ShowOverlayCore();
    }

    public void HideOverlay()
    {
        if (_disposed)
            return;

        try
        {
            _overlayWindow?.Hide();
            _settings.Enabled = false;
            OverlayVisibilityChanged?.Invoke(false);
            _ = SaveSettingsAsync();
            _log.Debug("Overlay hidden");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to hide overlay", ex);
        }
    }

    public void ToggleOverlay()
    {
        if (_disposed)
            return;

        if (!_isInitialized)
        {
            // 초기화 전 버튼을 눌러도 첫 클릭에서 바로 표시되도록 합니다.
            _ = InitializeAndShowAsync();
            return;
        }

        if (IsOverlayVisible)
            HideOverlay();
        else
            ShowOverlayCore();
    }

    private async Task InitializeAndShowAsync()
    {
        await InitializeAsync();
        if (!_disposed && _isInitialized && !IsOverlayVisible)
            ShowOverlayCore();
    }

    private void ShowOverlayCore()
    {
        try
        {
            if (_overlayWindow == null)
                CreateOverlayWindow();

            _overlayWindow!.Show();
            _overlayWindow.Activate();
            _settings.Enabled = true;
            OverlayVisibilityChanged?.Invoke(true);
            _ = SaveSettingsAsync();
            _log.Debug("Overlay shown");
        }
        catch (InvalidOperationException ex)
        {
            // 닫힌 WPF Window는 다시 Show할 수 없습니다. 참조를 버리고 새 창으로 복구합니다.
            _log.Warning($"Overlay window was no longer reusable; recreating it: {ex.Message}");
            DetachOverlayWindow();

            try
            {
                CreateOverlayWindow();
                _overlayWindow!.Show();
                _overlayWindow.Activate();
                _settings.Enabled = true;
                OverlayVisibilityChanged?.Invoke(true);
                _ = SaveSettingsAsync();
            }
            catch (Exception retryException)
            {
                _settings.Enabled = false;
                _log.Error("Failed to recreate overlay", retryException);
            }
        }
        catch (Exception ex)
        {
            _settings.Enabled = false;
            _log.Error("Failed to show overlay", ex);
        }
    }

    private void CreateOverlayWindow()
    {
        var window = new OverlayMiniMapWindow(_settings);
        window.SettingsChanged += OnOverlaySettingsChanged;
        window.OverlayClosed += OnOverlayClosed;
        _overlayWindow = window;
    }

    private void DetachOverlayWindow()
    {
        if (_overlayWindow == null)
            return;

        _overlayWindow.SettingsChanged -= OnOverlaySettingsChanged;
        _overlayWindow.OverlayClosed -= OnOverlayClosed;
        _overlayWindow = null;
    }

    private void OnOverlaySettingsChanged(OverlayMiniMapSettings settings)
    {
        _settings = settings;
        SyncHotkeys();
        _ = SaveSettingsAsync();
        SettingsChanged?.Invoke(settings);
    }

    private void OnOverlayClosed()
    {
        // 닫힌 창 객체를 보관하면 다음 Show()가 InvalidOperationException으로 실패합니다.
        DetachOverlayWindow();
        _settings.Enabled = false;
        OverlayVisibilityChanged?.Invoke(false);
        _ = SaveSettingsAsync();
        _log.Debug("Overlay window closed and released");
    }

    private void SubscribeHotkeys()
    {
        var hooks = GlobalKeyboardHookService.Instance;
        hooks.OverlayTogglePressed -= OnOverlayTogglePressed;
        hooks.OverlaySettingsPressed -= OnSettingsPressed;
        hooks.OverlayZoomInPressed -= OnZoomInPressed;
        hooks.OverlayZoomOutPressed -= OnZoomOutPressed;

        hooks.OverlayTogglePressed += OnOverlayTogglePressed;
        hooks.OverlaySettingsPressed += OnSettingsPressed;
        hooks.OverlayZoomInPressed += OnZoomInPressed;
        hooks.OverlayZoomOutPressed += OnZoomOutPressed;
        SyncHotkeys();
    }

    private void UnsubscribeHotkeys()
    {
        var hooks = GlobalKeyboardHookService.Instance;
        hooks.OverlayTogglePressed -= OnOverlayTogglePressed;
        hooks.OverlaySettingsPressed -= OnSettingsPressed;
        hooks.OverlayZoomInPressed -= OnZoomInPressed;
        hooks.OverlayZoomOutPressed -= OnZoomOutPressed;
    }

    private void OnOverlayTogglePressed()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ToggleOverlay);
    }

    private void OnSettingsPressed()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ShowSettingsWindow);
    }

    private void OnZoomInPressed()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_overlayWindow != null && IsOverlayVisible)
                _overlayWindow.ZoomIn();
        });
    }

    private void OnZoomOutPressed()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_overlayWindow != null && IsOverlayVisible)
                _overlayWindow.ZoomOut();
        });
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var json = await _userDataDb.GetSettingAsync(SettingsKey);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var loaded = JsonSerializer.Deserialize<OverlayMiniMapSettings>(json);
            if (loaded != null)
                _settings = loaded;
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to load overlay settings: {ex.Message}");
            _settings = new OverlayMiniMapSettings();
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings);
            await _userDataDb.SetSettingAsync(SettingsKey, json);
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to save overlay settings: {ex.Message}");
        }
    }

    public void SaveSettings()
    {
        _ = SaveSettingsAsync();
    }

    public void ResetSettings()
    {
        _settings.ResetToDefaults();

        var window = _overlayWindow;
        DetachOverlayWindow();
        window?.Close();

        _ = SaveSettingsAsync();
        OverlayVisibilityChanged?.Invoke(false);
    }

    public void RefreshMap()
    {
        _overlayWindow?.RefreshMap();
    }

    public void ShowSettingsWindow()
    {
        if (_disposed)
            return;

        if (_settingsWindow == null || !_settingsWindow.IsVisible)
        {
            _settingsWindow = new OverlaySettingsWindow(_settings, _overlayWindow);
            _settingsWindow.SettingsApplied += OnSettingsApplied;
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            return;
        }

        _settingsWindow.Activate();
    }

    private void OnSettingsApplied(OverlayMiniMapSettings settings)
    {
        _settings.CopyFrom(settings);
        SyncHotkeys();
        _ = SaveSettingsAsync();
        SettingsChanged?.Invoke(_settings);
    }

    private void SyncHotkeys()
    {
        GlobalKeyboardHookService.Instance.ZoomInKey = _settings.ZoomInKey;
        GlobalKeyboardHookService.Instance.ZoomOutKey = _settings.ZoomOutKey;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        UnsubscribeHotkeys();

        if (_settingsWindow != null)
        {
            _settingsWindow.Close();
            _settingsWindow = null;
        }

        var window = _overlayWindow;
        DetachOverlayWindow();
        window?.Close();

        _isInitialized = false;
        _initializeGate.Dispose();
        _log.Info("OverlayMiniMapService disposed");
        GC.SuppressFinalize(this);
    }
}
