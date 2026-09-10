using System;
using System.Collections.Generic;

namespace ChillFocusWhitelist.Core;

public sealed class WindowGuard
{
    private readonly WhitelistStore _store;
    private readonly HashSet<IntPtr> _minimizedByUs = new HashSet<IntPtr>();
    private readonly object _lock = new object();

    public WindowGuard(WhitelistStore store)
    {
        _store = store;
    }

    public bool FocusActive { get; private set; }

    public void SetFocusActive(bool active)
    {
        lock (_lock)
        {
            if (active == FocusActive)
                return;

            FocusActive = active;
            if (active)
            {
                Plugin.Log.LogInfo("[Chill Clock] focus minimize started");
                SweepMinimizeLocked();
            }
            else
            {
                EndFocusLocked();
            }
        }
    }

    public void Tick()
    {
        lock (_lock)
        {
            if (!FocusActive)
                return;

            SweepMinimizeLocked();
            PruneDeadHandlesLocked();
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
            EndFocusLocked();
    }

    private void SweepMinimizeLocked()
    {
        var windows = Win32.EnumerateVisibleTopLevelWindows();
        foreach (var window in windows)
        {
            if (window.IsShellWindow)
                continue;
            if (window.ProcessId == (uint)Win32.CurrentProcessId)
                continue;
            if (string.IsNullOrEmpty(window.ProcessPath) && !window.IsTaskManager)
                continue;
            if (Win32.IsMinimized(window.Handle))
                continue;
            if (_store.IsAllowed(window.ProcessPath, window.ProcessName))
                continue;

            if (window.IsTaskManager)
                Win32.RequestClose(window.Handle);

            if (Win32.HideWindow(window.Handle))
            {
                _minimizedByUs.Add(window.Handle);
                Plugin.Log.LogInfo("[Chill Clock] minimized window: " + window.ProcessName);
            }
        }
    }

    private void EndFocusLocked()
    {
        // 只解除管制：保留当前最小化状态，不自动把所有窗口弹回。
        _minimizedByUs.Clear();
        FocusActive = false;
        Plugin.Log.LogInfo("[Chill Clock] focus minimize ended (windows stay minimized)");
    }

    private void PruneDeadHandlesLocked()
    {
        _minimizedByUs.RemoveWhere(handle => !Win32.IsWindowAlive(handle));
    }
}
