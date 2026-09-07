using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ChillFocusWhitelist.Core;

namespace ChillFocusWhitelist.UI;

internal sealed class AppWindowCandidate
{
    public string Path;
    public string Name;
    public string Title;
}

internal static class WindowCandidates
{
    public static List<AppWindowCandidate> Enumerate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AppWindowCandidate>();

        var windows = Win32.EnumerateAllTopLevelWindowsForPicker();
        foreach (var window in windows.OrderBy(w => w.ProcessName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            if (window.IsShellWindow || window.IsStartMenuCandidate)
                continue;
            if (string.IsNullOrEmpty(window.ProcessPath))
                continue;
            if (string.IsNullOrWhiteSpace(window.ProcessName))
                continue;
            if (window.ProcessId == (uint)Win32.CurrentProcessId)
                continue;

            // 托盘/后台程序的主窗口可能被隐藏：仍要纳入候选，但优先保留有标题的窗口。
            if (string.IsNullOrWhiteSpace(window.Title) && !window.IsVisible)
                continue;

            if (!seen.Add(window.ProcessPath))
                continue;

            result.Add(new AppWindowCandidate
            {
                Path = window.ProcessPath,
                Name = window.ProcessName,
                Title = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title
            });
        }

        AddProcessFallbackCandidates(result, seen);

        return result;
    }

    private static void AddProcessFallbackCandidates(
        List<AppWindowCandidate> result,
        HashSet<string> seen)
    {
        var currentSession = Process.GetCurrentProcess().SessionId;
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "svchost", "system", "idle", "conhost", "runtimebroker", "dwm",
            "csrss", "lsass", "services", "winlogon", "smss", "wininit",
            "fontdrvhost", "textinputhost", "searchapp", "applicationframehost",
            "startmenuexperiencehost", "explorer", "powershell", "cmd",
            "codex", "node", "pwsh"
        };

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Win32.CurrentProcessId)
                    continue;
                if (process.SessionId != currentSession)
                    continue;

                var path = Win32.GetProcessPath((uint)process.Id);
                var name = process.ProcessName;
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
                    continue;
                if (excluded.Contains(name))
                    continue;
                if (!seen.Add(path))
                    continue;

                result.Add(new AppWindowCandidate
                {
                    Path = path,
                    Name = name + ".exe",
                    Title = string.IsNullOrEmpty(process.MainWindowTitle)
                        ? name + ".exe"
                        : process.MainWindowTitle
                });
            }
            catch
            {
                // 系统进程访问不到，直接跳过。
            }
        }
    }
}
