// 专注期间的 ESC 拦截 —— 不用全局键盘钩子，改成让游戏的 Input 直接读不到 ESC。
//
// 为什么换成这个做法：
//   以前用过 SetWindowsHookEx（WH_KEYBOARD_LL）拦 Ctrl+Shift+Esc，那是全局钩子，
//   每一次按键都要经过我们的托管代码，结果整个键盘都变粘（用户实测）。
//   Harmony 是在游戏进程内部改方法，游戏调 Input.GetKeyDown(Escape) 时我们直接
//   把结果改成 false —— 按键根本没进过游戏，也没有任何全局副作用。
//
// 注意：这只能挡住**游戏自己**对 ESC 的读取（游戏内的设置 / 结束通话菜单）。
// Ctrl+Shift+Esc＝任务管理器的热键是 Windows 自己（登录会话）处理的，不会经过游戏进程，
// 所以任何进程内的补丁都拦不到它 —— 那件事仍然由 WindowGuard 的"检测到任务管理器就最小化"
// 那套来兜（顺带说一句台词）。
//
// 放在 .UI 命名空间下，才能用 Plugin.PatchMethod("类名", "方法名") 注册。
using HarmonyLib;
using UnityEngine;

namespace ChillFocusWhitelist.UI;

internal static class EscInputPatch
{
    /// <summary>
    /// 专注期间（且开了"专注时禁止关闭游戏"）让 ESC 读不到：
    /// 结果直接给 false，并跳过原方法。
    /// </summary>
    [HarmonyPrefix]
    private static bool Prefix(KeyCode key, ref bool __result)
    {
        if (key != KeyCode.Escape)
            return true;

        var plugin = Plugin.Instance;
        if (plugin == null || !plugin.ShouldBlockGameExit())
            return true;

        __result = false;
        return false;
    }
}
