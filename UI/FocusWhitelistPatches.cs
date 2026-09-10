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
/// 只读诊断（服务层）：不管游戏从哪条剧情指令下发，最后都会落到
/// HeroineService 这几个方法上：
///   ChangeHeroineAnimationForInteger 身体动作（-1 = 不改，保持手头的活）
///   ChangeHeroineFacialAnimation     表情
///   ChangeLookScaleAnimation         转头（scale / speed / ease）
///   ChangeHeroineAnimationImmediately 直接点名播某个动作
/// 把它们的参数记下来，就是游戏自己的那套值，我们照着对齐。
///
/// 我们自己在调的时候会置 Ours，免得把自己的调用也记进去。只记日志，不改行为。
/// </summary>
internal static class HeroineMotionProbePatch
{
    /// <summary>我们自己在调这些方法时置位。</summary>
    internal static bool Ours;

    private static void Body(object[] __args) => Log("body", __args);
    private static void Facial(object[] __args) => Log("facial", __args);
    private static void Look(object[] __args) => Log("look(scale/speed/ease)", __args);
    private static void Immediate(object[] __args) => Log("immediate", __args);

    private static void Log(string what, object[] args)
    {
        if (Ours)
            return;

        try
        {
            var text = args == null || args.Length == 0
                ? "?"
                : string.Join("/", System.Array.ConvertAll(args, a => a?.ToString() ?? "null"));
            Plugin.Log.LogInfo("[Chill Clock] game motion: " + what + " = " + text);
        }
        catch
        {
            // 诊断用，出错就算了
        }
    }
}

/// <summary>
/// 只读诊断：游戏自己在播剧情 / 点击反应时，会通过
/// ScenarioReader.CommandChangeMotion 一次下发三件事 ——
/// 身体动作(-1 = 不改)、表情、转头幅度。把这三个值记下来，
/// 我们就能核对"我们自己下的动作，和游戏自己下的到底是不是一回事"。
///
/// 只记日志，不改任何行为。
/// </summary>
internal static class ScenarioMotionProbePatch
{
    private static System.Reflection.FieldInfo _body;
    private static System.Reflection.FieldInfo _facial;
    private static System.Reflection.FieldInfo _look;
    private static System.Reflection.FieldInfo _lookSeconds;
    private static System.Reflection.FieldInfo _lookEase;
    private static bool _resolved;

    private static void Postfix(object __0)
    {
        try
        {
            if (__0 == null)
                return;

            if (!_resolved)
            {
                _resolved = true;
                var type = __0.GetType();
                _body = AccessTools.Field(type, "BodyMotion");
                _facial = AccessTools.Field(type, "FacialMotion");
                _look = AccessTools.Field(type, "LookScale");
                _lookSeconds = AccessTools.Field(type, "LookSpeedSeconds");
                _lookEase = AccessTools.Field(type, "LookEaseType");
            }

            Plugin.Log.LogInfo("[Chill Clock] game motion: body=" + Value(_body, __0) +
                               " facial=" + Value(_facial, __0) +
                               " look=" + Value(_look, __0) +
                               " lookSec=" + Value(_lookSeconds, __0) +
                               " ease=" + Value(_lookEase, __0));
        }
        catch
        {
            // 诊断用，出错就算了
        }
    }

    private static string Value(System.Reflection.FieldInfo field, object target)
    {
        try
        {
            return field == null ? "?" : (field.GetValue(target)?.ToString() ?? "null");
        }
        catch
        {
            return "?";
        }
    }
}

/// <summary>
/// 点击聪音的反应。默认不接管（走游戏原逻辑）；打开 ClickReaction 后，
/// 满足条件时用我们池子里的台词回应，并跳过游戏原本的反应。
///
/// 返回 true 表示已接管：把 __result 置 true 让点击流程以为"已响应"
/// （否则它会走"现在点不动"的反馈），同时 return false 跳过原方法，
/// 这样游戏自己的反应状态机不会被启动。
/// </summary>
internal static class ClickReactionPatch
{
    private static bool Prefix(
        Bulbul.FacilityClickHeroine.ReactionType reactionType,
        ref bool __result)
    {
        if (!Plugin.Instance.TryTakeOverClickReaction(reactionType))
            return true;

        __result = true;
        return false;
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
