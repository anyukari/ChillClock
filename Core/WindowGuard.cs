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

    public Action<string, bool> OnWindowMinimized { get; set; }

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
                // 初始扫描也要发声：玩家在开始专注前就开着的应用被收走时，同样该提醒
                if (!HeroineActionBridge.IsGameSequenceBusy())
                    SweepMinimizeLocked(false, true);
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

            // 游戏自己在演出（开场 / 结束通话）时先不收窗口：
            // 收窗口会把前台焦点从游戏手里抢走，演出就断了。
            if (HeroineActionBridge.IsGameSequenceBusy())
                return;

            SweepMinimizeLocked(false, true);
            PruneDeadHandlesLocked();
        }
    }

    public void TickTaskManagerOnly()
    {
        lock (_lock)
        {
            if (HeroineActionBridge.IsGameSequenceBusy())
                return;

            SweepMinimizeLocked(true, true);
            PruneDeadHandlesLocked();
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
            EndFocusLocked();
    }

    private void SweepMinimizeLocked(bool taskManagerOnly, bool allowVoice)
    {
        var windows = Win32.EnumerateVisibleTopLevelWindows();
        var hidAny = false;
        foreach (var window in windows)
        {
            if (taskManagerOnly && !window.IsTaskManager)
                continue;
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
            {
                Win32.RequestClose(window.Handle);
                if (allowVoice)
                    OnWindowMinimized?.Invoke(window.ProcessName ?? "taskmgr.exe", true);
                continue;
            }

            if (Win32.HideWindow(window.Handle))
            {
                hidAny = true;
                _minimizedByUs.Add(window.Handle);
                if (allowVoice)
                    OnWindowMinimized?.Invoke(window.ProcessName, false);
                Plugin.Log.LogInfo("[Chill Clock] minimized window: " + window.ProcessName);
            }
        }

        // 最小化别的窗口会让 Windows 把焦点交给别人，这里把焦点还给游戏。
        if (hidAny)
            Win32.RestoreGameFocus();
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
