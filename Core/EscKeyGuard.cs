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

    /// <summary>
    /// 键盘钩子的回调委托。**一旦创建就永不释放**。
    ///
    /// 原因：SetWindowsHookEx 记的是这个委托的函数指针，只要钩子还挂着，
    /// 系统随时可能回调进来。以前 Uninstall() 里把它置成 null，万一 Unhook 失败
    /// （或者还有别的线程正在回调），指针指向的内存被回收后再被调用，
    /// 就是 0xc0000005 —— 退出时弹 Unity 崩溃框的典型成因。
    /// 让委托和进程同生共死，代价只有一个委托对象。
    /// </summary>
    private readonly HookProc _hookProc;
    private IntPtr _hookId = IntPtr.Zero;

    public EscKeyGuard()
    {
        _hookProc = HookCallback;
    }

    public void EnsureInstalled()
    {
        if (_hookId != IntPtr.Zero)
            return;

        try
        {
            var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
            var module = moduleName == null
                ? IntPtr.Zero
                : GetModuleHandle(moduleName);

            _hookId = SetWindowsHookEx(WhKeyboardLl, _hookProc, module, 0);
            if (_hookId == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("[Chill Clock] ESC keyboard hook failed, error=" +
                                      Marshal.GetLastWin32Error());
            }
        }
        catch (Exception e)
        {
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
