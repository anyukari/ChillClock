using Bulbul;
using HarmonyLib;

namespace ChillFocusWhitelist.UI;

[HarmonyPatch(typeof(SettingUI), "Setup")]
internal static class FocusWhitelistSetupPatch
{
    private static void Postfix(SettingUI __instance)
    {
        try
        {
            SettingsPageInjector.Active?.EnsureBuilt(__instance);
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogWarning("[FocusWhitelist] SettingUI.Setup postfix failed: " + e);
        }
    }
}

[HarmonyPatch(typeof(SettingUI), "Activate")]
internal static class FocusWhitelistActivatePatch
{
    private static void Postfix(SettingUI __instance)
    {
        try
        {
            SettingsPageInjector.Active?.OnActivated(__instance);
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogWarning("[FocusWhitelist] SettingUI.Activate postfix failed: " + e);
        }
    }
}

/// <summary>
/// 点击聪音的反应。默认不接管（走游戏原逻辑）；打开 ClickReaction 后，
/// 满足条件时用我们池子里的台词回应，并跳过游戏原本的反应。
///
/// 三种走法：
///   已接管   -> __result = true（点击流程认为"已响应"），跳过原方法
///   现在不能 -> __result = false（触发游戏自己的"现在不能反应"反馈，就是那个禁止光标），跳过原方法
///   不管     -> 走游戏原逻辑
/// </summary>
internal static class ClickReactionPatch
{
    private static bool Prefix(
        Bulbul.FacilityClickHeroine.ReactionType reactionType,
        ref bool __result)
    {
        switch (Plugin.Instance.HandleClickReaction(reactionType))
        {
            case Plugin.ClickReactionResult.TakeOver:
                __result = true;
                return false;

            case Plugin.ClickReactionResult.Blocked:
                // 她正在说话（我们这边的台词还没完），照游戏自己的规矩：现在点不动
                __result = false;
                return false;

            default:
                return true;
        }
    }
}

internal static class PomodoroStartPatch
{
    private static void Prefix(Bulbul.PomodoroService __instance)
    {
        Plugin.Instance?.AttachPomodoroService(__instance);
        Plugin.Instance?.SetPomodoroSessionActive(true);
        Plugin.Instance?.SetFocusFromEvent(true);
    }
}

internal static class PomodoroTimerEndPatch
{
    private static void Prefix(Bulbul.PomodoroService __instance)
    {
        if (__instance == null)
            return;
        Plugin.Instance?.AttachPomodoroService(__instance);
        try
        {
        var type = __instance.CurrentPomodoroType;
        if (type == Bulbul.PomodoroService.PomodoroType.Work)
        {
            // 到点了：游戏马上要让她自己说一句，先把我们这边正在说的那句掐掉，免得两边叠着
            Plugin.Instance?.AbortVoiceForGameLine();
            Plugin.Instance?.OnBreakStarted();
                Plugin.Instance?.SetFocusFromEvent(false);
            }
            else if (type == Bulbul.PomodoroService.PomodoroType.Break)
                Plugin.Instance?.SetFocusFromEvent(true);
        }
        catch
        {
        }
    }
}

internal static class PomodoroResetPatch
{
    private static void Prefix(Bulbul.PomodoroService __instance)
    {
        Plugin.Instance?.AttachPomodoroService(__instance);
        Plugin.Instance?.SetPomodoroSessionActive(false);
        Plugin.Instance?.SetFocusFromEvent(false);
    }
}

internal static class PomodoroCompletePatch
{
    private static void Postfix(Bulbul.PomodoroService __instance)
    {
        Plugin.Instance?.AttachPomodoroService(__instance);
        Plugin.Instance?.SetPomodoroSessionActive(false);
        Plugin.Instance?.SetFocusFromEvent(false);
    }
}

internal static class CountupStartPatch
{
    private static void Prefix()
    {
        Plugin.Instance?.SetFocusFromEvent(true);
    }
}

internal static class CountupTogglePatch
{
    private static void Prefix(Bulbul.CountupService __instance)
    {
        if (__instance == null)
            return;

        try
        {
            // 正计时暂停会切到 Break 状态；恢复则回到 Work。
            if (__instance.IsCurrentWorking())
                Plugin.Instance?.SetFocusFromEvent(false);
            else
                Plugin.Instance?.SetFocusFromEvent(true);
        }
        catch
        {
        }
    }
}

internal static class CountupResetPatch
{
    private static void Prefix()
    {
        Plugin.Instance?.SetFocusFromEvent(false);
    }
}

internal static class CountupCompletePatch
{
    private static void Postfix()
    {
        Plugin.Instance?.SetFocusFromEvent(false);
    }
}

internal sealed class FocusUiDriver : UnityEngine.MonoBehaviour
{
    private void Update()
    {
        SettingsPageInjector.Active?.Tick();
    }
}

internal sealed class FocusHostBehaviour : UnityEngine.MonoBehaviour
{
    private void Update()
    {
        Plugin.Instance?.TickHost();
    }
}
