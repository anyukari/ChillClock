using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Bulbul;
using ChillFocusWhitelist.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChillFocusWhitelist.UI;

internal sealed class SettingsPageInjector
{
    private const float PollInterval = 0.5f;
    private const float RowWidth = 1260f;
    private const float RowHeight = 60.9f;

    private readonly WhitelistStore _store;
    private readonly Func<bool> _getMasterEnabled;
    private readonly Action<bool> _setMasterEnabled;

    private SettingUI _settingUi;
    private bool _pageBuilt;
    private bool _dirty = true;
    private bool _wasActive;
    private float _nextPollTime;
    private float _nextForeignHookTime;
    private bool _pickerMode;

    private GameObject _pageRoot;
    private GameObject _pickerRoot;
    private GameObject _tabObject;
    private GameObject _scrollContent;
    private GameObject _pickerContent;
    private GameObject _toggleTemplate;
    private string _pendingPickedFile;
    private GameObject _toast;
    private float _rowWidth = RowWidth;
    private readonly HashSet<Button> _hookedForeignButtons = new HashSet<Button>();

    public static SettingsPageInjector Active { get; private set; }

    public SettingsPageInjector(
        WhitelistStore store,
        Func<bool> getMasterEnabled,
        Action<bool> setMasterEnabled)
    {
        _store = store;
        _getMasterEnabled = getMasterEnabled;
        _setMasterEnabled = setMasterEnabled;
        Active = this;
    }

    public void EnsureBuilt(SettingUI ui)
    {
        if (ui == null || (_pageBuilt && ReferenceEquals(_settingUi, ui)))
            return;

        if (_pageBuilt)
            ResetBinding();

        _settingUi = ui;
        Plugin.Log.LogInfo("[Chill Clock UI] building page");
        BuildPage(ui);
        _pageBuilt = true;
        _wasActive = ui.gameObject.activeInHierarchy;
        RebuildRows();

        if (ui.GetComponent<FocusUiDriver>() == null)
            ui.gameObject.AddComponent<FocusUiDriver>();

        Plugin.Log.LogInfo("[Chill Clock UI] page built");
    }

    public void OnActivated(SettingUI ui)
    {
        if (!_pageBuilt || !ReferenceEquals(_settingUi, ui))
            return;

        _wasActive = true;
        HidePicker();
        ForceGeneralDefault();
        ApplyPendingFile();
        HookForeignTabs();
        RequestRebuild();
    }

