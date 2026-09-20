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
public const string Version = "0.9.5";

    internal static ManualLogSource Log = null!;
    internal static Plugin Instance = null!;

    private ConfigEntry<bool> _masterEnabled = null!;
    private ConfigEntry<bool> _disableStopSkip = null!;
    private ConfigEntry<bool> _hideUiDuringFocus = null!;
    private ConfigEntry<bool> _blockGameExitOnFocus = null!;
    private ConfigEntry<bool> _voiceReminders = null!;
    private ConfigEntry<bool> _ambientVoice = null!;
    private ConfigEntry<bool> _heroineReactions = null!;
    private ConfigEntry<bool> _clickReaction = null!;
    private WhitelistStore _store = null!;
    private WindowGuard _guard = null!;
    private FocusSessionWatcher _watcher = null!;
    private SettingsPageInjector _ui = null!;
    private FocusUiHider _uiHider = null!;
    private SteamCloseGuard _steamCloseGuard = null!;
    private CloseGuard _closeGuard = null!;
    private VoiceManager _voiceManager = null!;

    private bool _focusActive;
    private float _nextMiniWindowCheck;
    private bool _isMiniWindow;
    private bool _pomodoroSessionActive;
    private Bulbul.PomodoroService _pomodoroServiceInstance;
    private Harmony _harmony = null!;
    private bool _loggedServiceMissing;
    private bool _subscribed;
    private CompositeDisposable _subscriptions;
    private float _nextCoreTick;
    private float _nextGuardSweep;
    private bool _quitting;
    private float _quittingAt;
    private bool _pendingDistractionVoice;
    private bool _pendingTaskManagerVoice;
    private bool _pendingExitVoice;
    private bool _pendingRestVoice;
    private float _nextAmbientVoiceTime;
    private float _nextIdleTalkTime;
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
            "走神 / 任务管理器 / 退出 / 休息开始时是否播放聪音的语音提醒。" +
            "（专注中的自言自语另有开关，见 AmbientVoice）");
        // 专注中自言自语：和上面的"提醒语音"相互独立 ——
        // 只想安安静静专注、但保留走神提醒的话，把这一项关掉即可。
        _ambientVoice = Config.Bind(
            "Focus", "AmbientVoice", true,
            "专注期间是否让聪音偶尔自言自语（Ambient 台词池）。与走神/退出提醒相互独立。");
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
        _steamCloseGuard = new SteamCloseGuard((path, name) => _store.IsAllowed(path, name));
        _closeGuard = new CloseGuard(() => ShouldBlockGameExit());
        Application.wantsToQuit += OnWantsToQuit;
        // 退出时尽早把鼠标钩子还回去：它是"系统随时会回调进来"的原生钩子，
        // 拖到进程收尾阶段再解，容易变成退出时崩溃（0xc0000005）。
        Application.quitting += OnQuitting;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
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
            value => SetConfigValue(_voiceReminders, value),
            () => _ambientVoice.Value,
            value => SetConfigValue(_ambientVoice, value));

        var hostObject = new GameObject("ChillClockHost");
        hostObject.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(hostObject);
        hostObject.AddComponent<FocusHostBehaviour>();
        _voiceManager = new VoiceManager(hostObject);
        _guard.OnWindowMinimized += OnWindowMinimized;
        _steamCloseGuard.OnCloseBlocked = () => _pendingExitVoice = true;
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

            // 游戏自己要开口时先让我们闭嘴（HeroineAI 在全局命名空间，只能按名字找）
            // 我们念台词期间，拦掉游戏"挂在动作上的小声音"（喝咖啡呼呼 / 看书嗯声）——
            // 这些都从 HeroineVoiceController.PlayVoice 出去，名字前缀是 Motion_。
            var voiceController = AccessTools.TypeByName("HeroineVoiceController");
            var controllerPlay = voiceController == null
                ? null
                : AccessTools.Method(voiceController, "PlayVoice",
                    new[] { typeof(string), typeof(bool), typeof(bool) });
            if (controllerPlay != null)
                _harmony.Patch(controllerPlay, prefix: PatchMethod("MotionVoiceSuppressPatch", "Prefix"));
            else
                Log.LogWarning("[Chill Clock] 没找到 HeroineVoiceController.PlayVoice，动作音拦截没挂上");

            // 那些声音其实是从 MotionSoundController 出来的（它管"动作自带的声音"）：
            // 喝东西吹气"呼呼"、端杯子、翻页(看书/翻小说)…我们说话期间一律不播。
            // 用户实测过：关掉它就会被"看书时的嗯声"打断且接不回来，所以重新打开，
            // 并且把剩下的动作声音（打气握拳、蹦起来、伸懒腰、思考/疑问那几句）也一起挂上。
            const bool enableMotionSoundSuppress = true;
            var motionSound = enableMotionSoundSuppress ? AccessTools.TypeByName("MotionSoundController") : null;
            if (motionSound != null)
            {
                var names = new[] { "PlayDrinkToCoolVoice", "PlayDrinkToCoolVoice_2", "PlayDrinkHotVoice",
                                    "NovelFlipPage", "TextbookFlipPage",
                                    "PlayCupSoundForPlacing", "PlayCupSoundAfterDrinking",
                                    "PlayCupSoundForSmallMovement", "PlayCupSoundForStrongMovement",
                                    "PlayGutsVoice", "PlayJumpUpStartVoice", "PlayJumpUpEndVoice",
                                    "PlayStretchStartVoice", "PlayStretchEndVoice",
                                    "PlayInterestVoice", "PlayQuestionVoice", "PlayThinkingVoice",
                                    "PlayUnderstandVoice", "PlayDropPenVoice", "PlayHandClap",
                                    // 笑出声那一条也是走语音系统的动作音：我们说话期间不该响
                                    //（不然又会把我们的整句掐断一次）
                                    "PlayLaughVoice" };
                var motionPatched = 0;
                foreach (var name in names)
                {
                    var method = AccessTools.Method(motionSound, name);
                    if (method == null)
                        continue;
                    _harmony.Patch(method, prefix: PatchMethod("MotionVoiceSuppressPatch", "SimplePrefix"));
                    motionPatched++;
                }
                Log.LogInfo("[Chill Clock] 动作声音拦截已挂 " + motionPatched + " 个方法");
            }
            else
                Log.LogWarning("[Chill Clock] 没找到 MotionSoundController");

            var heroineAiType = AccessTools.TypeByName("HeroineAI");
            var playVoice = heroineAiType == null
                ? null
                : AccessTools.Method(heroineAiType, "PlayVoice", new[] { typeof(string), typeof(bool), typeof(bool) });
            if (playVoice != null)
                _harmony.Patch(playVoice, prefix: PatchMethod("HeroineVoicePatch", "Prefix"));

            else
                Logger.LogWarning("HeroineAI.PlayVoice not found; 游戏开口时不会主动让路");

            // 游戏自己写字幕时通知我们：我们在显示期间它只换文字、不重新激活，
            // 我们收尾时得让开，别把它的字幕一起淡掉
            var scenarioMessageType = AccessTools.TypeByName("Bulbul.ScenarioTextMessage");
            var startText = scenarioMessageType == null
                ? null
                : AccessTools.Method(scenarioMessageType, "StartText", new[] { typeof(string) });
            if (startText != null)
                _harmony.Patch(startText, prefix: PatchMethod("SubtitleTextPatch", "Prefix"));
            else
                Logger.LogWarning("ScenarioTextMessage.StartText not found; 游戏自己的字幕可能被我们盖掉");

            PatchExternalPiP();

            // 专注期间的 ESC：不用全局键盘钩子（那个会让键盘发粘），
            // 改成在进程内让游戏的 Input 读不到 ESC（见 UI\EscInputPatch.cs）。
            var inputType = typeof(UnityEngine.Input);
            var escPatched = 0;
            foreach (var inputMethod in new[] { "GetKeyDown", "GetKey", "GetKeyUp" })
            {
                var target = AccessTools.Method(inputType, inputMethod, new[] { typeof(KeyCode) });
                if (target == null)
                    continue;
                _harmony.Patch(target, prefix: PatchMethod("EscInputPatch", "Prefix"));
                escPatched++;
            }
            Log.LogInfo("[Chill Clock] ESC 拦截已挂 " + escPatched + " 个输入方法（进程内，无全局钩子）");

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
        _ui.Tick();
        _uiHider.Tick(
            // 专注时禁止结束/跳过：藏番茄钟的停止/跳过按钮（独立开关）
            _masterEnabled.Value && IsPomodoroSessionActive() && _disableStopSkip.Value,
            // 专注时隐藏 UI：藏主界面右侧那列图标
            _masterEnabled.Value &&
            _focusActive &&
            _hideUiDuringFocus.Value,
            // 设置 / 结束通话：只有"隐藏UI 打开 + 专注中"才藏
            _masterEnabled.Value &&
            IsPomodoroSessionActive() &&
            _hideUiDuringFocus.Value &&
            _focusActive,
            // 其余情况一律"显示 + 上锁"（游戏自己的 LockUI）：
            // 休息阶段，以及"隐藏UI"没打开时的专注阶段 —— 否则这两种情况能直接点结束通话退出
            _masterEnabled.Value &&
            IsPomodoroSessionActive() &&
            (!_focusActive || !_hideUiDuringFocus.Value),
            // 番茄钟的播放/暂停按钮：整个会话期间都藏（和"隐藏UI"无关）
            _masterEnabled.Value &&
            IsPomodoroSessionActive());
        ProcessVoiceReminders();
        TickAmbientVoice();
        // ===== 临时调试（用完删掉）：启动 30 秒后把游戏的 MasterData 导一份出来 =====
        if (Time.realtimeSinceStartup > 30f)
            Core.MasterDataDump.DumpOnce(Logger);

        HeroineActionBridge.Enabled = _heroineReactions.Value;
        // 她端起杯子喝水时把视线收回去、喝完再转回来。
        //
        // 上一版这里没有 try/catch，运行时一旦抛异常，BepInEx 会把这个插件整个停掉 ——
        // 表现就是"点击完全没反应"（所有补丁都不在了）。所以：
        //   1) 外面裹 try/catch，异常绝不允许冒到 Update 之外
        //   2) 内部有 0.25 秒节流，不是每帧都做重活
        // 喝水相关的特殊处理**已全部删除**（用户要求）。
        //
        // 之前试过两版：一是"她喝水就把视线收回去、喝完再转回来"，二是"她喝水时
        // 我们的台词先等着"。前者会让端着杯子时点不动（视线补间动画占住了游戏的
        // 点击反应），后者会让连播卡在等待里、点击全程失效。都不划算，删掉。
        // 现在喝水就是游戏自己的行为，我们只保持"说话时转头看你"这一条通用逻辑。
        TickCoreHost();
    }

    /// <summary>
    /// 专注中的定时闲聊 / 休息中的闲聊。间隔刻意拉得很开，避免打断专注。
    ///
    /// 两段各归各的开关：
    ///   AmbientVoice   —— 专注中的自言自语（关掉它不影响走神 / 退出提醒）
    ///   VoiceReminders —— 休息中的闲聊（这一段跟提醒语音共用一个总闸）
    /// 提醒类语音（走神 / 任务管理器 / 退出 / 休息开始）在 ProcessVoiceReminders 里，不受本方法影响。
    /// </summary>
    private void TickAmbientVoice()
    {
        if (_voiceManager == null || !_masterEnabled.Value)
            return;

        var chatInFocus = _ambientVoice.Value;
        var chatInBreak = _voiceReminders.Value;
        if (!chatInFocus && !chatInBreak)
        {
            // 两段都不说话：计时器清零，这样下次打开时是"重新计时"，
            // 而不是因为计时器早就过期、一开就立刻蹦出一句。
            _nextAmbientVoiceTime = 0f;
            _nextRestChatTime = 0f;
            return;
        }

        var now = Time.realtimeSinceStartup;

        if (_focusActive)
        {
            _nextRestChatTime = 0f;
            _nextIdleTalkTime = 0f;

            // 自言自语关掉了：不推进计时器，等下次打开时重新计时
            if (!chatInFocus)
            {
                _nextAmbientVoiceTime = 0f;
                return;
            }

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
            _nextIdleTalkTime = 0f;

            if (!chatInBreak)
            {
                _nextRestChatTime = 0f;
                return;
            }

            if (_nextRestChatTime <= 0f)
            {
                _nextRestChatTime = now + 60f;
                return;
            }
            if (now >= _nextRestChatTime)
            {
                _nextRestChatTime = now + UnityEngine.Random.Range(2f, 4f) * 60f;
                // 休息时一半是小课堂那种闲聊，一半还是原来的休息提醒
                if (chatInBreak && UnityEngine.Random.value < 0.5f)
                    _voiceManager.PlayBreakTalk();
                else
                    _voiceManager.PlayRestReminder();
            }
            return;
        }

        // 既不在专注也不在休息 = 待机。
        // 这里放小课堂那段闲聊（用户要在非专注时也能听到），间隔比专注时短一些。
        _nextRestChatTime = 0f;
        _nextAmbientVoiceTime = 0f;

        if (!chatInFocus)
        {
            _nextIdleTalkTime = 0f;
            return;
        }

        if (_nextIdleTalkTime <= 0f)
        {
            _nextIdleTalkTime = now + 5f * 60f;
            return;
        }

        if (now >= _nextIdleTalkTime)
        {
            _nextIdleTalkTime = now + UnityEngine.Random.Range(7f, 12f) * 60f;
            _voiceManager.PlayIdleTalk();
        }
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
        var shouldGuard = _masterEnabled.Value &&
                          _blockGameExitOnFocus.Value &&
                          (_focusActive || IsPomodoroSessionActive());
        if (shouldGuard)
        {
            // 退出拦截：接管游戏窗口过程（右上角 X / Alt+F4 / 任务栏关闭都走这条）。
            // 但它和"小窗模式"冲突（画中画 mod 会改同一个窗口，专注中按 F3 会崩），
            // 所以小窗期间自动卸掉，退出小窗再装回来 —— 见 IsMiniWindowMode()。
            if (IsMiniWindowMode())
            {
                // 小窗模式（或者刚切过小窗）：这时候必须让开，别和画中画 mod 抢窗口过程
                _closeGuard?.Uninstall();
            }
            else if (!(_closeGuard?.WasCalledRecently(2f) ?? false))
            {
                // 还没在链子里（或者被换掉了）才装。
                // WasCalledRecently 为真说明我们正在链子里收消息（画中画 mod 可能盖在我们上面），
                // 这时再多装一次会在链子上出现两个我们 —— 所以既不能装，也不能卸。
                _closeGuard?.EnsureInstalled();
            }

            // Steam 退出也会强杀游戏进程，所以一并拦：
            //   慢巡收 Steam 主窗口（任务栏按钮消失）+ 快巡收托盘菜单弹窗
            //   （都不碰输入路径，鼠标不受影响）
            _steamCloseGuard?.SweepSteamWindows();
            _steamCloseGuard?.SweepSteamPopups();
        }
        else
        {
            _closeGuard?.Uninstall();
        }
    }

    /// <summary>
    /// 画中画 mod（iGPU Savior / Potato Mode）要切小窗了 —— 抢先让开。
    ///
    /// 它自己也接管了游戏窗口过程（PiPWindowProc / InstallPiPWindowProc），和我们那套
    /// 是同一个窗口的两套过程，撞上就会崩在 ntdll 的堆分配里。这里被它的
    /// SetPiPMode / TogglePiPMode 的前缀调用（见 PiPModeChangePatch），所以能
    /// 在它动手之前先把我们那套卸掉；两秒后由尺寸判断决定要不要装回来。
    /// </summary>
    internal void OnExternalPiPModeChange()
    {
        // 让开 3 秒：它切换时会改样式、装/卸自己的窗口过程，这段时间我们绝对不能插手
        _externalPiPUntil = Time.realtimeSinceStartup + 3f;
        _closeGuard?.Uninstall();
    }

    private float _externalPiPUntil;
    private bool _piPPatched;

    /// <summary>
    /// 现在是不是"小窗模式"。
    ///
    /// 画中画 mod（Potato Mode / iGPU Savior）会把游戏窗口缩到 ~510x480；我们的窗口过程
    /// 接管和它同时作用时，专注中按 F3 会崩（转储是堆被写坏 + 系统回调进托管代码）。
    /// 判断方式是看游戏主窗口尺寸 —— 小窗期间把接管让开，退出小窗立刻装回来。
    ///
    /// 开销：主窗口句柄是缓存好的，实际只是每 0.1 秒一次 GetWindowRect（微秒级），
    /// 不是每帧都问。窗口缩放是几十~几百毫秒的过程，0.1 秒足够在画中画动手之前让开。
    /// </summary>
    private bool IsMiniWindowMode()
    {
        // 画中画 mod 刚切换过：先无条件让开一会儿（它切换时要改窗口样式和窗口过程）
        if (Time.realtimeSinceStartup < _externalPiPUntil)
            return true;

        // 挂上了它的切换方法 → 只靠"切换事件"判断，允许切完稳定后在小窗里把接管装回来
        if (_piPPatched)
            return false;

        // 没挂上（没装那个 mod / 改了名）→ 退回按窗口尺寸判断
        var now = Time.realtimeSinceStartup;
        if (now < _nextMiniWindowCheck)
            return _isMiniWindow;

        _nextMiniWindowCheck = now + 0.1f;
        _isMiniWindow = ComputeMiniWindowMode();
        return _isMiniWindow;
    }

    private static bool ComputeMiniWindowMode()
    {
        try
        {
            var hwnd = Win32.GetCurrentProcessMainWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            return Win32.TryGetWindowSize(hwnd, out var width, out var height) &&
                   (width <= 800 || height <= 600);
        }
        catch
        {
            return false;
        }
    }

    private void TickCoreHost()
    {
        // 退出流程里不要再做任何周期性工作（扫窗口、收窗口、播语音都没意义了）。
        //
        // 但是：这次退出有可能被取消（我们自己拦下了它，或者游戏自己取消了），
        // 那时候进程还活着、Update 还在跑 —— 必须把功能恢复回来，
        // 否则整个 mod 会"静悄悄地死掉"，表现就是专注时什么都不做了。
        if (_quitting)
        {
            if (Time.realtimeSinceStartup - _quittingAt < 2f)
                return;

            _quitting = false;
            Logger.LogInfo("[Chill Clock] 退出被取消，功能已恢复");
        }

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
            // 扫窗口本身要挨个问 DWM / 进程信息，是主线程上最贵的一件周期活，
            // 所以它单独按 0.5 秒的节奏跑（晚半秒收起新开的窗口，换来的是不掉帧）。
            if (now >= _nextGuardSweep)
            {
                _nextGuardSweep = now + 0.5f;

                if (_focusActive)
                {
                    _guard.Tick();
                }
                else if (IsPomodoroSessionActive())
                {
                    // 休息阶段不再最小化普通应用，但继续关闭任务管理器。
                    _guard.TickTaskManagerOnly();
                }
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
                SetFocusFromEvent(true, "PomodoroService.OnStartWork");
            }).AddTo(_subscriptions);
            service.OnStartBreak.Subscribe(_ =>
            {
                _pomodoroSessionActive = true;
                SetFocusFromEvent(false, "PomodoroService.OnStartBreak");
            }).AddTo(_subscriptions);
            service.OnUnpause.Subscribe(type =>
                SetFocusFromEvent(type == Bulbul.PomodoroService.PomodoroType.Work,
                    "PomodoroService.OnUnpause(" + type + ")")).AddTo(_subscriptions);
            service.OnCompletePomodoro.Subscribe(_ =>
            {
                _pomodoroSessionActive = false;
                SetFocusFromEvent(false, "PomodoroService.OnCompletePomodoro");
            }).AddTo(_subscriptions);
            _subscribed = true;
            Logger.LogInfo("[Chill Clock] subscribed to PomodoroService events");
        }
        catch (Exception e)
        {
            Logger.LogWarning("[Chill Clock] event subscribe failed: " + e);
        }
    }

    /// <summary>
    /// 游戏事件驱动的专注开关。reason 只用来写日志，方便事后看清"是谁把专注关掉的"。
    /// </summary>
    internal void SetFocusFromEvent(bool active, string reason = null)
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
        Logger.LogInfo("[Chill Clock] event focus state -> " + (active ? "Work active" : "ended") +
                       (string.IsNullOrEmpty(reason) ? "" : " （来自 " + reason + "）"));
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

    /// <summary>
    /// 游戏那边有人开口（HeroineAI.PlayVoice）。**不**立刻把我们的整段作废 ——
    /// 看书时的嗯声、翻页、呼呼吹气这些小声音也走这条路，直接作废就是
    /// "讲到一半被嗯声掐掉、而且再也不接上"。交给 VoiceManager 按声音长短判定。
    /// </summary>
    internal void NotifyGameVoiceStarted()
    {
        try
        {
            _voiceManager?.NotifyGameVoiceStarted();
        }
        catch (Exception e)
        {
            Logger.LogWarning("[Chill Clock] notify game voice failed: " + e.Message);
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
    ///
    /// 点击台词只归「点击反应」这一个开关管：把"提醒语音"关掉之后，
    /// 点她照样会说扩充台词（以前这里还 AND 了 VoiceReminders，等于被一起关掉）。
    /// </summary>
    internal ClickReactionResult HandleClickReaction(Bulbul.FacilityClickHeroine.ReactionType reactionType)
    {
        if (!_clickReaction.Value)
            return ClickReactionResult.PassThrough;
        if (_voiceManager == null || !_masterEnabled.Value)
            return ClickReactionResult.PassThrough;

        // 只接管"玩家点击"，不碰她自发的 HeroineSelf
        if (reactionType != Bulbul.FacilityClickHeroine.ReactionType.Click)
        {
            // 例外：她自发的自言自语会走游戏的 VoiceManager.Stop()，把我们的音频
            // 一起掐断（用户听到的就是"我们的台词讲到一半被她的自言自语打断"）。
            // 我们正在念的时候先把这类反应压住；我们说完了它自然会照常。
            if (reactionType == Bulbul.FacilityClickHeroine.ReactionType.HeroineSelf &&
                _voiceManager.IsBusy)
            {
                return ClickReactionResult.TakeOver;
            }

            return ClickReactionResult.PassThrough;
        }

        if (!HeroineActionBridge.CanTakeOverClickReaction())
        {
            return ClickReactionResult.PassThrough;
        }

        // 她头上有"想说话"的气泡（剧情准备触发）：这时候点身体会被我们接管，
        // 点气泡却能触发剧情 —— 用户遇到的就是这个不对称。
        // 气泡亮着 = 游戏自己有话要说，我们让路，交给游戏自己的点击流程。
        if (HeroineActionBridge.IsWantingTalk())
        {
            Logger.LogInfo("[Chill Clock] 她有想说的话（气泡亮着），这次点击让给游戏");
            return ClickReactionResult.PassThrough;
        }

        // 游戏这会儿有剧情等着触发（它的点击反应已经就绪）：让路，
        // 让玩家点到的是**游戏自己的剧情**，而不是我们的扩充台词。
        Logger.LogInfo("[Chill Clock] 点击诊断: mainState=" + HeroineActionBridge.ClickMainStateName() +
                       " reactionStartReady=" + HeroineActionBridge.IsGameClickScenarioReady() +
                       " possible=" + HeroineActionBridge.IsPossibleClickReaction() +
                       " free=" + HeroineActionBridge.IsClickReactionFree() +
                       " seqBusy=" + HeroineActionBridge.IsGameSequenceBusy() +
                       " voiceBusy=" + HeroineActionBridge.IsGameVoiceBusy());
        // 注意：这里**不要**再拿"游戏剧情是否就绪"来拦我们的点击。
        // 之前加过一版，结果 `IsReactionStartReady` 在平时也是 true ——
        // 于是点击全被让路，玩家点她再也没反应。要拦"剧情待触发"得换判据。

        var state = _focusActive
            ? "Work"
            : (IsPomodoroSessionActive() ? "Break" : "Normal");

        var result = _voiceManager.PlayClick(state);
        if (result == VoiceStartResult.Started)
        {
            return ClickReactionResult.TakeOver;
        }

        // Deferred = 她还在说（我们的台词没完）。这时按游戏自己的规矩"现在不能点"，
        // 让点击流程走那个禁止反馈；既不会插新台词，也不会让游戏叠一条它自己的话。
        if (result == VoiceStartResult.Deferred)
        {
            return ClickReactionResult.Blocked;
        }

        // Skipped（冷却中 / 池子空）才让游戏走它自己的反应
        return ClickReactionResult.PassThrough;
    }

    private bool OnWantsToQuit()
    {
        var block = ShouldBlockGameExit();
        Logger.LogInfo("[Chill Clock] wantsToQuit 触发，拦截=" + block);
        if (!block)
            return true;

        Logger.LogWarning("[Chill Clock] 番茄钟会话中已拦截退出请求");
        _pendingExitVoice = true;
        return false;
    }

    /// <summary>
    /// 游戏开始退出：先把我们挂上去的原生钩子撤掉，再做别的收尾。
    ///
    /// 顺序很重要 —— Application.quitting 比 OnDestroy 早，赶在进程收尾之前
    /// 把 WH_MOUSE_LL 解开，这样就不会回调到已经卸下的代码。
    /// </summary>
    private void OnQuitting()
    {
        _quitting = true;
        _quittingAt = Time.realtimeSinceStartup;

        try
        {
            _closeGuard?.Uninstall();
            _uiHider?.RestoreAll();
            Logger.LogInfo("[Chill Clock] quitting: 已撤掉窗口过程接管");
        }
        catch (Exception e)
        {
            Logger.LogWarning("[Chill Clock] quit cleanup failed: " + e.Message);
        }
    }

    private void OnProcessExit(object sender, EventArgs e)
    {
        OnQuitting();
    }

    private void OnDestroy()
    {
        Application.wantsToQuit -= OnWantsToQuit;
        Application.quitting -= OnQuitting;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _quitting = true;
        // 只有真的在退出时才丢开语音资源：游戏中途发起又取消的"退出"也会走到 OnDestroy，
        // 那种情况下把资源丢掉会让整个 mod 之后彻底没声音（用户报过这个）。
        if (Application.isPlaying == false || _quittingAt > 0f)
        _voiceManager?.Dispose();
        _closeGuard?.Uninstall();
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

    /// <summary>
    /// 挂画中画 mod 的"切换小窗"方法：它一动，我们先把自己的窗口过程接管让开。
    /// 没装那个 mod（或改了名）就什么都不做。
    /// </summary>
    private void PatchExternalPiP()
    {
        var type = AccessTools.TypeByName("PotatoOptimization.Features.WindowStateManager");
        if (type == null)
            return;

        var patch = PatchMethod("PiPModeChangePatch", "Prefix");
        var patched = false;
        foreach (var name in new[] { "SetPiPMode", "TogglePiPMode" })
        {
            var original = AccessTools.Method(type, name);
            if (original == null)
                continue;

            try
            {
                _harmony.Patch(original, prefix: patch);
                patched = true;
            }
            catch (Exception e)
            {
                Logger.LogWarning("[Chill Clock] 挂画中画 " + name + " 失败：" + e.Message);
            }
        }

        if (patched)
            _piPPatched = true;
    }
}
