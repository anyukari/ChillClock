using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ChillFocusWhitelist.Core;

internal static class Win32
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int DwmwaCloaked = 14;

    private const int GwlExstyle = -20;
    private const long WsExToolwindow = 0x00000080;
    private const long WsExTopmost = 0x00000008;
    private const uint GwOwner = 4;

    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoActivate = 0x0010;

    private const int SwHide = 0;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const int SwShowNa = 8;
    private const int SwShow = 5;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private static IntPtr _cachedMainWindow;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder exeName,
        ref uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    private sealed class HandleCollector
    {
        public readonly List<IntPtr> Handles = new List<IntPtr>();

        public bool OnWindow(IntPtr hWnd, IntPtr lParam)
        {
            if (IsWindowVisible(hWnd))
                Handles.Add(hWnd);
            return true;
        }
    }

    public sealed class WindowInfo
    {
        public IntPtr Handle;
        public uint ProcessId;
        public string ProcessPath;
        public string ProcessName;
        public string ClassName;
        public string Title;
        public bool IsVisible;
        public bool IsShellWindow;
        public bool IsStartMenuCandidate;
        public bool IsTaskManager;
    }

    public static int CurrentProcessId { get; } = Process.GetCurrentProcess().Id;

    public static bool HideWindow(IntPtr hWnd)
    {
        return ShowWindow(hWnd, SwMinimize);
    }

    public static bool RestoreWindow(IntPtr hWnd)
    {
        return ShowWindow(hWnd, SwRestore);
    }

    public static void RequestClose(IntPtr hWnd)
    {
        PostMessage(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool ForceShowWindow(IntPtr hWnd)
    {
        return ShowWindow(hWnd, SwShow);
    }

    public static bool IsMinimized(IntPtr hWnd)
    {
        return IsIconic(hWnd);
    }

    public static bool IsWindowAlive(IntPtr hWnd)
    {
        return IsWindow(hWnd);
    }

    public static string GetProcessPath(uint pid)
    {
        return TryGetProcessPath(pid);
    }

    public static IntPtr GetCurrentProcessMainWindow()
    {
        if (_cachedMainWindow != IntPtr.Zero)
            return _cachedMainWindow;

        try
        {
            _cachedMainWindow = Process.GetCurrentProcess().MainWindowHandle;
        }
        catch
        {
            _cachedMainWindow = IntPtr.Zero;
        }

        if (_cachedMainWindow == IntPtr.Zero)
        {
            var collector = new HandleCollector();
            EnumWindows(collector.OnWindow, IntPtr.Zero);
            long bestArea = -1;
            foreach (var handle in collector.Handles)
            {
                GetWindowThreadProcessId(handle, out var pid);
                if (pid != (uint)CurrentProcessId)
                    continue;
                if (GetWindowRect(handle, out var rect))
                {
                    var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        _cachedMainWindow = handle;
                    }
                }
            }
        }

        if (_cachedMainWindow == IntPtr.Zero)
            _cachedMainWindow = FindWindow("UnityWndClass", null);

        return _cachedMainWindow;
    }

    public static void ResetCachedMainWindow()
    {
        _cachedMainWindow = IntPtr.Zero;
    }

    public static bool IsTopmost(IntPtr hWnd)
    {
        var style = GetWindowLong(hWnd, GwlExstyle);
        return ((long)style & WsExTopmost) != 0;
    }

    public static void SetWindowTopmost(IntPtr hWnd, bool topmost)
    {
        var insertAfter = topmost ? new IntPtr(-1) : new IntPtr(-2);
        SetWindowPos(hWnd, insertAfter, 0, 0, 0, 0,
            (uint)(SwpNoMove | SwpNoSize | SwpNoActivate));
    }

    public static List<WindowInfo> EnumerateVisibleTopLevelWindows()
    {
        return EnumerateWindows(true, false);
    }

    public static List<WindowInfo> EnumerateAllTopLevelWindows()
    {
        return EnumerateWindows(false, false);
    }

    public static List<WindowInfo> EnumerateAllTopLevelWindowsForPicker()
    {
        return EnumerateWindows(false, true);
    }

    public static List<IntPtr> EnumerateCurrentProcessVisibleWindowHandles()
    {
        var collector = new HandleCollector();
        EnumWindows(collector.OnWindow, IntPtr.Zero);

        var result = new List<IntPtr>(collector.Handles.Count);
        foreach (var handle in collector.Handles)
        {
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == (uint)CurrentProcessId && IsWindowVisible(handle))
                result.Add(handle);
        }

        return result;
    }

    private static List<WindowInfo> EnumerateWindows(bool onlyVisible, bool includeToolWindows)
    {
        var collector = new HandleCollector();
        EnumWindows(collector.OnWindow, IntPtr.Zero);

        var result = new List<WindowInfo>(collector.Handles.Count);
        foreach (var handle in collector.Handles)
        {
            if (onlyVisible && !IsWindowVisible(handle))
                continue;

            var info = BuildWindowInfo(handle, onlyVisible, includeToolWindows);
            if (info != null)
                result.Add(info);
        }

        return result;
    }

    private static WindowInfo BuildWindowInfo(IntPtr hWnd, bool requireVisibleRect, bool includeToolWindows)
    {
        var className = GetText(hWnd, GetClassName);
        if (IsCloaked(hWnd))
            return null;
        if (!includeToolWindows && IsToolWindow(hWnd))
            return null;

        GetWindowRect(hWnd, out var rect);
        if (requireVisibleRect)
        {
            if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
                return null;
        }

        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0 || pid == (uint)CurrentProcessId)
            return null;

        var title = GetText(hWnd, GetWindowText);
        var path = TryGetProcessPath(pid);
        var processName = string.IsNullOrEmpty(path)
            ? TryGetProcessNameByPid(pid)
            : System.IO.Path.GetFileName(path);
        var isTaskManager = IsTaskManagerWindow(className, title, processName);
        var info = new WindowInfo
        {
            Handle = hWnd,
            ProcessId = pid,
            ClassName = className,
            Title = title,
            IsVisible = IsWindowVisible(hWnd),
            IsShellWindow = IsShellClass(className),
            IsStartMenuCandidate = IsStartMenuClass(className, pid),
            IsTaskManager = isTaskManager
        };

        if (path == null && processName == null &&
            !info.IsShellWindow && !info.IsStartMenuCandidate && !isTaskManager)
            return null;

        info.ProcessPath = path;
        info.ProcessName = processName;
        return info;
    }

    private static string GetText(IntPtr hWnd, Func<IntPtr, StringBuilder, int, int> getter)
    {
        var sb = new StringBuilder(512);
        getter(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool IsToolWindow(IntPtr hWnd)
    {
        var style = GetWindowLong(hWnd, GwlExstyle);
        return ((long)style & WsExToolwindow) != 0;
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        var result = DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(int));
        return result == 0 && cloaked != 0;
    }

    private static bool IsShellClass(string className)
    {
        switch (className)
        {
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
            case "Progman":
            case "WorkerW":
            case "tooltips_class32":
                return true;
            default:
                return false;
        }
    }

    private static bool IsStartMenuClass(string className, uint processId)
    {
        if (className == "Windows.UI.Core.CoreWindow" || className == "ImmersiveLauncher")
        {
            // explorer.exe 拥有开始菜单/任务视图等系统壳窗口。
            var name = TryGetProcessName(processId);
            return string.Equals(name, "explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "startmenuexperiencehost.exe", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string TryGetProcessPath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var capacity = 1024u;
            var sb = new StringBuilder((int)capacity);
            return QueryFullProcessImageName(handle, 0, sb, ref capacity) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string TryGetProcessName(uint pid)
    {
        var path = TryGetProcessPath(pid);
        return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path);
    }

    private static string TryGetProcessNameByPid(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid).ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTaskManagerWindow(string className, string title, string processName)
    {
        if (!string.IsNullOrEmpty(processName) &&
            string.Equals(processName, "taskmgr.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(className, "TaskManagerWindow", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrEmpty(title))
            return false;
        return title.IndexOf("任务管理器", StringComparison.OrdinalIgnoreCase) >= 0 ||
               title.IndexOf("Task Manager", StringComparison.OrdinalIgnoreCase) >= 0 ||
               title.IndexOf("タスク マネージャー", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
