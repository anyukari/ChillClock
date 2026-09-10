using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChillFocusWhitelist.Core;

/// <summary>
/// 全屏且游戏窗口在前台时吞掉 ESC，阻止游戏借此退出全屏。
/// </summary>
internal sealed class EscKeyGuard
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int VkEscape = 0x1B;

    private HookProc _hookProc;
    private IntPtr _hookId = IntPtr.Zero;

    public void EnsureInstalled()
    {
        if (_hookId != IntPtr.Zero)
            return;

        try
        {
            _hookProc = HookCallback;
            var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
            var module = moduleName == null
                ? IntPtr.Zero
                : GetModuleHandle(moduleName);

            _hookId = SetWindowsHookEx(WhKeyboardLl, _hookProc, module, 0);
            if (_hookId == IntPtr.Zero)
            {
                _hookProc = null;
                Plugin.Log.LogWarning("[Chill Clock] ESC keyboard hook failed, error=" +
                                      Marshal.GetLastWin32Error());
            }
        }
        catch (Exception e)
        {
            _hookProc = null;
            Plugin.Log.LogWarning("[Chill Clock] ESC keyboard hook exception: " + e.Message);
        }
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _hookProc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var msg = wParam.ToInt32();
                if (msg == WmKeydown || msg == WmSyskeydown)
                {
                    var data = (KbdLlHookStruct)Marshal.PtrToStructure(lParam, typeof(KbdLlHookStruct));
                    if (data.VirtualKeyCode == VkEscape &&
                        UnityEngine.Screen.fullScreen &&
                        IsGameForeground())
                    {
                        return new IntPtr(1);
                    }

                }
            }
        }
        catch
        {
            // 出错时放行，避免把键盘锁死。
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsGameForeground()
    {
        var gameWindow = Win32.GetCurrentProcessMainWindow();
        return gameWindow != IntPtr.Zero && GetForegroundWindow() == gameWindow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        HookProc proc,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookId,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}
