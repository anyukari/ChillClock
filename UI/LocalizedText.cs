using System;
using Bulbul;
using NestopiSystem.DIContainers;

namespace ChillFocusWhitelist.UI;

internal static class LocalizedText
{
    private static GameLanguageType? _cached;
    private static float _nextLookup;

    public static void SetLanguage(GameLanguageType? language)
    {
        if (language.HasValue)
            _cached = language;
    }

    public static string Pick(string zh, string en, string ja)
    {
        try
        {
            if (!_cached.HasValue)
                RefreshLanguageIfNeeded();
            if (_cached == GameLanguageType.Japanese)
                return ja;
            if (_cached == GameLanguageType.ChineseSimplified ||
                _cached == GameLanguageType.ChineseTraditional)
                return zh;
        }
        catch
        {
            // 解析失败时退回英文。
        }

        return en;
    }

    private static void RefreshLanguageIfNeeded()
    {
        if (UnityEngine.Time.realtimeSinceStartup < _nextLookup)
            return;

        _nextLookup = UnityEngine.Time.realtimeSinceStartup + 5f;
        var languageSupplier = ProjectLifetimeScope.Resolve<LanguageSupplier>();
        _cached = languageSupplier?.Get() ?? GameLanguageType.English;
    }
}
