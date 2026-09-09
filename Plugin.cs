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
    public const string Version = "0.4.0";

    internal static ManualLogSource Log = null!;
    internal static Plugin Instance = null!;

    private ConfigEntry<bool> _masterEnabled = null!;
    private ConfigEntry<bool> _disableStopSkip = null!;
    private ConfigEntry<bool> _hideUiDuringFocus = null!;
    private ConfigEntry<bool> _blockGameExitOnFocus = null!;
    private WhitelistStore _store = null!;
    private WindowGuard _guard = null!;
    private FocusSessionWatcher _watcher = null!;
    private SettingsPageInjector _ui = null!;
    private FocusUiHider _uiHider = null!;
    private CloseGuard _closeGuard = null!;
    private EscKeyGuard _escGuard = null!;

    private bool _focusActive;
    private bool _pomodoroSessionActive;
    private Bulbul.PomodoroService _pomodoroServiceInstance;
    private Harmony _harmony = null!;
    private bool _loggedServiceMissing;
    private bool _subscribed;
    private CompositeDisposable _subscriptions;
    private float _nextCoreTick;
    private float _nextFocusStatusLog;

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
            value => SetConfigValue(_blockGameExitOnFocus, value));

        var hostObject = new GameObject("ChillClockHost");
        hostObject.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(hostObject);
        hostObject.AddComponent<FocusHostBehaviour>();
        Logger.LogInfo("[Chill Clock] host created in Awake");

        try
        {
            _harmony = new Harmony(Guid);
            var setup = AccessTools.Method(typeof(Bulbul.SettingUI), "Setup");
            var activate = AccessTools.Method(typeof(Bulbul.SettingUI), "Activate");
            Logger.LogInfo("Patch targets -> Setup: " + (setup != null) + ", Activate: " + (activate != null));

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
        TickCoreHost();
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
                if (now >= _nextFocusStatusLog)
                {
                    _nextFocusStatusLog = now + 10f;
                    Logger.LogInfo("[Chill Clock] focus active, sweeping windows");
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
        Logger.LogInfo("[Chill Clock] event focus state -> " + (active ? "Work active" : "ended"));
    }

    internal void SetPomodoroSessionActive(bool active)
    {
        _pomodoroSessionActive = active;
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
        return _masterEnabled.Value &&
               _blockGameExitOnFocus.Value &&
               (_focusActive || IsPomodoroSessionActive());
    }

    private bool OnWantsToQuit()
    {
        if (!_masterEnabled.Value || !_blockGameExitOnFocus.Value)
            return true;
        if (!_focusActive && !IsPomodoroSessionActive())
            return true;

        Logger.LogWarning("[Chill Clock] 番茄钟会话中已拦截退出请求");
        return false;
    }

    private void OnDestroy()
    {
        Application.wantsToQuit -= OnWantsToQuit;
        _closeGuard?.Uninstall();
        _escGuard?.Uninstall();
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
        Logger.LogInfo("Pomodoro patch " + methodName + " -> " + (original != null));
        if (original != null)
        {
            var patch = PatchMethod(patchTypeName, isPrefix ? "Prefix" : "Postfix");
            _harmony.Patch(original, prefix: isPrefix ? patch : null, postfix: isPrefix ? null : patch);
        }
    }

    private void PatchCountup(Type type, string methodName, string patchTypeName, bool isPrefix)
    {
        var original = AccessTools.Method(type, methodName);
        Logger.LogInfo("Countup patch " + methodName + " -> " + (original != null));
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
