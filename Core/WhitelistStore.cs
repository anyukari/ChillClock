using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ChillFocusWhitelist.Core;

public sealed class WhitelistStore
{
    private readonly List<string> _entries = new List<string>();
    private readonly object _lock = new object();

    public WhitelistStore(string filePath)
    {
        FilePath = filePath;
        Load();
    }

    public string FilePath { get; }

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_lock)
                return _entries.ToList();
        }
    }

    public void Add(string rawEntry)
    {
        TryAdd(rawEntry);
    }

    public bool TryAdd(string rawEntry)
    {
        var normalized = NormalizeEntry(rawEntry);
        if (normalized == null)
            return false;

        lock (_lock)
        {
            if (_entries.Any(e => string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase)))
                return false;
            _entries.Add(normalized);
        }

        Save();
        return true;
    }

    public void Remove(string rawEntry)
    {
        var normalized = NormalizeEntry(rawEntry);
        if (normalized == null)
            return;

        lock (_lock)
        {
            _entries.RemoveAll(e => string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase));
        }

        Save();
    }

    public bool IsAllowed(string processPath, string processName)
    {
        if (string.IsNullOrEmpty(processPath) && string.IsNullOrEmpty(processName))
            return false;

        var path = NormalizeForCompare(processPath);
        var name = NormalizeForCompare(processName);
        var nameWithoutExtension = string.IsNullOrEmpty(name)
            ? null
            : NormalizeForCompare(Path.GetFileNameWithoutExtension(processName));

        lock (_lock)
        {
            foreach (var raw in _entries)
            {
                var entry = NormalizeForCompare(raw);
                if (string.IsNullOrEmpty(entry))
                    continue;

                if (!string.IsNullOrEmpty(path) && string.Equals(entry, path, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(name) && string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(nameWithoutExtension) &&
                    string.Equals(entry, nameWithoutExtension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private void Load()
    {
        lock (_lock)
        {
            _entries.Clear();
            if (!File.Exists(FilePath))
                return;

            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var normalized = NormalizeEntry(raw);
                if (normalized != null && !_entries.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    _entries.Add(normalized);
            }
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var lines = _entries.Select(e => "\"" + e + "\"").ToArray();
            File.WriteAllLines(FilePath, lines, new System.Text.UTF8Encoding(false));
        }
    }

    private static string NormalizeEntry(string raw)
    {
        if (raw == null)
            return null;

        var value = raw.Trim();
        if (value.Length == 0 || value.StartsWith("#") || value.StartsWith(";"))
            return null;

        if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
            value = value.Substring(1, value.Length - 2).Trim();

        return value.Length == 0 ? null : value;
    }

    private static string NormalizeForCompare(string value)
    {
        if (value == null)
            return null;
        value = value.Trim().Trim('"');
        return value.Length == 0 ? null : value;
    }
}
