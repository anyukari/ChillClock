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

    /// <summary>
    /// 情绪 -> 动作（AnimationType 的 id）。
    ///
    /// 只用 Story_SubBase* 这一组（1001-1004 / 1101-1103 / 1201-1202 / 1301-1302）：
    /// 它们是游戏自己在"聪音坐着和你说话"时用的上半身手势，手不往桌面上放，
    /// 也不会把她切到别的工作姿势。
    ///
    /// 下面这些一个都不能用（原来 Working / Relaxed 就是用的它们）：
    ///   253/255 WorkBase002 翻页、256 WorkBase002 端茶、755 BreakBase004 吹茶
    ///   304 WorkBase003 端茶、305 WorkBase003 掃桌面
    ///   651/652 BreakBase002 翻书页
    /// 这些动作要和桌上的道具（文件、杯子、书）互动，道具是游戏自己按剧本刷出来的；
    /// 我们单独播的时候道具不在，手就按老位置伸过去，穿过键盘和桌面。
    /// 更麻烦的是 304/305 属于 WorkBase003，播完会把她留在"伏案写字"那一套姿势里，
    /// 于是她明明在敲键盘，却开始对着桌面写字。
    ///
    /// 2026-09-11 实测：她正在敲键盘（state=WorkPC、desk=Pc，本身就是 WorkBase002 家族）时
    /// 播 253/255 翻页，手照样穿过键盘 —— 说明这几个动作要的是游戏自己摆出来的纸，
    /// 跟"是不是同一个动画家族"无关。所以这批道具动作一律不用了。
    /// </summary>
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
            ["Think"] = new[] { 1301, 1004 },
            ["Curious"] = new[] { 1004, 1301 },
            ["Relaxed"] = new[] { 1001, 1101 },
            ["Tired"] = new[] { 1201, 1002 },
            ["Sleepy"] = new[] { 1201 },
            ["Working"] = new[] { 1004, 1301 },
            ["Excited"] = new[] { 1004, 1001 },
            ["Greeting"] = new[] { 1001, 1101 },
            ["Nervous"] = new[] { 1302, 1002 },
            ["Idle"] = new[] { 1301, 1001 }
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
    private static float _nextHeroineLookup;
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

    private static MethodInfo _getActionState;
    private static Type _actionStateType;
    private static MethodInfo _getCurrentAnimation;
    private static Type _animationTypeEnum;
    private static object _useObjectController;
    private static PropertyInfo _pDeskType;
    private static MethodInfo _isGameEndDirection;

    private static MonoBehaviour _clickHeroine;
    private static FieldInfo _fTimeOfDay;
    private static FieldInfo _fClickMainState;
    private static MethodInfo _getTimeOfDay;
    private static object _timeOfDayProvider;
    private static float _nextTimeOfDayLookup;

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

    /// <summary>
    /// 游戏当前时段：Morning / Noon / Evening / Night，拿不到返回 null。
    /// 来源是 Bulbul.FacilityClickHeroine._timeOfDayProvider.GetCurrentTimeOfDayType()。
    /// </summary>
    public static string GetTimeOfDay()
    {
        try
        {
            if (_timeOfDayProvider == null || _getTimeOfDay == null)
            {
                if (Time.realtimeSinceStartup < _nextTimeOfDayLookup)
                    return null;

                if (!EnsureClickHeroine())
                {
                    _nextTimeOfDayLookup = Time.realtimeSinceStartup + 2f;
                    return null;
                }

                _timeOfDayProvider = _fTimeOfDay.GetValue(_clickHeroine);
                if (_timeOfDayProvider == null)
                    return null;

                _getTimeOfDay = _timeOfDayProvider.GetType().GetMethod("GetCurrentTimeOfDayType", Instance);
                if (_getTimeOfDay == null)
                    return null;
            }

            var value = _getTimeOfDay.Invoke(_timeOfDayProvider, null);
            return value?.ToString();
        }
        catch
        {
            _clickHeroine = null;
            _timeOfDayProvider = null;
            _getTimeOfDay = null;
            _nextTimeOfDayLookup = Time.realtimeSinceStartup + 2f;
            return null;
        }
    }

    private static bool EnsureClickHeroine()
    {
        if (_clickHeroine != null)
            return true;

        _clickHeroine = FindBehaviour("Bulbul.FacilityClickHeroine");
        if (_clickHeroine == null)
            return false;

        var type = _clickHeroine.GetType();
        _fTimeOfDay = type.GetField("_timeOfDayProvider", Instance);
        _fClickMainState = type.GetField("_mainState", Instance);
        return true;
    }

    /// <summary>点击反应是否空闲（FacilityClickHeroine._mainState == 0）。</summary>
    public static bool IsClickReactionFree()
    {
        return GetClickMainState() == 0;
    }

    /// <summary>
    /// 读取 FacilityClickHeroine._mainState。
    /// 注意它声明成枚举 MainState，反射取出来是**装箱的枚举**，
    /// 一定要走 Convert.ToInt32，不能写 "is int"（那个判断永远为 false）。
    /// 读不到返回 -1。
    /// </summary>
    private static int GetClickMainState()
    {
        if (!EnsureClickHeroine() || _fClickMainState == null)
            return -1;

        try
        {
            var raw = _fClickMainState.GetValue(_clickHeroine);
            return raw == null ? -1 : Convert.ToInt32(raw);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>游戏自己判断"现在能响应点击"。</summary>
    public static bool IsPossibleClickReaction()
    {
        return InvokeServiceFlag("IsPossibleClickHeroineReaction");
    }

    /// <summary>
    /// 接管这次点击反应：播放我们池子里的台词。
    /// 返回 true 表示已经接管（调用方跳过游戏原本的反应流程）。
    /// </summary>
    public static bool CanTakeOverClickReaction()
    {
        if (IsGameSequenceBusy() || IsGameVoiceBusy())
            return false;
        if (!IsClickReactionFree())
            return false;
        if (!IsPossibleClickReaction())
            return false;
        return true;
    }

    /// <summary>
    /// 返回当前挡住"接管点击反应"的第一条原因；全部通过返回 null。
    /// 只用于诊断日志，不参与逻辑。
    /// </summary>
    public static string DescribeClickReactionBlocker()
    {
        if (IsScenarioPlaying())
            return "游戏中正在放剧情/演出";
        if (IsGameEndDirection())
            return "正在走结束通话演出";
        if (InvokeServiceFlag("IsLeaveChair"))
            return "她正在离席";
        if (InvokeServiceFlag("IsPlayingPomodoroAction"))
            return "她正在做番茄钟动作";
        if (InvokeServiceFlag("IsPlayingClickReactionAnimation"))
            return "她还在上一次的点击反应动作里";
        if (InvokeServiceFlag("IsSleeping"))
            return "她睡着了";
        if (IsGameVoiceBusy())
            return "游戏自己的语音还在播";
        if (!IsClickReactionFree())
            return "反应状态机不是空闲";
        if (!IsPossibleClickReaction())
            return "游戏自己认为现在不能反应";
        return null;
    }

    /// <summary>
    /// 她此刻在做什么（HeroineAI.ActionStateType 的数值），读不到返回 -1。
    /// 17 WorkPC / 18 WorkBook / 19 WorkReport / 21 BreakReadBook / 23 BreakTeaTime …
    /// </summary>
    private static int GetActionState()
    {
        if (_getActionState == null && !EnsureHeroineAi())
            return -1;
        if (_getActionState == null)
            return -1;

        try
        {
            var value = _getActionState.Invoke(_heroineAi, null);
            return value == null ? -1 : Convert.ToInt32(value);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>桌上现在摆的是什么：0 Book / 1 Pc / 2 Report / 3 None，读不到返回 -1。</summary>
    private static int GetDeskType()
    {
        if (_pDeskType == null && EnsureService() == null)
            return -1;
        if (_useObjectController == null || _pDeskType == null)
            return -1;

        try
        {
            var raw = _pDeskType.GetValue(_useObjectController);
            return raw == null ? -1 : Convert.ToInt32(raw);
        }
        catch
        {
            _useObjectController = null;
            _pDeskType = null;
            return -1;
        }
    }

    /// <summary>播放一个情绪对应的身体动作，并同步一个安全的表情。</summary>
    public static void Play(string emotion)
    {
        if (!Enabled || IsGameSequenceBusy())
            return;

        // 我们的动作都是"坐在桌前和你说话"的手势——游戏自己在她工作时说短句
        // （FacilityClickHeroine.OneWord → 小剧情）用的也是这一组。
        // 她离席、睡着了的时候不在桌前，这套手势会显得突兀：那就只动嘴和表情。
        int[] ids = null;
        if (!InvokeServiceFlag("IsLeaveChair") &&
            !InvokeServiceFlag("IsSleeping") &&
            EmotionAnimations.TryGetValue(emotion ?? "Idle", out var byEmotion))
        {
            ids = byEmotion;
        }

        if (ids != null &&
            ids.Length > 0 &&
            EnsureService() != null)
        {
            var id = ids[Rng.Next(ids.Length)];
            var before = CurrentAnimationName();
            try
            {
                _changeAnimation.Invoke(_service, new object[] { id });
                Plugin.Log.LogInfo("[Chill Clock] action: " + (emotion ?? "") +
                                   " -> " + AnimationName(id) + "(" + id + ")" +
                                   " state=" + ActionStateName(GetActionState()) +
                                   " desk=" + DeskName(GetDeskType()) +
                                   " was=" + before);
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

    /// <summary>把动作 id 翻成 AnimationType 里的名字，只给日志用。</summary>
    private static string AnimationName(int id)
    {
        try
        {
            // ChangeHeroineAnimationForInteger 的参数是 int，枚举类型得从
            // GetCurrentAnimationType() 的返回值上拿。
            return _animationTypeEnum != null && _animationTypeEnum.IsEnum
                ? (Enum.GetName(_animationTypeEnum, id) ?? "?")
                : "?";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>她当前播放的整套动画（AnimationType 的名字），只给日志用。</summary>
    private static string CurrentAnimationName()
    {
        try
        {
            if (_getCurrentAnimation == null || _service == null || _animationTypeEnum == null)
                return "?";

            var raw = _getCurrentAnimation.Invoke(_service, null);
            if (raw == null)
                return "?";

            var value = Convert.ToInt32(raw);
            return (Enum.GetName(_animationTypeEnum, value) ?? "?") + "(" + value + ")";
        }
        catch
        {
            return "?";
        }
    }

    private static string ActionStateName(int value)
    {
        try
        {
            if (value < 0)
                return "?";

            var name = _actionStateType != null && _actionStateType.IsEnum
                ? (Enum.GetName(_actionStateType, value) ?? "?")
                : "?";
            return name + "(" + value + ")";
        }
        catch
        {
            return "?";
        }
    }

    private static string DeskName(int value)
    {
        try
        {
            if (value < 0)
                return "?";

            var type = _pDeskType?.PropertyType;
            var name = type == null || !type.IsEnum
                ? "?"
                : (Enum.GetName(type, value) ?? "?");
            return name + "(" + value + ")";
        }
        catch
        {
            return "?";
        }
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

        // 场景里没有 HeroineAI 时（标题画面 / 读盘 / 结算）不要每次调用都全场景扫一遍：
        // 这个方法会被演出闸门每秒调用好几次，FindObjectsOfType<MonoBehaviour>() 很贵。
        // 失败后隔 2 秒再试；成功时不设退避，这样换场景后能立刻重新解析。
        if (Time.realtimeSinceStartup < _nextHeroineLookup)
            return false;

        _heroineAi = FindBehaviour("HeroineAI");
        if (_heroineAi == null)
        {
            _nextHeroineLookup = Time.realtimeSinceStartup + 2f;
            return false;
        }

        var type = _heroineAi.GetType();
        _fService = type.GetField("_heroineService", Instance);
        _fVoiceController = type.GetField("_heroineVoiceController", Instance);
        _fFacialController = type.GetField("_heroineFacialController", Instance);
        _fScenarioReader = type.GetField("_scenarioReader", Instance);
        _isGameEndDirection = type.GetMethod("get_IsCurrentGameEndDirection", Instance);
        _getActionState = type.GetMethod("GetCurrentState", Instance);
        _actionStateType = _getActionState?.ReturnType;
        _useObjectController = null;
        _pDeskType = null;
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

        var serviceType = service.GetType();
        _getCurrentAnimation = serviceType.GetMethod("GetCurrentAnimationType", Instance);
        _animationTypeEnum = _getCurrentAnimation?.ReturnType;

        // 桌面上现在摆的是什么（书 / 电脑 / 写字），用来判断她的工作姿势
        try
        {
            var property = serviceType.GetProperty("HeroineUseObjectController", Instance);
            var controller = property?.GetValue(service);
            _useObjectController = controller;
            _pDeskType = controller?.GetType().GetProperty("DeskType", Instance);
        }
        catch
        {
            _useObjectController = null;
            _pDeskType = null;
        }

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
