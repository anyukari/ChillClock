using System;
using System.Collections;
using System.Reflection;
using ChillFocusWhitelist.Core;
using UnityEngine;

namespace ChillFocusWhitelist.UI;

/// <summary>
/// 用游戏原生的字幕框显示台词，不自己画 UI。
///
/// 聪音自言自语 / 闲聊走的就是这条链路：
///   Bulbul.StorySystemUI.ActivateNormalText()            // 淡入底部字幕框
///   Bulbul.StorySystemUI._normalTextMessage.StartText(s) // ScenarioTextMessage
///                                                        // -> TMP + Febucci 打字机
///   Bulbul.StorySystemUI.DeactivateNormalText(null)      // 淡出
/// StorySystemUI 的两个 ScenarioTextMessage 中：
///   _mainStoryTextMessage = 剧情对话（大框，MessageType.MainStory）
///   _normalTextMessage    = 普通字幕（底部小框，MessageType.Normal）
/// 这里固定用后者，和游戏自己的闲聊字幕是同一个组件、同一种字体和打字机效果。
/// </summary>
internal sealed class GameSubtitle : MonoBehaviour
{
    private const string StoryUiTypeName = "Bulbul.StorySystemUI";
    private const string StoryUiInterfaceName = "Bulbul.IStorySystemUI";
    private const float LookupRetrySeconds = 5f;

    private object _ui;
    private MethodInfo _activate;
    private MethodInfo _deactivate;
    private FieldInfo _messageField;
    private MethodInfo _startText;
    private MethodInfo _isActiveNormalText;
    private Coroutine _routine;
    private float _nextLookup;
    private bool _warned;

    public void Show(string text, float duration)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // 游戏自己在演出时不要抢字幕框（它可能正在显示自己的台词）
        if (HeroineActionBridge.IsGameSequenceBusy())
            return;

        if (!EnsureUi())
            return;

        // 游戏正在用这个字幕框时不要抢：抢过来会把它的 _isActiveNormalText 状态搅乱，
        // 而游戏的剧情脚本是 "if (!IsActiveNormalText()) ActivateNormalText();"，
        // 状态一乱它就不会再激活自己的字幕框了。
        //
        // 但如果是**我们自己**正开着（连播的上一句），那必须能接手：
        // 否则联动台词从第二句起就没有字幕。_routine != null 就是我们自己在显示的标志。
        var ours = _routine != null;
        if (!ours && IsGameUsingSubtitle())
            return;

