using System;
using Bulbul;
using NestopiSystem.DIContainers;

namespace ChillFocusWhitelist.Core;

public sealed class FocusSessionWatcher
{
    private PomodoroService _service;
    private int _nextAttemptTime;

    public PomodoroService Service
    {
        get { return _service; }
    }

    public bool TryGetWorkActive(out bool active)
    {
        active = false;

        if (_service == null)
        {
            var now = Environment.TickCount;
            if (now < _nextAttemptTime)
                return false;

            _nextAttemptTime = now + 1000;
            try
            {
                _service = ProjectLifetimeScope.Resolve<PomodoroService>();
            }
            catch
            {
                // 场景尚未完成 DI 初始化，稍后重试。
                return false;
            }

            if (_service == null)
                return false;
        }

        try
        {
            // 轮询只负责“开启”：当前处于 Work 计时/暂停都算专注时段。
            active = _service.IsCurrentWorking() ||
                     (_service.CurrentPomodoroType == PomodoroService.PomodoroType.Work &&
                      _service.IsTimerRunning());
            return true;
        }
        catch
        {
            _service = null;
            return false;
        }
    }
}
