using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ChillFocusWhitelist.Core;
using ChillFocusWhitelist.UI;
using HarmonyLib;
using R3;
using UnityEngine;

namespace ChillFocusWhitelist;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.chillclock.plugin";
    public const string Name = "Chill Clock";
    public const string Version = "0.5.0";

    internal static ManualLogSource Log = null!;
    internal static Plugin Instance = null!;

    private ConfigEntry<bool> _masterEnabled = null!;
    private ConfigEntry<bool> _disableStopSkip = null!;
    private ConfigEntry<bool> _hideUiDuringFocus = null!;
    private ConfigEntry<bool> _blockGameExitOnFocus = null!;
    private ConfigEntry<bool> _voiceReminders = null!;
    private ConfigEntry<bool> _heroineReactions = null!;
    private ConfigEntry<bool> _clickReaction = null!;
    private WhitelistStore _store = null!;
    private WindowGuard _guard = null!;
    private FocusSessionWatcher _watcher = null!;
    private SettingsPageInjector _ui = null!;
    private FocusUiHider _uiHider = null!;
    private CloseGuard _closeGuard = null!;
    private EscKeyGuard _escGuard = null!;
    private VoiceManager _voiceManager = null!;

    private bool _focusActive;
    private bool _pomodoroSessionActive;
    private Bulbul.PomodoroService _pomodoroServiceInstance;
    private Harmony _harmony = null!;
    private bool _loggedServiceMissing;
    private bool _subscribed;
    private CompositeDisposable _subscriptions;
    private float _nextCoreTick;
    private bool _pendingDistractionVoice;
    private bool _pendingTaskManagerVoice;
    private bool _pendingExitVoice;
    private bool _pendingRestVoice;
    private float _nextAmbientVoiceTime;
    private float _nextRestChatTime;
    private float _voiceGraceUntil;

    /// <summary>
    /// 专注刚开场时游戏自己会播一句（比如「开始工作了」）。这段时间先别插话，
    /// 否则要么盖掉它，要么让它的 PlayVoice 被静默丢弃。
    /// </summary>
    private const float FocusVoiceGraceSeconds = 6f;

    private void Awake()
    {
        Log = Logger;
        Instance = this;

        _masterEnabled = Config.Bind("General", "Enabled", true, "总开关：是否启用专注白名单功能。");
        _disableStopSkip = Config.Bind(
            "Focus", "DisableStopSkip", true,
            "专注/休息期间是否隐藏番茄钟的停止与跳过按钮。");
        _hideUiDuringFocus = Config.Bind(
            "Focus", "HideUiDuringFocus", false,
            "专注期间是否隐藏主界面右侧按钮列和等级图标。");
        _blockGameExitOnFocus = Config.Bind(
            "Focus", "BlockGameExitOnFocus", false,
            "专注期间是否拦截右上角 X / 任务栏关闭等正常退出操作。");
        _voiceReminders = Config.Bind(
            "Focus", "VoiceReminders", true,
            "走神或尝试退出时是否播放聪音的语音提醒。");
        _heroineReactions = Config.Bind(
            "Focus", "HeroineReactions", true,
            "念台词时是否让聪音配合动作和表情。关掉后本模组完全不碰游戏的动作/表情/口型系统。");
        _clickReaction = Config.Bind(
            "Focus", "ClickReaction", true,
            "点击聪音时是否也用扩充的台词回应（会跳过游戏原本的那句反应）。按她当前状态+时段挑选。");

        // 老版本的 cfg 是早先写下的，新加的项（比如 ClickReaction）不会自动出现。
        // 这里重写一次，保证配置文件里能看到所有开关。
        try
        {
            Config.Save();
        }
        catch (Exception e)
        {
            Logger.LogWarning("[Chill Clock] config save failed: " + e.Message);
        }

        var pluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        var whitelistPath = Path.Combine(pluginDirectory ?? ".", "FocusWhitelist.txt");

        _store = new WhitelistStore(whitelistPath);
        _guard = new WindowGuard(_store);
        _watcher = new FocusSessionWatcher();
        _uiHider = new FocusUiHider();
        _closeGuard = new CloseGuard(() => ShouldBlockGameExit());
        _escGuard = new EscKeyGuard();
        Application.wantsToQuit += OnWantsToQuit;
        _ui = new SettingsPageInjector(
            _store,
            () => _masterEnabled.Value,
            value => SetConfigValue(_masterEnabled, value),
            () => _disableStopSkip.Value,
            value => SetConfigValue(_disableStopSkip, value),
            () => _hideUiDuringFocus.Value,
            value => SetConfigValue(_hideUiDuringFocus, value),
            () => _blockGameExitOnFocus.Value,
            value => SetConfigValue(_blockGameExitOnFocus, value),
            () => _voiceReminders.Value,
            value => SetConfigValue(_voiceReminders, value));

        var hostObject = new GameObject("ChillClockHost");
        hostObject.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(hostObject);
        hostObject.AddComponent<FocusHostBehaviour>();
        _voiceManager = new VoiceManager(hostObject);
        _guard.OnWindowMinimized += OnWindowMinimized;
        _closeGuard.OnCloseBlocked = () => _pendingExitVoice = true;
        try
        {
            _harmony = new Harmony(Guid);
            var setup = AccessTools.Method(typeof(Bulbul.SettingUI), "Setup");
            var activate = AccessTools.Method(typeof(Bulbul.SettingUI), "Activate");

            if (setup != null)
                _harmony.Patch(setup, postfix: PatchMethod("FocusWhitelistSetupPatch", "Postfix"));
            if (activate != null)
                _harmony.Patch(activate, postfix: PatchMethod("FocusWhitelistActivatePatch", "Postfix"));

            var pomodoro = typeof(Bulbul.PomodoroService);
            PatchPomodoro(pomodoro, "StartPomodoro", "PomodoroStartPatch", true);
            PatchPomodoro(pomodoro, "OnTimerEnd", "PomodoroTimerEndPatch", true);
            PatchPomodoro(pomodoro, "ResetTimer", "PomodoroResetPatch", true);
            PatchPomodoro(pomodoro, "CompletePomodoroTimer", "PomodoroCompletePatch", false);

            var countup = typeof(Bulbul.CountupService);
            PatchCountup(countup, "StartCountup", "CountupStartPatch", true);
            PatchCountup(countup, "PlayOrPauseCountupTimer", "CountupTogglePatch", true);
            PatchCountup(countup, "ResetTimer", "CountupResetPatch", true);
            PatchCountup(countup, "CompleteCountupTimer", "CountupCompletePatch", false);

            var reactionReady = AccessTools.Method(typeof(Bulbul.FacilityClickHeroine), "ReactionReady");
            if (reactionReady != null)
                _harmony.Patch(reactionReady, prefix: PatchMethod("ClickReactionPatch", "Prefix"));

            var patched = _harmony.GetPatchedMethods()
                .Select(m => m.DeclaringType?.Name + "." + m.Name)
                .ToList();
            Logger.LogInfo("Patched methods: " + string.Join(", ", patched));
        }
        catch (Exception e)
        {
            Logger.LogWarning("Harmony patch failed: " + e);
        }

        Logger.LogInfo(Name + " v" + Version + " loaded. Whitelist: " + whitelistPath);
    }

    internal void TickHost()
    {
        UpdateCloseGuard();
        _escGuard?.EnsureInstalled();
        _ui.Tick();
        _uiHider.Tick(
            _masterEnabled.Value && IsPomodoroSessionActive() && _disableStopSkip.Value,
            _masterEnabled.Value &&
            _focusActive &&
            _hideUiDuringFocus.Value,
            _masterEnabled.Value &&
            IsPomodoroSessionActive() &&
            _hideUiDuringFocus.Value);
        ProcessVoiceReminders();
        TickAmbientVoice();
        HeroineActionBridge.Enabled = _heroineReactions.Value;
        TickCoreHost();
    }

    /// <summary>
    /// 专注中的定时闲聊 / 休息中的闲聊。间隔刻意拉得很开，避免打断专注。
    /// </summary>
    private void TickAmbientVoice()
    {
        if (_voiceManager == null || !_voiceReminders.Value || !_masterEnabled.Value)
            return;

        var now = Time.realtimeSinceStartup;

        if (_focusActive)
        {
            _nextRestChatTime = 0f;
            if (_nextAmbientVoiceTime <= 0f)
            {
                _nextAmbientVoiceTime = now + 8f * 60f;
                return;
            }
            if (now >= _nextAmbientVoiceTime)
            {
                _nextAmbientVoiceTime = now + UnityEngine.Random.Range(12f, 20f) * 60f;
                _voiceManager.PlayAmbient();
            }
            return;
        }

        if (IsPomodoroSessionActive())
        {
            _nextAmbientVoiceTime = 0f;
            if (_nextRestChatTime <= 0f)
            {
                _nextRestChatTime = now + 60f;
                return;
            }
            if (now >= _nextRestChatTime)
            {
                _nextRestChatTime = now + UnityEngine.Random.Range(2f, 4f) * 60f;
                _voiceManager.PlayRestReminder();
            }
            return;
        }

        // 既不在专注也不在休息：一句都不说。
        // 顺手把计时器清零，免得下次进入专注时因为计时器早就过期而立刻开口。
        _nextRestChatTime = 0f;
        _nextAmbientVoiceTime = 0f;
    }

    private void OnWindowMinimized(string processName, bool isTaskManager)
    {
        if (isTaskManager)
            _pendingTaskManagerVoice = true;
        else
            _pendingDistractionVoice = true;
    }

    private void ProcessVoiceReminders()
    {
        // 订阅在某些场景切换后会丢，这里每帧自愈一次
        if (_guard != null && _guard.OnWindowMinimized == null)
            _guard.OnWindowMinimized += OnWindowMinimized;

        if (_voiceManager == null || !_voiceReminders.Value)
        {
            ClearPendingVoices();
            return;
        }

        // 专注刚开场：让游戏先说完它自己的那句
        if (Time.realtimeSinceStartup < _voiceGraceUntil)
            return;

        if (_pendingExitVoice)
        {
            ConsumeVoice(_voiceManager.PlayExitAttempt());
            return;
        }

        if (_pendingTaskManagerVoice)
        {
            ConsumeVoice(_voiceManager.PlayTaskManager());
            return;
        }

        if (_pendingRestVoice)
        {
            ConsumeVoice(_voiceManager.PlayRestReminder());
            return;
        }

        if (_pendingDistractionVoice)
            ConsumeVoice(_voiceManager.PlayDistraction());
    }

    /// <summary>Deferred 表示这次没播成，保留待播标记下一帧再试。</summary>
    private void ConsumeVoice(VoiceStartResult result)
    {
        if (result == VoiceStartResult.Deferred)
            return;

        ClearPendingVoices();
    }

    private void ClearPendingVoices()
    {
        _pendingDistractionVoice = false;
        _pendingTaskManagerVoice = false;
        _pendingExitVoice = false;
        _pendingRestVoice = false;
    }

    private void UpdateCloseGuard()
    {
        if (_closeGuard == null)
            return;

        var shouldGuard = _masterEnabled.Value &&
                          _blockGameExitOnFocus.Value &&
                          (_focusActive || IsPomodoroSessionActive());
        if (shouldGuard)
            _closeGuard.EnsureInstalled();
        else
            _closeGuard.Uninstall();
    }

    private void TickCoreHost()
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextCoreTick)
            return;
        _nextCoreTick = now + 0.2f;

        try
        {
            if (!_masterEnabled.Value)
            {
                if (_focusActive)
                {
                    _focusActive = false;
                    _guard.SetFocusActive(false);
                    Logger.LogInfo("[Chill Clock] focus off (master disabled)");
                }
                return;
            }

            // 巡逻不依赖 DI：事件一旦开启专注，就持续每 0.2 秒扫描并隐藏。
            if (_focusActive)
            {
                _guard.Tick();
            }
            else if (IsPomodoroSessionActive())
            {
                // 休息阶段不再最小化普通应用，但继续关闭任务管理器。
                _guard.TickTaskManagerOnly();
            }

            var known = _watcher.TryGetWorkActive(out var workActive);
            if (!known)
            {
                if (!_loggedServiceMissing)
                {
                    _loggedServiceMissing = true;
                    Logger.LogWarning("[Chill Clock] PomodoroService not available yet, will retry");
                }
                return;
            }

            _loggedServiceMissing = false;
            EnsureSubscriptions();

            // 轮询只把专注“打开”；“关闭”一律由游戏事件（休息/完成/重置）驱动，
            // 避免游戏失焦自动暂停时误放行非白名单窗口。
            if (workActive && !_focusActive)
            {
                _focusActive = true;
                _guard.SetFocusActive(true);
                _voiceGraceUntil = Time.realtimeSinceStartup + FocusVoiceGraceSeconds;
                ClearPendingVoices();
                Logger.LogInfo("[Chill Clock] focus state -> Work active (poll)");
            }
        }
        catch (Exception e)
        {
            Logger.LogWarning("[Chill Clock] core tick failed: " + e);
        }
    }

    private void EnsureSubscriptions()
    {
        if (_subscribed || _watcher.Service == null)
            return;

        try
        {
            _subscriptions = new CompositeDisposable();
            var service = _watcher.Service;

            service.OnStartWork.Subscribe(_ =>
            {
                _pomodoroSessionActive = true;
                SetFocusFromEvent(true);
            }).AddTo(_subscriptions);
            service.OnStartBreak.Subscribe(_ =>
            {
                _pomodoroSessionActive = true;
                SetFocusFromEvent(false);
            }).AddTo(_subscriptions);
            service.OnUnpause.Subscribe(type => SetFocusFromEvent(type == Bulbul.PomodoroService.PomodoroType.Work)).AddTo(_subscriptions);
            service.OnCompletePomodoro.Subscribe(_ =>
            {
                _pomodoroSessionActive = false;
                SetFocusFromEvent(false);
            }).AddTo(_subscriptions);
            _subscribed = true;
            Logger.LogInfo("[Chill Clock] subscribed to PomodoroService events");
        }
        catch (Exception e)
        {
            Logger.LogWarning("[Chill Clock] event subscribe failed: " + e);
        }
    }

    internal void SetFocusFromEvent(bool active)
    {
        if (!_masterEnabled.Value)
            return;

        if (_focusActive == active)
            return;

        _focusActive = active;
        _guard.SetFocusActive(active);
        if (active)
        {
            _voiceGraceUntil = Time.realtimeSinceStartup + FocusVoiceGraceSeconds;
            // 开始专注前攒下的待播提醒（大多是开场收窗口引起的）不要带进来
            ClearPendingVoices();
        }
        Logger.LogInfo("[Chill Clock] event focus state -> " + (active ? "Work active" : "ended"));
    }

    internal void SetPomodoroSessionActive(bool active)
    {
        _pomodoroSessionActive = active;
        if (!active)
            _pendingRestVoice = false;
    }

    internal void OnBreakStarted()
    {
        _pendingRestVoice = true;
    }

    /// <summary>游戏马上要/正在自己说话时，把我们这边正在播的台词停掉（避免叠音）。</summary>
    internal void AbortVoiceForGameLine()
    {
        try
        {
            _voiceManager?.Abort();
        }
        catch (Exception e)
        {
            Logger.LogWarning("[Chill Clock] abort voice failed: " + e.Message);
        }
    }

    internal void AttachPomodoroService(Bulbul.PomodoroService service)
    {
        _pomodoroServiceInstance = service;
    }

    internal bool IsPomodoroSessionActive()
    {
        if (_pomodoroSessionActive)
            return true;
        if (_pomodoroServiceInstance == null)
            return false;

        try
        {
            // 专注、休息、暂停阶段都算会话进行中；
            // 只有整个番茄钟完成后 CurrentPomodoroType 才变为 Complete。
            return _pomodoroServiceInstance.IsTimerRunning() ||
                   _pomodoroServiceInstance.CurrentPomodoroType !=
                   Bulbul.PomodoroService.PomodoroType.Complete;
        }
        catch
        {
            return false;
        }
    }

    internal bool ShouldBlockGameExit()
    {
        if (!_masterEnabled.Value || !_blockGameExitOnFocus.Value)
            return false;
        if (!_focusActive && !IsPomodoroSessionActive())
            return false;

        // 游戏自己正在走"结束通话"演出时放行，否则它的收尾流程会被我们卡住
        if (HeroineActionBridge.IsGameEndingCall())
            return false;

        return true;
    }

    /// <summary>
    /// 点击反应的处理结果。
    /// </summary>
    internal enum ClickReactionResult
    {
        /// <summary>不管这次点击，走游戏原逻辑。</summary>
        PassThrough,

        /// <summary>我们接管了这次点击（已经播出我们的台词）。</summary>
        TakeOver,

        /// <summary>现在不能反应（她还在说话）——交给游戏播它自己的"点不动"反馈。</summary>
        Blocked
    }

    /// <summary>
    /// 点击反应的处理：开关、状态、以及我们的候选池都得满足才接管。
    /// 她还在说我们这边的台词时返回 Blocked，让游戏出它的禁止光标，而不是被我们吞掉。
    /// </summary>
    internal ClickReactionResult HandleClickReaction(Bulbul.FacilityClickHeroine.ReactionType reactionType)
    {
        if (!_clickReaction.Value)
            return ClickReactionResult.PassThrough;
        if (_voiceManager == null || !_voiceReminders.Value || !_masterEnabled.Value)
            return ClickReactionResult.PassThrough;

        // 只接管"玩家点击"，不碰她自发的 HeroineSelf
        if (reactionType != Bulbul.FacilityClickHeroine.ReactionType.Click)
            return ClickReactionResult.PassThrough;

        if (!HeroineActionBridge.CanTakeOverClickReaction())
            return ClickReactionResult.PassThrough;

        var state = _focusActive
            ? "Work"
            : (IsPomodoroSessionActive() ? "Break" : "Normal");

        var result = _voiceManager.PlayClick(state);
        if (result == VoiceStartResult.Started)
            return ClickReactionResult.TakeOver;

        // Deferred = 她还在说（我们的台词没完）。这时按游戏自己的规矩"现在不能点"，
        // 让点击流程走那个禁止反馈；既不会插新台词，也不会让游戏叠一条它自己的话。
        if (result == VoiceStartResult.Deferred)
            return ClickReactionResult.Blocked;

        // Skipped（冷却中 / 池子空）才让游戏走它自己的反应
        return ClickReactionResult.PassThrough;
    }

    private bool OnWantsToQuit()
    {
        if (!ShouldBlockGameExit())
            return true;

        Logger.LogWarning("[Chill Clock] 番茄钟会话中已拦截退出请求");
        return false;
    }

    private void OnDestroy()
    {
        Application.wantsToQuit -= OnWantsToQuit;
        _voiceManager?.Dispose();
        _closeGuard?.Uninstall();
        _escGuard?.Uninstall();
        _guard.OnWindowMinimized -= OnWindowMinimized;
        _pomodoroServiceInstance = null;
        _uiHider?.RestoreAll();
        _guard?.ReleaseAll();
    }

    private static HarmonyMethod PatchMethod(string typeName, string methodName)
    {
        var type = typeof(FocusWhitelistSetupPatch).Assembly.GetType("ChillFocusWhitelist.UI." + typeName);
        var method = type?.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (method == null)
            Plugin.Log.LogWarning("Patch method not found: " + typeName + "." + methodName);
        return new HarmonyMethod(method);
    }

    private void PatchPomodoro(Type type, string methodName, string patchTypeName, bool isPrefix)
    {
        var original = AccessTools.Method(type, methodName);
        if (original != null)
        {
            var patch = PatchMethod(patchTypeName, isPrefix ? "Prefix" : "Postfix");
            _harmony.Patch(original, prefix: isPrefix ? patch : null, postfix: isPrefix ? null : patch);
        }
    }

    private void PatchCountup(Type type, string methodName, string patchTypeName, bool isPrefix)
    {
        var original = AccessTools.Method(type, methodName);
        if (original != null)
        {
            var patch = PatchMethod(patchTypeName, isPrefix ? "Prefix" : "Postfix");
            _harmony.Patch(original, prefix: isPrefix ? patch : null, postfix: isPrefix ? null : patch);
        }
    }

    private static void SetConfigValue(ConfigEntry<bool> entry, bool value)
    {
        entry.Value = value;
    }
}
