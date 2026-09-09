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
                Plugin.Instance?.SetFocusFromEvent(false);
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
