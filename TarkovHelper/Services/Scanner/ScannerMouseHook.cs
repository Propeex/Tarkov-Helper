using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TarkovHelper.Services.Scanner;

/// <summary>
/// 전역 왼쪽 마우스 버튼 해제를 감지합니다. 입력은 절대 차단하지 않습니다.
/// </summary>
internal sealed class ScannerMouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLeftButtonUp = 0x0202;
    private const int LlmhfInjected = 0x00000001;

    private readonly LowLevelMouseProc _callback;
    private IntPtr _hook;
    private bool _disposed;

    public event Action<int, int>? LeftButtonReleased;

    public ScannerMouseHook()
    {
        _callback = HookCallback;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != IntPtr.Zero)
            return;

        _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "스캐너 마우스 훅을 설치하지 못했습니다.");
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() == WmLeftButtonUp)
        {
            var mouse = Marshal.PtrToStructure<MouseHookData>(data);
            if ((mouse.Flags & LlmhfInjected) == 0)
                LeftButtonReleased?.Invoke(mouse.Point.X, mouse.Point.Y);
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseHookData
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        LowLevelMouseProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr message,
        IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
