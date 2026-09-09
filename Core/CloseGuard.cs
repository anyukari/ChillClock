using System;
using System.Runtime.InteropServices;

namespace ChillFocusWhitelist.Core;

/// <summary>
/// 通过子类化游戏主窗口，在专注期间拦截 WM_CLOSE（右上角 X、任务栏关闭、Alt+F4）。
/// </summary>
internal sealed class CloseGuard
{
    private const int GwlWndProc = -4;
    private const uint WmClose = 0x0010;

    private readonly Func<bool> _shouldBlock;

    private IntPtr _hwnd;
    private IntPtr _previousProc;
    private WindowProcDelegate _procDelegate;

    public CloseGuard(Func<bool> shouldBlock)
    {
        _shouldBlock = shouldBlock;
    }

    public void EnsureInstalled()
    {
        try
        {
            var hwnd = Win32.GetCurrentProcessMainWindow();
            if (hwnd == IntPtr.Zero)
                return;
            if (hwnd == _hwnd && _previousProc != IntPtr.Zero)
                return;

            Uninstall();
            Install(hwnd);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] close guard install failed: " + e.Message);
        }
    }

    public void Uninstall()
    {
        if (_hwnd != IntPtr.Zero && _previousProc != IntPtr.Zero)
        {
            try
            {
                SetWindowLongPtr(_hwnd, GwlWndProc, _previousProc);
            }
            catch
            {
                // 窗口可能已销毁，忽略。
            }
        }

        _hwnd = IntPtr.Zero;
        _previousProc = IntPtr.Zero;
        _procDelegate = null;
    }

    private void Install(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _procDelegate = WndProc;
        var result = SetWindowLongPtr(hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_procDelegate));
        if (result == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                _hwnd = IntPtr.Zero;
                _previousProc = IntPtr.Zero;
                _procDelegate = null;
                Plugin.Log.LogWarning("[Chill Clock] close guard SetWindowLongPtr failed, error=" + error);
                return;
            }
        }

        _previousProc = result;
        Plugin.Log.LogInfo("[Chill Clock] close guard installed on " + hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WmClose)
            {
                var block = _shouldBlock != null && _shouldBlock();
                if (block)
                {
                    Plugin.Log.LogWarning("[Chill Clock] 专注中已拦截 WM_CLOSE 关闭请求");
                    return IntPtr.Zero;
                }

                // 非专注时允许关闭：先摘掉子类再转发，避免干扰 Unity 自己的退出流程。
                return PassCloseToOriginal(hwnd, wParam, lParam);
            }
        }
        catch
        {
            // 回调异常时放行，避免把游戏窗口卡死。
        }

        return _previousProc == IntPtr.Zero
            ? IntPtr.Zero
            : CallWindowProc(_previousProc, hwnd, msg, wParam, lParam);
    }

    private IntPtr PassCloseToOriginal(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        var previous = _previousProc;
        if (previous == IntPtr.Zero)
            return IntPtr.Zero;

        SetWindowLongPtr(hwnd, GwlWndProc, previous);
        _hwnd = IntPtr.Zero;
        _previousProc = IntPtr.Zero;

        var result = CallWindowProc(previous, hwnd, WmClose, wParam, lParam);
        _procDelegate = null;
        return result;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousProc,
        IntPtr hwnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hwnd, int index, IntPtr newValue);

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, newValue)
            : SetWindowLong32(hwnd, index, newValue);
    }
}
