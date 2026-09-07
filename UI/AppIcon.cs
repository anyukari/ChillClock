using System;
using System.IO;
using UnityEngine;

namespace ChillFocusWhitelist.UI;

internal static class AppIcon
{
    public static Texture2D LoadTexture(string executablePath)
    {
        return NativeIcon.LoadTexture(executablePath);
    }

    public static string ResolveExecutable(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return null;

        var trimmed = entry.Trim().Trim('"');
        if (File.Exists(trimmed))
            return trimmed;

        if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed += ".exe";

        var paths = Environment.GetEnvironmentVariable("PATH");
        if (paths == null)
            return null;

        foreach (var dir in paths.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            var candidate = Path.Combine(dir.Trim(), trimmed);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
