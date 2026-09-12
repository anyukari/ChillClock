using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ChillFocusWhitelist.Core;

/// <summary>
/// 给游戏进程的所有可见顶层窗口挂上关闭消息拦截，
/// 在专注/休息期间阻止右上角 X、任务栏关闭、Alt+F4 等正常退出。
/// </summary>
internal sealed class CloseGuard
{
    private const int GwlWndProc = -4;
    private const uint WmClose = 0x0010;
    private const uint WmSyscommand = 0x0112;
    private const int ScClose = 0xF060;

    private readonly Func<bool> _shouldBlock;
    private readonly Dictionary<IntPtr, IntPtr> _previousProcs = new Dictionary<IntPtr, IntPtr>();

    /// <summary>
    /// 窗口过程回调。**一旦创建就永不释放**。
    ///
    /// SetWindowLongPtr 记的是这个委托的函数指针，只要还有窗口没还原成功，
    /// Windows 之后仍可能回调进来。以前 Uninstall() 里把它置成 null ——
    /// 那些没还原成功的窗口就成了"指向已回收内存的过程"，退出时收到
    /// WM_DESTROY 之类的消息就是 0xc0000005。
    /// </summary>
    private readonly WindowProcDelegate _procDelegate;
    private int _nextEnumerateTime;

    public CloseGuard(Func<bool> shouldBlock)
    {
        _shouldBlock = shouldBlock;
        _procDelegate = WndProc;
    }

    public Action OnCloseBlocked { get; set; }

    public void EnsureInstalled()
    {
        var now = Environment.TickCount;
        if (now < _nextEnumerateTime)
            return;
        _nextEnumerateTime = now + 300;

        try
        {
            var handles = Win32.EnumerateCurrentProcessVisibleWindowHandles();
            if (handles.Count == 0)
            {
                var fallback = Win32.GetCurrentProcessMainWindow();
                if (fallback != IntPtr.Zero)
                    handles.Add(fallback);
            }

            var alive = new HashSet<IntPtr>();
            foreach (var hwnd in handles)
            {
                if (!Win32.IsWindowAlive(hwnd))
                    continue;
                alive.Add(hwnd);
                EnsureWindowInstalled(hwnd);
            }

            RemoveDeadAndMissing(alive);
        }
        catch
        {
            // 同步失败不阻塞主流程。
        }
    }

    public void Uninstall()
    {
        foreach (var hwnd in new List<IntPtr>(_previousProcs.Keys))
            RestoreWindow(hwnd, remove: true);
    }

    private void EnsureWindowInstalled(IntPtr hwnd)
    {
        if (_previousProcs.ContainsKey(hwnd))
        {
            var currentProc = GetWindowLongPtr(hwnd, GwlWndProc);
            var myProc = Marshal.GetFunctionPointerForDelegate(_procDelegate);
            if (currentProc == myProc)
                return;

            RestoreWindow(hwnd, remove: true);
        }

        var result = SetWindowLongPtr(
            hwnd,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_procDelegate));
        if (result == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 0)
                return;
        }

        _previousProcs[hwnd] = result;
    }

    private void RemoveDeadAndMissing(HashSet<IntPtr> alive)
    {
        foreach (var hwnd in new List<IntPtr>(_previousProcs.Keys))
        {
            if (!alive.Contains(hwnd))
                RestoreWindow(hwnd, remove: true);
        }
    }

    private void RestoreWindow(IntPtr hwnd, bool remove)
    {
        if (!_previousProcs.TryGetValue(hwnd, out var previous))
            return;

        try
        {
            var currentProc = GetWindowLongPtr(hwnd, GwlWndProc);
            var myProc = Marshal.GetFunctionPointerForDelegate(_procDelegate);
            if (currentProc == myProc)
                SetWindowLongPtr(hwnd, GwlWndProc, previous);
        }
        catch
        {
            // 窗口可能已销毁。
        }

        if (remove)
            _previousProcs.Remove(hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WmClose || (msg == WmSyscommand && (wParam.ToInt32() & 0xFFF0) == ScClose))
            {
                var block = _shouldBlock != null && _shouldBlock();
                if (block)
                {
                    OnCloseBlocked?.Invoke();
                    return IntPtr.Zero;
                }

                if (msg == WmClose)
                    return PassCloseToOriginal(hwnd, wParam, lParam);
            }
        }
        catch
        {
            // 回调异常时放行。
        }

        return _previousProcs.TryGetValue(hwnd, out var previous)
            ? CallWindowProc(previous, hwnd, msg, wParam, lParam)
            : IntPtr.Zero;
    }

    private IntPtr PassCloseToOriginal(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        if (!_previousProcs.TryGetValue(hwnd, out var previous))
            return IntPtr.Zero;

        RestoreWindow(hwnd, remove: true);
        return CallWindowProc(previous, hwnd, WmClose, wParam, lParam);
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLong32(IntPtr hwnd, int index);

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, newValue)
            : SetWindowLong32(hwnd, index, newValue);
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : GetWindowLong32(hwnd, index);
    }
}