        try
        {
            _activate.Invoke(_ui, null);

            var message = _messageField.GetValue(_ui);
            if (message == null)
                return;

            _startText.Invoke(message, new object[] { text });

            if (_routine != null)
                StopCoroutine(_routine);

            // 游戏的字幕是逐字打出来的（Febucci 打字机，速度跟随游戏文本速度设置）。
            // 之前只按 0.06 秒/字留时间，短语音（2 秒左右）会"字幕一闪就没了"，
            // 长句子也常常打不完。现在：语音时长 + 1 秒收尾，并且按 0.16 秒/字兜底。
            _routine = StartCoroutine(HideAfter(DisplaySeconds(text, duration)));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] native subtitle failed: " + e.Message);
            Reset();
        }
    }

    /// <summary>
    /// 这条字幕应该停留多久。Show() 和语音连播的句间停顿都用它，
    /// 这样下一句开口之前，上一句的字幕一定读得完（联动台词里短句最容易"一闪而过"）。
    /// </summary>
    public static float DisplaySeconds(string text, float duration)
    {
        if (string.IsNullOrEmpty(text))
            return 2.5f;

        return Mathf.Clamp(Mathf.Max(duration + 1f, text.Length * 0.16f), 2.5f, 20f);
    }

    private IEnumerator HideAfter(float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        _routine = null;
        Hide();
    }

    private void Hide()
    {
        if (_ui == null || _deactivate == null)
            return;

        // 必须无条件收尾：Show 时我们调了 ActivateNormalText（把 _isActiveNormalText 置 true），
        // 游戏自己的剧情脚本是 "if (!IsActiveNormalText()) ActivateNormalText();"，
        // 这里不收尾的话那个标记会一直留在 true，游戏之后就不再激活自己的字幕框，剧情状态错位。
        //
        // 也不能因为"游戏正在说话"就跳过收尾（之前那样试过）：那样标记会卡在 true，
        // 而我们自己的 Show 又有一条"不是自己在显示就别抢"的保护 ——
        // 结果就是后面每一句都没有字幕（实测过）。
        try
        {
            // onEndAction 传 null：游戏那边会自己判空
            _deactivate.Invoke(_ui, new object[] { null });
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] native subtitle hide failed: " + e.Message);
            Reset();
        }
    }

    private bool EnsureUi()
    {
        if (_ui != null && _activate != null && _deactivate != null &&
            _messageField != null && _startText != null)
            return true;

        if (Time.unscaledTime < _nextLookup)
            return false;
        _nextLookup = Time.unscaledTime + LookupRetrySeconds;

        try
        {
            var type = FindStoryUiType();
            if (type == null)
            {
                WarnOnce("StorySystemUI 未找到，字幕将不显示");
                return false;
            }

            var instance = FindInstance(type);
            if (instance == null)
            {
                WarnOnce("StorySystemUI 实例未找到，字幕将不显示");
                return false;
            }

            var activate = type.GetMethod(
                "ActivateNormalText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var deactivate = type.GetMethod(
                "DeactivateNormalText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var field = type.GetField(
                "_normalTextMessage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var startText = field?.FieldType.GetMethod(
                "StartText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var isActive = type.GetMethod(
                "IsActiveNormalText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (activate == null || deactivate == null || field == null || startText == null)
            {
                WarnOnce("StorySystemUI 接口不匹配，字幕将不显示");
                return false;
            }

            _ui = instance;
            _activate = activate;
            _deactivate = deactivate;
            _messageField = field;
            _startText = startText;
            _isActiveNormalText = isActive;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] subtitle ui lookup failed: " + e.Message);
            Reset();
            return false;
        }
    }

    private bool IsGameUsingSubtitle()
    {
        if (_ui == null || _isActiveNormalText == null)
            return false;

        try
        {
            return _isActiveNormalText.Invoke(_ui, null) is bool active && active;
        }
        catch
        {
            return false;
        }
    }

    private static Type FindStoryUiType()
    {
        var type = FindType(StoryUiTypeName);
        if (type != null)
            return type;

        // 备选：桌面版实现不在时，找任何实现了 IStorySystemUI 的组件
        try
        {
            foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (behaviour == null)
                    continue;

                var candidate = behaviour.GetType();
                if (Implements(candidate, StoryUiInterfaceName))
                    return candidate;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>
    /// StorySystemUI 挂在场景里、平时可能是 SetActive(false)，
    /// FindObjectOfType 只会找激活对象，所以这里用 FindObjectsOfTypeAll 再过滤场景实例。
    /// </summary>
    private static object FindInstance(Type type)
    {
        var all = Resources.FindObjectsOfTypeAll(type);
        if (all != null)
        {
            foreach (var obj in all)
            {
                if (obj is Component component && component != null && component.gameObject.scene.IsValid())
                    return component;
            }
        }

        return null;
    }

    private static bool Implements(Type type, string interfaceFullName)
    {
        foreach (var contract in type.GetInterfaces())
        {
            if (string.Equals(contract.FullName, interfaceFullName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static Type FindType(string typeFullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(typeFullName, false);
                if (type != null)
                    return type;
            }
            catch
            {
                // 动态程序集可能不允许查询
            }
        }

        return null;
    }

    private void WarnOnce(string message)
    {
        if (_warned)
            return;
        _warned = true;
        Plugin.Log.LogWarning("[Chill Clock] " + message);
    }

    private void Reset()
    {
        _ui = null;
        _activate = null;
        _deactivate = null;
        _messageField = null;
        _startText = null;
        _isActiveNormalText = null;
    }
}