    public void Tick()
    {
        try
        {
            if (_pageBuilt)
            {
                if (_settingUi == null)
                {
                    ResetBinding();
                    return;
                }

                HandleActiveEdge();
                ApplyPendingFile();
                if (_dirty)
                    RebuildRows();

                if (UnityEngine.Time.realtimeSinceStartup >= _nextForeignHookTime)
                {
                    _nextForeignHookTime = UnityEngine.Time.realtimeSinceStartup + 2f;
                    HookForeignTabs();
                }
                return;
            }

            if (UnityEngine.Time.realtimeSinceStartup < _nextPollTime)
                return;
            _nextPollTime = UnityEngine.Time.realtimeSinceStartup + PollInterval;

            TryFindAndBuild();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock UI] Tick failed: " + e);
            ResetBinding();
        }
    }

    private void TryFindAndBuild()
    {
        var candidates = Resources.FindObjectsOfTypeAll<SettingUI>();
        foreach (var candidate in candidates)
        {
            if (ReadField<GameObject>(candidate, "_generalParent") == null)
                continue;
            if (ReadField<InteractableUI>(candidate, "_creditsInteractableUI") == null)
                continue;

            Plugin.Log.LogInfo("[Chill Clock UI] SettingUI found: " + candidate.name);
            EnsureBuilt(candidate);
            return;
        }
    }

    private void BuildPage(SettingUI ui)
    {
        var generalParent = ReadField<GameObject>(ui, "_generalParent");
        var credits = ReadField<InteractableUI>(ui, "_creditsInteractableUI");
        if (generalParent == null || credits == null)
            throw new InvalidOperationException("General/Credits page fields missing");

        _toggleTemplate = FindToggleTemplate(ui);
        Plugin.Log.LogInfo("[Chill Clock UI] toggle template found: " + (_toggleTemplate != null));

        _tabObject = Object.Instantiate(credits.gameObject);
        _tabObject.name = "ChillClockTab";
        _tabObject.transform.SetParent(credits.transform.parent, false);
        _tabObject.transform.SetSiblingIndex(credits.transform.GetSiblingIndex() + 1);
        _tabObject.GetComponent<InteractableUI>()?.Setup();

        var tabButton = _tabObject.GetComponent<Button>();
        if (tabButton != null)
        {
            tabButton.onClick.RemoveAllListeners();
            tabButton.onClick.AddListener(ShowOurPage);
        }

        ClearLocalizersRecursive(_tabObject);
        foreach (var tabText in _tabObject.GetComponentsInChildren<TMP_Text>(true))
            tabText.text = "Chill Clock";

        _pageRoot = Object.Instantiate(generalParent);
        _pageRoot.name = "ChillClockPage";
        _pageRoot.transform.SetParent(generalParent.transform.parent, false);
        _pageRoot.SetActive(false);

        var pageTitle = _pageRoot.transform.Find("Title")?.GetComponent<TMP_Text>();
        if (pageTitle != null)
            Object.Destroy(pageTitle.gameObject);

        var scrollRect = _pageRoot.GetComponentInChildren<ScrollRect>(true);
        _scrollContent = scrollRect?.content?.gameObject;
        if (_scrollContent == null)
            throw new InvalidOperationException("No ScrollRect content in cloned page");

        TrimToScrollRect(_pageRoot.transform, scrollRect.transform);

        ClearChildren(_scrollContent.transform);
        ConfigureContentLayout(scrollRect);
        DisableInitButtons(_pageRoot);

        HookNativeTabButtons(ui);
        HookForeignTabs();
    }

    private void RebuildRows()
    {
        _dirty = false;
        if (_scrollContent == null || _settingUi == null)
            return;

        RefreshUiLanguage();
        ClearChildren(_scrollContent.transform);

        _rowWidth = RowWidth;

        if (_pickerMode)
        {
            BuildPickerRows();
            return;
        }

        // 1. 总开关固定在页面最顶部。
        AddChild(CreateToggleRow(
            LocalizedText.Pick("启用 Chill Clock", "Enable Chill Clock", "Chill Clock を有効化"),
            _getMasterEnabled(),
            _setMasterEnabled));

        // 2. 添加入口。
        AddChild(CreateActionRow(
            LocalizedText.Pick("从文件添加应用", "Add App from File", "ファイルからアプリ追加"),
            LocalizedText.Pick("选择程序", "Choose .exe", "ファイルを選択"),
            PickExecutable));

        AddChild(CreateActionRow(
            LocalizedText.Pick("从当前窗口添加应用", "Add App from Windows", "ウィンドウからアプリ追加"),
            LocalizedText.Pick("打开窗口列表", "Window List", "ウィンドウ一覧"),
            OpenWindowPicker));

        // 4. 白名单列表区（图标在每一行的左侧）。
        AddChild(CreateSectionRow(LocalizedText.Pick("白名单应用", "Whitelisted Apps", "ホワイトリスト")));
        AddChild(CreateDividerRow());

        foreach (var entry in _store.Entries)
        {
            var row = CreateAppEntryRow(entry);
            if (row != null)
                AddChild(row);
        }

        if (_store.Entries.Count == 0)
        {
            AddChild(CreateSectionRow(
                LocalizedText.Pick("（暂无白名单应用）", "(No apps yet)", "（ホワイトリストにアプリがありません）")));
        }

        ForceLayoutRebuild(_scrollContent);
    }

    private static void ForceLayoutRebuild(GameObject contentRoot)
    {
        if (contentRoot == null)
            return;
        LayoutRebuilder.ForceRebuildLayoutImmediate(contentRoot.GetComponent<RectTransform>());
    }

    private GameObject CreateAppEntryRow(string entry)
    {
        var row = CloneTemplate("Entry_" + entry, Path.GetFileNameWithoutExtension(entry.Trim().Trim('"')));
        if (row == null)
            return null;

        var buttons = row.GetComponentsInChildren<Button>(true).ToList();
        var onButton = buttons.FirstOrDefault(b => b.name.Contains("OnButton"));
        var offButton = buttons.FirstOrDefault(b => b.name.Contains("OffButton"));
        if (onButton == null || offButton == null)
        {
            Object.Destroy(row);
            return null;
        }

        onButton.gameObject.SetActive(false);
        offButton.onClick.RemoveAllListeners();
        offButton.interactable = true;
        SetButtonText(offButton, LocalizedText.Pick("删除", "Delete", "削除"));
        offButton.onClick.AddListener(() =>
        {
            _store.Remove(entry);
            RequestRebuild();
        });

        var rowTitle = FindRowTitle(row.transform);
        var titleRect = rowTitle?.GetComponent<RectTransform>();
        var originalTitleX = titleRect != null ? titleRect.anchoredPosition.x : 120f;

        var iconTexture = AppIcon.LoadTexture(entry);
        if (iconTexture != null)
        {
            var iconObject = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconObject.transform.SetParent(row.transform, false);
            var iconImage = iconObject.GetComponent<Image>();
            iconImage.sprite = Sprite.Create(iconTexture, new Rect(0, 0, iconTexture.width, iconTexture.height),
                new Vector2(0.5f, 0.5f));
            iconImage.raycastTarget = false;
            iconImage.preserveAspect = true;
            var iconRect = iconImage.rectTransform;
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(originalTitleX, 0f);
            iconRect.sizeDelta = new Vector2(42f, 42f);
        }

        if (titleRect != null && iconTexture != null)
        {
            titleRect.anchoredPosition = new Vector2(originalTitleX + 58f, titleRect.anchoredPosition.y);
        }

        return row;
    }

    private static void PlaceIconLeftOfTitle(GameObject row, RectTransform iconRect, float size)
    {
        var title = FindRowTitle(row.transform);
        var titleRect = title?.GetComponent<RectTransform>();

        float titleX = titleRect != null ? titleRect.anchoredPosition.x : 120f;
        var anchorX = titleRect != null ? titleRect.anchorMin.x : 0f;
        iconRect.anchorMin = new Vector2(anchorX, 0.5f);
        iconRect.anchorMax = new Vector2(anchorX, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = new Vector2(Mathf.Max(8f, titleX - size - 14f), 0f);
        iconRect.sizeDelta = new Vector2(size, size);
    }

    private void OpenWindowPicker()
    {
        _pickerMode = true;
        RequestRebuild();
    }

    private void BuildPickerRows()
    {
        RefreshUiLanguage();
        AddChild(CreateActionRow(
            LocalizedText.Pick("从当前窗口添加应用", "Add App from Windows", "ウィンドウからアプリ追加"),
            LocalizedText.Pick("返回设置", "Back to Settings", "設定に戻る"),
            HidePicker));

        var candidates = WindowCandidates.Enumerate();
        if (candidates.Count == 0)
        {
            AddChild(CreateSectionRow(
                LocalizedText.Pick("（没有检测到可添加的应用）", "(No apps found)", "（追加できるアプリが見つかりません）")));
            return;
        }

        foreach (var candidate in candidates)
        {
            var row = CreatePickerActionRow(candidate, LocalizedText.Pick("添加", "Add", "追加"), () =>
            {
                if (_store.TryAdd(candidate.Path))
                {
                    _pickerMode = false;
                    RequestRebuild();
                }
                else
                {
                    ShowToast(LocalizedText.Pick(
                        "已在白名单中：" + Path.GetFileNameWithoutExtension(candidate.Name),
                        "Already whitelisted: " + Path.GetFileNameWithoutExtension(candidate.Name),
                        "ホワイトリスト登録済み: " + Path.GetFileNameWithoutExtension(candidate.Name)));
                }
            });
            if (row != null)
                AddChild(row);
        }

        ForceLayoutRebuild(_scrollContent);
    }

    private void RefreshUiLanguage()
    {
        try
        {
            var supplier = ReadField<LanguageSupplier>(_settingUi, "_languageSupplier");
            if (supplier != null)
                LocalizedText.SetLanguage(supplier.Get());
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] language read failed: " + e.Message);
        }
    }

    private static void ForceVisible(GameObject root)
    {
        if (root == null)
            return;

        foreach (var group in root.GetComponentsInChildren<CanvasGroup>(true))
        {
            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;
        }
    }

    private void RebuildPickerRows()
    {
        if (_pickerContent == null)
            return;

        ClearChildren(_pickerContent.transform);
        var candidates = WindowCandidates.Enumerate();

        AddPickerChild(CreatePickerActionRow(
            LocalizedText.Pick("关闭列表", "Close", "リストを閉じる"),
            LocalizedText.Pick("关闭", "Close", "閉じる"),
            HidePicker));

        if (candidates.Count == 0)
        {
            AddPickerChild(CreateSectionRow(
                LocalizedText.Pick("（没有检测到窗口）", "(no windows detected)", "（ウィンドウが見つかりません）")));
            ForceLayoutRebuild(_pickerContent);
            return;
        }

        foreach (var candidate in candidates)
        {
            var row = CreatePickerActionRow(candidate, LocalizedText.Pick("添加", "Add", "追加"), () =>
            {
                _store.Add(candidate.Path);
                HidePicker();
                RequestRebuild();
            });
            AddPickerChild(row);
        }

        ForceLayoutRebuild(_pickerContent);
    }

    private GameObject CreatePickerActionRow(string title, string buttonLabel, Action onClick)
    {
        return CreateActionRow(title, buttonLabel, onClick);
    }

    private GameObject CreatePickerActionRow(AppWindowCandidate candidate, string buttonLabel, Action onClick)
    {
        var displayName = string.IsNullOrWhiteSpace(candidate.Name)
            ? candidate.Title
            : Path.GetFileNameWithoutExtension(candidate.Name);
        if (string.IsNullOrWhiteSpace(displayName) ||
            string.Equals(displayName, "null", StringComparison.OrdinalIgnoreCase))
        {
            displayName = candidate.Title;
        }

        var row = CreateActionRow(displayName, buttonLabel, onClick);
        if (row != null && !string.IsNullOrEmpty(candidate.Path))
        {
            var iconTexture = AppIcon.LoadTexture(candidate.Path);
            if (iconTexture != null)
            {
                var iconObject = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconObject.transform.SetParent(row.transform, false);
                var image = iconObject.GetComponent<Image>();
                image.sprite = Sprite.Create(iconTexture, new Rect(0, 0, iconTexture.width, iconTexture.height),
                    new Vector2(0.5f, 0.5f));
                image.raycastTarget = false;
                image.preserveAspect = true;
                PlaceIconLeftOfTitle(row, image.rectTransform, 40f);
            }
        }

        return row;
    }

    private void HidePicker()
    {
        if (_pickerMode)
        {
            _pickerMode = false;
            RequestRebuild();
        }
    }

    private void ShowOurPage()
    {
        PlayClickSound();

        foreach (var pair in EnumerateTabFieldPairs())
        {
            (pair.ParentField.GetValue(_settingUi) as GameObject)?.SetActive(false);
            (pair.ButtonField.GetValue(_settingUi) as InteractableUI)?.DeactivateUseUI(false);
        }

        DeactivateForeignPageObjects();
        DeactivateForeignTabHighlight();

        if (_pageRoot != null)
            _pageRoot.SetActive(true);
        ActivateInteractable(_tabObject);
        HookForeignTabs();
    }

    private void HideOurPage()
    {
        if (_pageRoot != null)
            _pageRoot.SetActive(false);
        HidePicker();
        DeactivateInteractable(_tabObject);
    }

    private void ForceGeneralDefault()
    {
        if (_pageRoot != null)
            _pageRoot.SetActive(false);
        HidePicker();
        DeactivateInteractable(_tabObject);

        var generalButton = ReadField<InteractableUI>(_settingUi, "_generalInteractableUI");
        var generalParent = ReadField<GameObject>(_settingUi, "_generalParent");

        foreach (var pair in EnumerateTabFieldPairs())
        {
            var parent = pair.ParentField.GetValue(_settingUi) as GameObject;
            var button = pair.ButtonField.GetValue(_settingUi) as InteractableUI;
            if (parent != null && !ReferenceEquals(parent, generalParent))
                parent.SetActive(false);
            if (button != null && !ReferenceEquals(button, generalButton))
                button.DeactivateUseUI(false);
        }

        if (generalButton != null)
            generalButton.ActivateUseUI(false);
        if (generalParent != null)
            generalParent.SetActive(true);
    }

    private void DeactivateForeignPageObjects()
    {
        if (_pageRoot == null)
            return;

        var container = _pageRoot.transform.parent;
        if (container == null)
            return;

        foreach (Transform child in container)
        {
            if (child == _pageRoot.transform || child == _pickerRoot?.transform)
                continue;
            if (child.name == "ModSettingsContent")
                child.gameObject.SetActive(false);
        }
    }

    private void DeactivateForeignTabHighlight()
    {
        if (_tabObject == null)
            return;

        var tabBar = _tabObject.transform.parent;
        if (tabBar == null)
            return;

        foreach (Transform child in tabBar)
        {
            if (child == _tabObject.transform)
                continue;
            if (child.name == "ModSettingsTabButton")
                (child.GetComponent<InteractableUI>())?.DeactivateUseUI(false);
        }
    }

    private void HookNativeTabButtons(SettingUI ui)
    {
        foreach (var pair in EnumerateTabFieldPairs())
        {
            var button = pair.ButtonField.GetValue(ui) as InteractableUI;
            var nativeButton = button?.GetComponent<Button>();
            if (nativeButton != null)
            {
                nativeButton.onClick.RemoveListener(HideOurPage);
                nativeButton.onClick.AddListener(HideOurPage);
            }
        }
    }

    private void HookForeignTabs()
    {
        if (_tabObject == null)
            return;

        var tabBar = _tabObject.transform.parent;
        if (tabBar == null)
            return;

        foreach (Transform child in tabBar)
        {
            if (child == _tabObject.transform)
                continue;

            var button = child.GetComponent<Button>();
            if (button == null || _hookedForeignButtons.Contains(button))
                continue;

            button.onClick.AddListener(HideOurPage);
            _hookedForeignButtons.Add(button);
        }
    }

    private void HandleActiveEdge()
    {
        if (_settingUi == null)
            return;

        var active = _settingUi.gameObject.activeInHierarchy;
        if (active == _wasActive)
            return;

        _wasActive = active;
        if (active)
        {
            ForceGeneralDefault();
            HookForeignTabs();
        }
    }

    private GameObject CreateToggleRow(string label, bool initialValue, Action<bool> onChanged)
    {
        var row = CloneTemplate("Toggle_" + label, label);
        if (row == null)
            return null;

        var buttons = row.GetComponentsInChildren<Button>(true).ToList();
        var onButton = buttons.FirstOrDefault(b => b.name.Contains("OnButton"));
        var offButton = buttons.FirstOrDefault(b => b.name.Contains("OffButton"));
        if (onButton == null || offButton == null)
        {
            Object.Destroy(row);
            return null;
        }

        onButton.onClick.RemoveAllListeners();
        offButton.onClick.RemoveAllListeners();

        Action<bool> applyState = null;
        applyState = state =>
        {
            onButton.interactable = !state;
            offButton.interactable = state;

            var onUi = onButton.GetComponent<InteractableUI>();
            var offUi = offButton.GetComponent<InteractableUI>();
            if (state)
            {
                onUi?.ActivateUseUI(false);
                offUi?.DeactivateUseUI(false);
            }
            else
            {
                onUi?.DeactivateUseUI(false);
                offUi?.ActivateUseUI(false);
            }
        };

        onButton.onClick.AddListener(() =>
        {
            if (onButton.interactable)
            {
                applyState(true);
                PlayClickSound();
                onChanged?.Invoke(true);
            }
        });
        offButton.onClick.AddListener(() =>
        {
            if (offButton.interactable)
            {
                applyState(false);
                PlayClickSound();
                onChanged?.Invoke(false);
            }
        });

        applyState(initialValue);
        return row;
    }

    private GameObject CreateSectionRow(string text)
    {
        var row = CloneTemplate("Section_" + text, text);
        if (row == null)
            return null;

        foreach (var button in row.GetComponentsInChildren<Button>(true))
            button.gameObject.SetActive(false);
        return row;
    }

    private GameObject CreateDividerRow()
    {
        var row = new GameObject("Divider", typeof(RectTransform), typeof(Image));
        row.transform.SetParent(null, false);
        var image = row.GetComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0.28f);
        image.raycastTarget = false;

        var rect = row.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(_rowWidth - 48f, 2f);

        var element = row.AddComponent<LayoutElement>();
        element.minHeight = 2f;
        element.preferredHeight = 2f;
        element.minWidth = _rowWidth;
        element.preferredWidth = _rowWidth;
        return row;
    }

    private void AddIconRows(IReadOnlyList<string> entries)
    {
        const float cellSize = 64f;
        const float spacing = 12f;
        var perRow = Mathf.Max(1, Mathf.FloorToInt((_rowWidth - 24f) / (cellSize + spacing)));

        GameObject row = null;
        var countInRow = 0;

        foreach (var entry in entries)
        {
            var iconTexture = AppIcon.LoadTexture(entry);
            if (iconTexture == null)
                continue;

            if (row == null || countInRow >= perRow)
            {
                row = CreateIconRow(perRow);
                AddChild(row);
                countInRow = 0;
            }

            var iconObject = new GameObject("AppIcon", typeof(RectTransform), typeof(Image));
            iconObject.transform.SetParent(row.transform, false);
            var image = iconObject.GetComponent<Image>();
            image.sprite = Sprite.Create(iconTexture, new Rect(0, 0, iconTexture.width, iconTexture.height),
                new Vector2(0.5f, 0.5f));
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.rectTransform.sizeDelta = new Vector2(56f, 56f);
            countInRow++;
        }
    }

    private GameObject CreateIconRow(int maxIcons)
    {
        var row = new GameObject("IconRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(null, false);

        var rect = row.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(_rowWidth, 72f);

        var layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 12f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        var element = row.GetComponent<LayoutElement>();
        element.ignoreLayout = false;
        element.minWidth = _rowWidth;
        element.preferredWidth = _rowWidth;
        element.minHeight = 72f;
        element.preferredHeight = 72f;
        return row;
    }

    private GameObject CreateActionRow(string title, string buttonLabel, Action onClick)
    {
        var row = CloneTemplate("Action_" + title, title);
        if (row == null)
            return null;

        var buttons = row.GetComponentsInChildren<Button>(true).ToList();
        var onButton = buttons.FirstOrDefault(b => b.name.Contains("OnButton"));
        var offButton = buttons.FirstOrDefault(b => b.name.Contains("OffButton"));
        if (onButton == null || offButton == null)
        {
            Object.Destroy(row);
            return null;
        }

        onButton.gameObject.SetActive(false);
        offButton.onClick.RemoveAllListeners();
        offButton.interactable = true;
        SetButtonText(offButton, buttonLabel);
        offButton.onClick.AddListener(() =>
        {
            PlayClickSound();
            onClick?.Invoke();
        });

        var ui = offButton.GetComponent<InteractableUI>();
        if (ui != null)
            ui.ActivateUseUI(false);

        return row;
    }

    private GameObject CloneTemplate(string newName, string title)
    {
        if (_toggleTemplate == null)
            return null;

        var row = Object.Instantiate(_toggleTemplate.gameObject);
        row.name = newName;
        row.SetActive(true);
        ClearLocalizersRecursive(row);

        var rowTitle = FindRowTitle(row.transform);
        if (rowTitle != null)
            rowTitle.text = title;

        var rect = row.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(_rowWidth, RowHeight);
        }

        var element = row.GetComponent<LayoutElement>();
        if (element == null)
            element = row.AddComponent<LayoutElement>();
        element.ignoreLayout = false;
        element.minWidth = _rowWidth;
        element.preferredWidth = _rowWidth;
        element.minHeight = RowHeight;
        element.preferredHeight = RowHeight;
        element.flexibleWidth = 0f;
        element.flexibleHeight = 0f;

        return row;
    }

    private void AddChild(GameObject child)
    {
        if (child != null && _scrollContent != null)
            child.transform.SetParent(_scrollContent.transform, false);
    }

    private void AddPickerChild(GameObject child)
    {
        if (child != null && _pickerContent != null)
            child.transform.SetParent(_pickerContent.transform, false);
    }

    private static GameObject FindToggleTemplate(SettingUI ui)
    {
        foreach (var parentName in new[] { "_generalParent", "_graphicParent", "_audioParent" })
        {
            var parent = ReadField<GameObject>(ui, parentName);
            var content = parent?.GetComponentInChildren<ScrollRect>(true)?.content;
            if (content == null)
                continue;

            foreach (Transform child in content)
            {
                var buttons = child.GetComponentsInChildren<Button>(true);
                if (buttons.Any(b => b.name.Contains("OnButton")) &&
                    buttons.Any(b => b.name.Contains("OffButton")))
                {
                    return child.gameObject;
                }
            }
        }

        return null;
    }

    private static TMP_Text FindRowTitle(Transform row)
    {
        var exact = row.Find("TitleText")?.GetComponent<TMP_Text>();
        if (exact != null)
            return exact;

        return row.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(t => t.GetComponentInParent<Button>() == null);
    }

    private static void SetButtonText(Button button, string text)
    {
        var textComponent = button.GetComponentInChildren<TMP_Text>(true);
        if (textComponent == null)
            return;

        ClearLocalizers(textComponent.gameObject);
        textComponent.text = text;
    }

    private static void ClearChildren(Transform parent)
    {
        if (parent == null)
            return;

        foreach (var child in parent.Cast<Transform>().ToList())
            Object.Destroy(child.gameObject);
    }

    private static void DisableInitButtons(GameObject root)
    {
        if (root == null)
            return;

        foreach (var button in root.GetComponentsInChildren<Button>(true))
        {
            if (button.name.EndsWith("InitButton", StringComparison.OrdinalIgnoreCase))
                button.gameObject.SetActive(false);
        }
    }

    private static void ClearLocalizers(GameObject gameObject)
    {
        if (gameObject == null)
            return;

        foreach (var component in gameObject.GetComponents<Component>())
        {
            if (component == null)
                continue;

            var typeName = component.GetType().FullName ?? string.Empty;
            if (typeName.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0)
                Object.Destroy(component);
        }
    }

    private static void ClearLocalizersRecursive(GameObject root)
    {
        if (root == null)
            return;

        foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
            ClearLocalizers(text.gameObject);
    }

    private static void TrimToScrollRect(Transform pageRoot, Transform scrollRect)
    {
        // 找出直接子容器，只保留 ScrollRect 所在分支，其余标题/背景装饰全部关闭。
        Transform rootContainer = scrollRect;
        while (rootContainer.parent != null && rootContainer.parent != pageRoot)
            rootContainer = rootContainer.parent;

        if (rootContainer == pageRoot)
            return;

        foreach (Transform child in pageRoot)
        {
            if (child != rootContainer)
                child.gameObject.SetActive(false);
        }

        if (rootContainer == scrollRect)
            return;

        // 容器里若还有滚动区之外的头部/关闭按钮，也关闭。
        var chain = new HashSet<Transform>();
        var cursor = scrollRect;
        while (cursor != null && cursor != rootContainer)
        {
            chain.Add(cursor);
            cursor = cursor.parent;
        }

        foreach (Transform child in rootContainer)
        {
            if (!chain.Contains(child))
                child.gameObject.SetActive(false);
        }
    }

    private static void ConfigureContentLayout(ScrollRect scrollRect)
    {
        if (scrollRect == null || scrollRect.content == null)
            return;

        var content = scrollRect.content;
        content.anchorMin = new Vector2(content.anchorMin.x, 1f);
        content.anchorMax = new Vector2(content.anchorMax.x, 0f);
        content.pivot = new Vector2(0.5f, 1f);

        var layout = content.GetComponent<VerticalLayoutGroup>();
        if (layout == null)
            layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 0f;
        layout.padding = new RectOffset(24, 24, 8, 24);
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = false;
        layout.reverseArrangement = false;

        var fitter = content.GetComponent<ContentSizeFitter>();
        if (fitter == null)
            fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        if (scrollRect.viewport != null)
        {
            var viewport = scrollRect.viewport;
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = new Vector2(0f, 0f);
            if (viewport.GetComponent<RectMask2D>() == null)
                viewport.gameObject.AddComponent<RectMask2D>();
        }
    }

    private void PickExecutable()
    {
        var thread = new Thread(() =>
        {
            _pendingPickedFile = ModernFileDialog.PickExecutablePath();
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private void ApplyPendingFile()
    {
        if (_pendingPickedFile == null)
            return;

        var path = _pendingPickedFile;
        _pendingPickedFile = null;
        if (path.Length == 0)
            return;

        if (_store.TryAdd(path))
        {
            RequestRebuild();
        }
        else
        {
            ShowToast(LocalizedText.Pick(
                "已在白名单中：" + Path.GetFileNameWithoutExtension(path),
                "Already whitelisted: " + Path.GetFileNameWithoutExtension(path),
                "ホワイトリスト登録済み: " + Path.GetFileNameWithoutExtension(path)));
        }
    }

    private void ShowToast(string message)
    {
        if (_pageRoot == null || _toggleTemplate == null)
            return;

        if (_toast != null)
            Object.Destroy(_toast);

        var canvas = _settingUi.GetComponentInParent<Canvas>();
        _toast = Object.Instantiate(_toggleTemplate.gameObject);
        _toast.name = "ChillClockToast";
        _toast.transform.SetParent(canvas != null ? canvas.transform : _pageRoot.transform, false);
        _toast.transform.SetAsLastSibling();
        _toast.SetActive(true);
        ClearLocalizersRecursive(_toast);

        foreach (var button in _toast.GetComponentsInChildren<Button>(true))
            button.gameObject.SetActive(false);

        var text = FindRowTitle(_toast.transform);
        if (text != null)
        {
            text.text = message;
            text.color = Color.white;
            text.fontStyle = TMPro.FontStyles.Normal;

            var shadow = text.gameObject.GetComponent<Shadow>() ?? text.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.9f);
            shadow.effectDistance = new Vector2(0f, -2f);
        }

        var rect = _toast.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -16f);
            rect.sizeDelta = new Vector2(900f, 60f);
        }

        _toast.AddComponent<ToastAutoDestroy>();
    }

    private void RequestRebuild()
    {
        _dirty = true;
    }

    private IEnumerable<TabFieldPair> EnumerateTabFieldPairs()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var fields = typeof(SettingUI).GetFields(flags);

        foreach (var buttonField in fields)
        {
            if (buttonField.FieldType != typeof(InteractableUI))
                continue;
            if (!buttonField.Name.EndsWith("InteractableUI"))
                continue;

            var prefix = buttonField.Name.Substring(0, buttonField.Name.Length - "InteractableUI".Length);
            var parentField = fields.FirstOrDefault(f => f.Name == prefix + "Parent" && f.FieldType == typeof(GameObject));
            if (parentField != null)
                yield return new TabFieldPair(buttonField, parentField);
        }
    }

    private static T ReadField<T>(SettingUI ui, string fieldName) where T : class
    {
        var field = typeof(SettingUI).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field?.GetValue(ui) as T;
    }

    private static void ActivateInteractable(GameObject gameObject)
    {
        gameObject?.GetComponent<InteractableUI>()?.ActivateUseUI(false);
    }

    private static void DeactivateInteractable(GameObject gameObject)
    {
        gameObject?.GetComponent<InteractableUI>()?.DeactivateUseUI(false);
    }

    private void PlayClickSound()
    {
        if (_settingUi == null)
            return;

        try
        {
            var service = typeof(SettingUI).GetField(
                "_systemSeService",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(_settingUi);
            service?.GetType().GetMethod("PlayClick")?.Invoke(service, null);
        }
        catch
        {
            // 音效失败不影响功能。
        }
    }

    private void ResetBinding()
    {
        _pageBuilt = false;
        _dirty = true;
        _wasActive = false;
        _pickerMode = false;
        _toast = null;
        _settingUi = null;
        _pageRoot = null;
        _pickerRoot = null;
        _pickerContent = null;
        _tabObject = null;
        _scrollContent = null;
        _toggleTemplate = null;
        _hookedForeignButtons.Clear();
    }

    private sealed class TabFieldPair
    {
        public TabFieldPair(FieldInfo buttonField, FieldInfo parentField)
        {
            ButtonField = buttonField;
            ParentField = parentField;
        }

        public FieldInfo ButtonField { get; }
        public FieldInfo ParentField { get; }
    }

    private sealed class ToastAutoDestroy : MonoBehaviour
    {
        private float _remaining = 2.5f;

        private void Update()
        {
            _remaining -= UnityEngine.Time.unscaledDeltaTime;
            if (_remaining <= 0f && gameObject != null)
                Object.Destroy(gameObject);
        }
    }
}
