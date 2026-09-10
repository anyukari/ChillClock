using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ChillFocusWhitelist.Core;

/// <summary>
/// 通过游戏原生的接口让聪音配合台词做动作 / 表情 / 口型。
///
/// 关键坑：这里用到的类里，只有 <c>Bulbul.HeroineService</c> 是 MonoBehaviour，
/// 其余全是普通 C# 类（实例挂在 HeroineAI 的私有字段上）：
///     HeroineAI                    MonoBehaviour   ← 唯一的入口
///     HeroineAI._heroineService          Bulbul.HeroineService          MonoBehaviour
///     HeroineAI._heroineVoiceController  HeroineVoiceController         System.Object
///     HeroineAI._heroineFacialController HeroineFacialController        System.Object
///     HeroineAI._scenarioReader          Bulbul.ScenarioReader          System.Object
/// 所以必须从 HeroineAI 的字段里取，用 FindObjectsOfType 是找不到后三个的。
/// </summary>
internal static class HeroineActionBridge
{
    private const BindingFlags Instance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>总开关：关闭后完全不碰聪音的动作 / 表情 / 口型。</summary>
    public static bool Enabled = true;

    private static readonly System.Random Rng = new System.Random();

    private static readonly Dictionary<string, int[]> EmotionAnimations =
        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Happy"] = new[] { 1001, 1101, 1004 },
            ["Fun"] = new[] { 1003, 1103, 1202 },
            ["Sad"] = new[] { 1002, 1102, 1201 },
            ["Angry"] = new[] { 1302, 1002 },
            ["Agree"] = new[] { 1301 },
            ["Understand"] = new[] { 1301 },
            ["Confused"] = new[] { 1302, 1201 },
            ["Surprise"] = new[] { 1003, 1001 },
            ["Shy"] = new[] { 1001, 1004 },
            ["Think"] = new[] { 651, 652 },
            ["Curious"] = new[] { 651 },
            ["Relaxed"] = new[] { 256, 755, 304 },
            ["Tired"] = new[] { 1201, 1002 },
            ["Sleepy"] = new[] { 1201 },
            ["Working"] = new[] { 253, 255, 256, 305 },
            ["Excited"] = new[] { 1004, 1001 },
            ["Greeting"] = new[] { 1001, 1004 },
            ["Nervous"] = new[] { 1302, 1201 },
            ["Idle"] = new[] { 253, 255 }
        };

    /// <summary>情绪 -> FacialType（Animator 整数参数 "Facial"）。</summary>
    private static readonly Dictionary<string, int> EmotionFacials =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Happy"] = 5,
            ["Fun"] = 2,
            ["Sad"] = 4,
            ["Angry"] = 3,
            ["Surprise"] = 6,
            ["Shy"] = 1,
            ["Relaxed"] = 1,
            ["Tired"] = 1,
            ["Sleepy"] = 1,
            ["Working"] = 0,
            ["Confused"] = 6
        };

    private static MonoBehaviour _heroineAi;
    private static FieldInfo _fService;
    private static FieldInfo _fVoiceController;
    private static FieldInfo _fFacialController;
    private static FieldInfo _fScenarioReader;

    private static object _service;
    private static MethodInfo _changeAnimation;
    private static readonly Dictionary<string, MethodInfo> ServiceFlags = new Dictionary<string, MethodInfo>();

    private static object _voiceController;
    private static MethodInfo _changeTalkAnimation;
    private static PropertyInfo _isFinishedVoice;
    private static FieldInfo _isFinishedField;

    private static object _facialController;
    private static MethodInfo _changeFacial;

    private static object _scenarioReader;
    private static MethodInfo _isPlayingScenario;
    private static MethodInfo _isGameEndDirection;

    private static object _voiceManager;
    private static IDictionary _voiceClips;
    private static MethodInfo _voiceManagerPlay;

    /// <summary>
    /// 游戏是不是正在放它自己的演出。
    ///
    /// 这一点非常关键：HeroineService.ChangeHeroineAnimationForInteger 开头会调
    /// ResetAllTrigger()，一次性清掉动画器上 22 个 trigger。游戏自己也这么做，
    /// 但只在受控时机；我们要是在开场问候、结束通话告别这类演出里插一脚，
    /// 就会把游戏排好的动作抹掉。
    /// </summary>
    public static bool IsGameSequenceBusy()
    {
        if (IsScenarioPlaying())
            return true;
        if (IsGameEndDirection())
            return true;
        if (InvokeServiceFlag("IsLeaveChair"))
            return true;
        if (InvokeServiceFlag("IsPlayingPomodoroAction"))
            return true;
        if (InvokeServiceFlag("IsPlayingClickReactionAnimation"))
            return true;
        if (InvokeServiceFlag("IsSleeping"))
            return true;

        return false;
    }

    /// <summary>游戏自己的语音是否正在播放。</summary>
    public static bool IsGameVoiceBusy()
    {
        var controller = EnsureVoiceController();
        if (controller == null || _isFinishedVoice == null)
            return false;

        try
        {
            return _isFinishedVoice.GetValue(controller) is bool finished && !finished;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 游戏自己正在走"结束通话"演出（HeroineAI._isCurrentGameEndDirection）。
    /// 这段时间不要再拦游戏的退出/收尾，否则它自己的流程会被卡住。
    /// </summary>
    public static bool IsGameEndingCall()
    {
        return EnsureHeroineAi() && IsGameEndDirection();
    }

    /// <summary>播放一个情绪对应的身体动作，并同步一个安全的表情。</summary>
    public static void Play(string emotion)
    {
        if (!Enabled || IsGameSequenceBusy())
            return;

        if (EmotionAnimations.TryGetValue(emotion ?? "Idle", out var ids) &&
            ids != null &&
            ids.Length > 0 &&
            EnsureService() != null)
        {
            try
            {
                _changeAnimation.Invoke(_service, new object[] { ids[Rng.Next(ids.Length)] });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Chill Clock] heroine animation failed: " + e.Message);
                ResetService();
            }
        }

        if (emotion != null && EmotionFacials.TryGetValue(emotion, out var facial))
            ChangeFacial(facial);
    }

    /// <summary>设置游戏表情（Animator 整数参数 "Facial"）。</summary>
    public static void ChangeFacial(int facialType)
    {
        if (!Enabled || facialType < 0 || IsGameSequenceBusy())
            return;

        var controller = EnsureFacialController();
        if (controller == null || _changeFacial == null)
            return;

        try
        {
            _changeFacial.Invoke(controller, new object[] { facialType });
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] facial change failed: " + e.Message);
            ResetFacialController();
        }
    }

    /// <summary>开/关说话口型（Animator 布尔参数 "Enable_Talk"）。</summary>
    public static void SetMouthTalk(bool enabled)
    {
        if (!Enabled)
            return;

        // 关口型前先确认游戏自己没有在说话（游戏自己的 EndNoVoiceTalk 也是这么判的）
        if (!enabled && IsGameVoiceBusy())
            return;

        var controller = EnsureVoiceController();
        if (controller == null || _changeTalkAnimation == null)
            return;

        try
        {
            _changeTalkAnimation.Invoke(controller, new object[] { enabled });
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] talk animation failed: " + e.Message);
            ResetVoiceController();
        }
    }

    /// <summary>
    /// 把外部加载的语音交给游戏自己的语音系统播放。
    /// 只调 VoiceManager.Play，绝不调 HeroineVoiceController.PlayVoice：
    /// 后者会先把 _isFinishedVoice 置 false 再调 Play，一旦 Play 返回 null 就直接
    /// return、标志永远卡在 false，之后游戏所有非剧情语音都会被它的守卫静默丢掉。
    /// </summary>
    public static bool TryPlayNative(string clipName, AudioClip clip)
    {
        if (!Enabled || clip == null || string.IsNullOrEmpty(clipName) || !EnsureVoiceManager())
            return false;

        var injected = false;
        try
        {
            _voiceClips[clipName] = clip;
            injected = true;
            if (PlayThroughVoiceManager(clipName))
                return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] native voice failed: " + e.Message);
            ResetVoiceManager();
        }

        if (injected)
        {
            try
            {
                _voiceClips.Remove(clipName);
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    private static bool PlayThroughVoiceManager(string clipName)
    {
        if (_voiceManagerPlay == null || _voiceManager == null)
            return false;

        var player = _voiceManagerPlay.Invoke(
            _voiceManager,
            new object[] { clipName, 1f, 0f, 1f, false, null, string.Empty, null });
        return player != null;
    }

    // ---------- 解析 ----------

    /// <summary>HeroineAI 是唯一能扫到的组件，其余几个纯 C# 类只能从它的字段里取。</summary>
    private static bool EnsureHeroineAi()
    {
        if (_heroineAi != null)
            return true;

        _heroineAi = FindBehaviour("HeroineAI");
        if (_heroineAi == null)
            return false;

        var type = _heroineAi.GetType();
        _fService = type.GetField("_heroineService", Instance);
        _fVoiceController = type.GetField("_heroineVoiceController", Instance);
        _fFacialController = type.GetField("_heroineFacialController", Instance);
        _fScenarioReader = type.GetField("_scenarioReader", Instance);
        _isGameEndDirection = type.GetMethod("get_IsCurrentGameEndDirection", Instance);
        return true;
    }

    private static object ReadField(FieldInfo field)
    {
        try
        {
            return field?.GetValue(_heroineAi);
        }
        catch
        {
            return null;
        }
    }

    private static object EnsureService()
    {
        if (_service != null && _changeAnimation != null)
            return _service;

        if (!EnsureHeroineAi())
            return null;

        var service = ReadField(_fService);
        var method = service?.GetType().GetMethod("ChangeHeroineAnimationForInteger", Instance);
        if (service == null || method == null)
            return null;

        _service = service;
        _changeAnimation = method;
        return _service;
    }

    private static object EnsureVoiceController()
    {
        if (_voiceController != null && _changeTalkAnimation != null)
            return _voiceController;

        if (!EnsureHeroineAi())
            return null;

        var controller = ReadField(_fVoiceController);
        if (controller == null)
            return null;

        var type = controller.GetType();
        var method = type.GetMethod("ChangeEnableHeroineTalkAnimation", Instance);
        if (method == null)
            return null;

        _voiceController = controller;
        _changeTalkAnimation = method;
        _isFinishedVoice = type.GetProperty("IsFinishedVoice", Instance);
        _isFinishedField = type.GetField("_isFinishedVoice", Instance);
        return _voiceController;
    }

    private static object EnsureFacialController()
    {
        if (_facialController != null && _changeFacial != null)
            return _facialController;

        if (!EnsureHeroineAi())
            return null;

        var controller = ReadField(_fFacialController);
        var method = controller?.GetType().GetMethod("ChangeFacial", Instance);
        if (controller == null || method == null)
            return null;

        _facialController = controller;
        _changeFacial = method;
        return _facialController;
    }

    private static bool IsScenarioPlaying()
    {
        if (_scenarioReader == null || _isPlayingScenario == null)
        {
            if (!EnsureHeroineAi())
                return false;

            _scenarioReader = ReadField(_fScenarioReader);
            _isPlayingScenario = _scenarioReader?.GetType().GetMethod("IsPlayingScenario", Instance);
            if (_scenarioReader == null || _isPlayingScenario == null)
                return false;
        }

        try
        {
            return _isPlayingScenario.Invoke(_scenarioReader, null) is bool playing && playing;
        }
        catch
        {
            _scenarioReader = null;
            _isPlayingScenario = null;
            return false;
        }
    }

    private static bool IsGameEndDirection()
    {
        if (_isGameEndDirection == null)
            return false;

        try
        {
            return _isGameEndDirection.Invoke(_heroineAi, null) is bool ending && ending;
        }
        catch
        {
            return false;
        }
    }

    private static bool InvokeServiceFlag(string methodName)
    {
        var service = EnsureService();
        if (service == null)
            return false;

        if (!ServiceFlags.TryGetValue(methodName, out var method) || method == null)
        {
            method = service.GetType().GetMethod(methodName, Instance);
            ServiceFlags[methodName] = method;
            if (method == null)
                return false;
        }

        try
        {
            return method.Invoke(service, null) is bool busy && busy;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureVoiceManager()
    {
        if (_voiceManager != null && _voiceClips != null && _voiceManagerPlay != null)
            return true;

        try
        {
            var type = FindType("KanKikuchi.AudioManager.VoiceManager");
            if (type == null)
                return false;

            object instance = null;
            var instanceProperty = type.GetProperty(
                "Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            if (instanceProperty != null)
                instance = instanceProperty.GetValue(null);
            if (instance == null)
                instance = UnityEngine.Object.FindObjectOfType(type);
            if (instance == null)
                return false;

            var dictField = FindField(type, "_audioClipDict");
            var play = type.GetMethod("Play", Instance);
            var dict = dictField?.GetValue(instance) as IDictionary;
            if (dict == null || play == null)
                return false;

            _voiceManager = instance;
            _voiceClips = dict;
            _voiceManagerPlay = play;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] voice manager lookup failed: " + e.Message);
            ResetVoiceManager();
            return false;
        }
    }

    private static MonoBehaviour FindBehaviour(string typeFullName)
    {
        try
        {
            foreach (var component in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (component != null && component.GetType().FullName == typeFullName)
                    return component;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] " + typeFullName + " lookup failed: " + e.Message);
        }

        return null;
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

    private static FieldInfo FindField(Type type, string fieldName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(fieldName, Instance);
            if (field != null)
                return field;
        }

        return null;
    }

    private static void ResetService()
    {
        _service = null;
        _changeAnimation = null;
    }

    private static void ResetVoiceController()
    {
        _voiceController = null;
        _changeTalkAnimation = null;
        _isFinishedVoice = null;
        _isFinishedField = null;
    }

    private static void ResetFacialController()
    {
        _facialController = null;
        _changeFacial = null;
    }

    private static void ResetVoiceManager()
    {
        _voiceManager = null;
        _voiceClips = null;
        _voiceManagerPlay = null;
    }
}
