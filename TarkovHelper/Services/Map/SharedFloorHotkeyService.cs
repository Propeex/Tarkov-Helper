using System.Diagnostics;
using System.Runtime.InteropServices;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services.Map;

/// <summary>
/// 미니맵 표시 여부와 무관하게 지도 층 이동 단축키를 전달합니다.
/// 기존 키보드 훅과 동일하게 입력을 소비하지 않고 관찰만 합니다.
/// </summary>
public sealed class SharedFloorHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private static readonly ILogger Log = Logging.Log.For<SharedFloorHotkeyService>();
    private static readonly Lazy<SharedFloorHotkeyService> LazyInstance =
        new(() => new SharedFloorHotkeyService());

    private readonly LowLevelKeyboardProc _callback;
    private readonly HashSet<int> _pressedKeys = new();
    private IntPtr _hookId;
    private int _subscriberCount;
    private bool _disposed;

    public static SharedFloorHotkeyService Instance => LazyInstance.Value;

    public event Action? FloorUpPressed;
    public event Action? FloorDownPressed;
    public event Action? ResumeAutomaticPressed;

    private SharedFloorHotkeyService()
    {
        _callback = HookCallback;
    }

    public void Acquire()
    {
        if (_disposed)
            return;

        _subscriberCount++;
        if (_subscriberCount == 1)
            StartHook();
    }

    public void Release()
    {
        if (_subscriberCount > 0)
            _subscriberCount--;
        if (_subscriberCount == 0)
            StopHook();
    }

    private void StartHook()
    {
        if (_hookId != IntPtr.Zero)
            return;

        _hookId = SetWindowsHookEx(WhKeyboardLl, _callback, IntPtr.Zero, 0);
        if (_hookId == IntPtr.Zero)
            Log.Warning($"Failed to install shared floor hotkey hook: {Marshal.GetLastWin32Error()}");
    }

    private void StopHook()
    {
        _pressedKeys.Clear();
        if (_hookId == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var message = wParam.ToInt32();
        var virtualKey = Marshal.ReadInt32(lParam);
        if (message is WmKeyUp or WmSysKeyUp)
        {
            _pressedKeys.Remove(virtualKey);
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        if (message is not WmKeyDown and not WmSysKeyDown)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        if (!_pressedKeys.Add(virtualKey) ||
            GlobalKeyboardHookService.Instance.OverlayHotkeysSuppressed ||
            !IsTarkovOrHelperForeground())
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        var settings = OverlayMiniMapService.Instance.Settings;
        Action? action = null;
        if (virtualKey != 0 && virtualKey == settings.FloorUpKey)
            action = FloorUpPressed;
        else if (virtualKey != 0 && virtualKey == settings.FloorDownKey)
            action = FloorDownPressed;
        else if (virtualKey != 0 && virtualKey == settings.ResumeAutoFloorKey)
            action = ResumeAutomaticPressed;

        if (action != null)
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsTarkovOrHelperForeground()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return false;

            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
                return false;

            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            return name.Contains("Tarkov", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("EFT", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("TarkovHelper", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("TarkovHelper_JH", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopHook();
        GC.SuppressFinalize(this);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
