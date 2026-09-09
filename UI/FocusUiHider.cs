using System;
using System.Collections.Generic;
using System.Reflection;
using Bulbul;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChillFocusWhitelist.UI;

/// <summary>
/// 番茄钟会话期间隐藏干扰控件：停止/跳过按钮，
/// 以及专注时主界面右侧的功能按钮列与等级图标。
/// </summary>
internal sealed class FocusUiHider
{
    private const float ReapplyInterval = 0.25f;

    private readonly List<HiddenEntry> _hidden = new List<HiddenEntry>();
    private readonly HashSet<GameObject> _hiddenTargets = new HashSet<GameObject>();

    private bool _hideStopSkip;
    private bool _hideUi;
    private bool _stateApplied;
    private float _nextApplyTime;

    public void Tick(bool hideStopSkip, bool hideUi)
    {
        var changed = hideStopSkip != _hideStopSkip || hideUi != _hideUi;
        _hideStopSkip = hideStopSkip;
        _hideUi = hideUi;

        var shouldHide = hideStopSkip || hideUi;
        if (changed)
        {
            RestoreAll();
            _nextApplyTime = 0f;
        }

        if (!shouldHide)
            return;

        if (_stateApplied && Time.realtimeSinceStartup < _nextApplyTime)
            return;
        _nextApplyTime = Time.realtimeSinceStartup + ReapplyInterval;

        try
        {
            if (hideStopSkip)
                HideStopAndSkip();
            if (hideUi)
                HideRightSideUi();
            _stateApplied = true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] focus UI hide failed: " + e.Message);
        }
    }

    public void RestoreAll()
    {
        foreach (var entry in _hidden)
        {
            entry.Restore();
            _hiddenTargets.Remove(entry.Target);
        }
        _hidden.Clear();
        _stateApplied = false;
    }

    private void HideStopAndSkip()
    {
        PruneDeadTargets();

        HideActiveObjectsByName("PomodoroResetButton");
        HideActiveObjectsByName("SkipButton");
        HideActiveObjectsByName("PomodoroNextButton");
        HidePomodoroButtonsByText();

        PomodoroTimerUI[] uis;
        try
        {
            uis = Resources.FindObjectsOfTypeAll<PomodoroTimerUI>();
        }
        catch
        {
            return;
        }

        foreach (var ui in uis)
        {
            if (ui == null)
                continue;

            var hiddenByField = false;
            hiddenByField |= HideReflectedObject(ui, "_resetButton");
            hiddenByField |= HideReflectedObject(ui, "_skipPomodoroButton");
            if (hiddenByField)
                continue;

            HideDescendantByName(ui.transform, "PomodoroResetButton");
            HideDescendantByName(ui.transform, "SkipButton");
            HideDescendantByName(ui.transform, "PomodoroNextButton");
        }

        PomodoroTimerStateView[] stateViews;
        try
        {
            stateViews = Resources.FindObjectsOfTypeAll<PomodoroTimerStateView>();
        }
        catch
        {
            return;
        }

        foreach (var stateView in stateViews)
        {
            if (stateView == null)
                continue;

            HideReflectedObject(stateView, "_skipButtonObject");
            HideDescendantByName(stateView.transform, "PomodoroResetButton");
            HideDescendantByName(stateView.transform, "SkipButton");
            HideDescendantByName(stateView.transform, "PomodoroNextButton");
        }
    }

    private bool HideReflectedObject(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
            return false;

        object value;
        try
        {
            value = field.GetValue(instance);
        }
        catch
        {
            return false;
        }

        return HideUnityObject(value);
    }

    private void HideRightSideUi()
    {
        PruneDeadTargets();

        var rightIcons = GameObject.Find(
            "Paremt/PCPlatform/Canvas/UI/MostFrontArea/RightIcons");
        if (rightIcons == null)
            rightIcons = FindActiveByExactName("RightIcons");
        if (rightIcons != null)
            HideTarget(rightIcons);

        // ChillPatcherLite 重排后上半部分按钮位于 TopIcons，与 RightIcons 分开。
        var topIcons = GameObject.Find(
            "Paremt/PCPlatform/Canvas/UI/MostFrontArea/TopIcons");
        if (topIcons == null)
            topIcons = FindActiveByExactName("TopIcons");
        if (topIcons != null)
            HideTarget(topIcons);

        var names = new[]
        {
            "UI_FacilityPlayerLevel",
            "FailityLevel",
            "IconNote_Button",
            "IconTodo_Button",
            "IconCalender_Button",
            "IconHabit_Button",
            "IconSetting_Button",
            "IconExit_Button"
        };
        foreach (var name in names)
        {
            var target = FindActiveByExactName(name);
            if (target != null)
                HideTarget(target);
        }
    }

    private static GameObject FindActiveByExactName(string name)
    {
        Transform[] all;
        try
        {
            all = Resources.FindObjectsOfTypeAll<Transform>();
        }
        catch
        {
            return null;
        }

        foreach (var transform in all)
        {
            if (transform == null || !transform.gameObject.activeInHierarchy)
                continue;
            if (transform.name == name)
                return transform.gameObject;
        }

        return null;
    }

    private void HideDescendantByName(Transform root, string name)
    {
        if (root == null)
            return;

        foreach (Transform child in root)
        {
            if (child.name == name)
                HideTarget(child.gameObject);
            HideDescendantByName(child, name);
        }
    }

    private bool HideUnityObject(object value)
    {
        if (value is GameObject gameObject)
        {
            HideTarget(gameObject);
            return true;
        }

        if (value is Component component)
        {
            if (component != null)
                HideTarget(component.gameObject);
            return true;
        }

        return false;
    }

    private void HideTarget(GameObject target)
    {
        if (target == null || !target.activeInHierarchy)
            return;
        if (!_hiddenTargets.Add(target))
            return;

        var group = target.GetComponent<CanvasGroup>();
        if (group == null)
            group = target.AddComponent<CanvasGroup>();

        _hidden.Add(new HiddenEntry(target, group));
    }

    private void PruneDeadTargets()
    {
        for (var i = _hidden.Count - 1; i >= 0; i--)
        {
            if (_hidden[i].Target == null)
            {
                _hiddenTargets.Remove(_hidden[i].Target);
                _hidden.RemoveAt(i);
            }
        }
    }

    private void HideActiveObjectsByName(string name)
    {
        Transform[] all;
        try
        {
            all = Resources.FindObjectsOfTypeAll<Transform>();
        }
        catch
        {
            return;
        }

        foreach (var transform in all)
        {
            if (transform == null || !transform.gameObject.activeInHierarchy)
                continue;
            if (transform.name == name)
                HideTarget(transform.gameObject);
        }
    }

    private void HidePomodoroButtonsByText()
    {
        TMP_Text[] texts;
        try
        {
            texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
        }
        catch
        {
            return;
        }

        foreach (var text in texts)
        {
            if (text == null || !text.gameObject.activeInHierarchy)
                continue;
            if (!IsPomodoroControlText(text.text))
                continue;
            if (!IsUnderPomodoro(text.transform))
                continue;

            var button = text.GetComponentInParent<Button>(true);
            if (button != null && button.gameObject.activeInHierarchy)
                HideTarget(button.gameObject);
            else
                HideTarget(text.gameObject);
        }
    }

    private static bool IsPomodoroControlText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.Length == 0)
            return false;

        string[] exact =
        {
            "跳过", "结束", "停止",
            "スキップ", "終了", "停止",
            "Skip", "End", "Stop"
        };
        foreach (var candidate in exact)
        {
            if (string.Equals(text, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return text.IndexOf("结束计时", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("終了する", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("Skip Timer", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsUnderPomodoro(Transform transform)
    {
        var current = transform;
        while (current != null)
        {
            if (current.name.IndexOf("Pomodoro", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            current = current.parent;
        }
        return false;
    }

    private sealed class HiddenEntry
    {
        private readonly float _originalAlpha;
        private readonly bool _originalInteractable;
        private readonly bool _originalBlocksRaycasts;
        private readonly CanvasGroup _group;
        private readonly GameObject _target;

        public HiddenEntry(GameObject target, CanvasGroup group)
        {
            _target = target;
            _group = group;
            _originalAlpha = group.alpha;
            _originalInteractable = group.interactable;
            _originalBlocksRaycasts = group.blocksRaycasts;

            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        public GameObject Target
        {
            get { return _target; }
        }

        public void Restore()
        {
            if (_target == null || _group == null)
                return;

            _group.alpha = _originalAlpha;
            _group.interactable = _originalInteractable;
            _group.blocksRaycasts = _originalBlocksRaycasts;
        }
    }
}
