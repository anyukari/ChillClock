using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ChillFocusWhitelist.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace ChillFocusWhitelist.Core;

/// <summary>一次语音提醒的启动结果。</summary>
internal enum VoiceStartResult
{
    /// <summary>已经开播（含连播）。</summary>
    Started,

    /// <summary>现在不方便播（游戏在说话 / 上一句还没完），保留待播标记稍后重试。</summary>
    Deferred,

    /// <summary>没得播（冷却中 / 候选池为空 / 已经在播），这条提醒可以丢掉。</summary>
    Skipped
}

/// <summary>
/// 聪音的语音提醒。
/// 语音包优先从 plugins\ChillClock\Voices\ 读（OGG + voice_catalog.tsv），
/// 找不到就退回 DLL 里内嵌的那几十条 WAV。
/// </summary>
internal sealed class VoiceManager
{
    private const int MaxCachedClips = 40;
    private const float ChainGap = 0.42f;
    private const float LoadTimeout = 5f;

    private readonly AudioSource _source;
    private readonly VoiceRunner _runner;
    private readonly GameSubtitle _subtitle;
    private readonly Dictionary<string, VoiceLine> _catalog = new Dictionary<string, VoiceLine>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pools = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _chains = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _nextTimes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _lru = new List<string>();
    private readonly HashSet<string> _loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly string _externalDir;
    private bool _chainRunning;
    private string _lastPlayed;
    private float _nextAttempt;

    public VoiceManager(GameObject host)
    {
        if (host.GetComponent<AudioSource>() == null)
            host.AddComponent<AudioSource>();
        _source = host.GetComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = false;
        _source.spatialBlend = 0f;
        _source.volume = 0.9f;
        _runner = host.GetComponent<VoiceRunner>() ?? host.AddComponent<VoiceRunner>();
        _subtitle = host.GetComponent<GameSubtitle>() ?? host.AddComponent<GameSubtitle>();

        var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (!string.IsNullOrEmpty(pluginDir))
            _externalDir = Path.Combine(pluginDir, "ChillClock", "Voices");

        LoadCatalog();
    }

    public VoiceStartResult PlayDistraction() => Play("Distraction", 8f);
    public VoiceStartResult PlayTaskManager() => Play("TaskManager", 5f);
    public VoiceStartResult PlayExitAttempt() => Play("Exit", 6f);
    public VoiceStartResult PlayRestReminder() => Play("Rest", 30f);
    public VoiceStartResult PlayAmbient() => Play("Ambient", 45f);

    private VoiceStartResult Play(string trigger, float cooldown)
    {
        if (_source == null || _runner == null || _chainRunning || _source.isPlaying)
            return VoiceStartResult.Deferred;

        var now = Time.realtimeSinceStartup;
        if (now < _nextAttempt)
            return VoiceStartResult.Deferred;

        if (_nextTimes.TryGetValue(trigger, out var next) && now < next)
            return VoiceStartResult.Skipped;

        if (!_pools.TryGetValue(trigger, out var pool) || pool.Count == 0)
            return VoiceStartResult.Skipped;

        // 游戏自己正在说话时先让路：既不会盖掉它，也不会让它的 PlayVoice 因为
        // _isFinishedVoice 还是 false 而被静默丢弃。
        if (HeroineActionBridge.IsGameVoiceBusy())
        {
            _nextAttempt = now + 0.5f;
            return VoiceStartResult.Deferred;
        }

        // 游戏正在放它自己的演出（开场问候、结束通话挥手等）时同样让路：
        // 这段时间插话既会盖掉台词，也会打断它排好的动作。
        if (HeroineActionBridge.IsGameSequenceBusy())
        {
            _nextAttempt = now + 1f;
            return VoiceStartResult.Deferred;
        }

        var start = Pick(pool);
        if (start == null)
            return VoiceStartResult.Skipped;
        _lastPlayed = start;

        var chain = _chains.TryGetValue(start, out var found) && found.Count > 1
            ? found
            : new List<string> { start };

        _nextTimes[trigger] = now + cooldown + (chain.Count > 1 ? chain.Count * 4.5f : 0f);
        _runner.StartCoroutine(PlayChain(chain));
        return VoiceStartResult.Started;
    }

    private static string Pick(List<string> pool)
    {
        if (pool.Count == 0)
            return null;
        return pool[UnityEngine.Random.Range(0, pool.Count)];
    }

    private IEnumerator PlayChain(List<string> files)
    {
        _chainRunning = true;
        try
        {
            foreach (var file in files)
            {
                RequestClip(file);
                var waited = 0f;
                while (GetClip(file) == null && waited < LoadTimeout)
                {
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }

                var clip = GetClip(file);
                if (clip == null)
                    continue;

                PlayLine(file, clip);
                yield return new WaitForSecondsRealtime(clip.length + ChainGap);
                HeroineActionBridge.SetMouthTalk(false);
            }
        }
        finally
        {
            _chainRunning = false;
        }
    }

    /// <summary>
    /// 播放一条语音。优先把音频交给游戏自己的语音系统（这样音量走游戏设置），
    /// 拿不到时才退回自己的 AudioSource。口型一律由我们自己开关。
    /// </summary>
    private void PlayLine(string fileName, AudioClip clip)
    {
        var clipName = Path.GetFileNameWithoutExtension(fileName);
        var native = false;
        try
        {
            native = HeroineActionBridge.TryPlayNative(clipName, clip);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] native voice failed: " + e.Message);
        }

        if (!native)
            _source.PlayOneShot(clip);

        // 口型由我们自己开关：VoiceManager.Play 不管这个，
        // 也不能走 HeroineVoiceController.PlayVoice（会把 _isFinishedVoice 卡死）。
        HeroineActionBridge.SetMouthTalk(true);

        if (!_catalog.TryGetValue(fileName, out var line))
            return;

        HeroineActionBridge.Play(line.Emotion);
        // 英文还没翻译完时，英语用户至少能看到日文原文，不至于空字幕
        var english = string.IsNullOrEmpty(line.English) ? line.Japanese : line.English;
        _subtitle.Show(LocalizedText.Pick(line.Chinese, english, line.Japanese), clip.length);
    }

    private AudioClip GetClip(string file)
    {
        return _clips.TryGetValue(file, out var clip) ? clip : null;
    }

    private void Store(string file, AudioClip clip)
    {
        _clips[file] = clip;
        _lru.Remove(file);
        _lru.Add(file);
        while (_lru.Count > MaxCachedClips)
        {
            var oldest = _lru[0];
            _lru.RemoveAt(0);
            if (_clips.TryGetValue(oldest, out var old) && old != null)
                UnityEngine.Object.Destroy(old);
            _clips.Remove(oldest);
        }
    }

    private void RequestClip(string file)
    {
        if (_clips.ContainsKey(file) || _loading.Contains(file))
            return;

        var path = string.IsNullOrEmpty(_externalDir) ? null : Path.Combine(_externalDir, file);
        if (path != null && File.Exists(path))
        {
            _loading.Add(file);
            _runner.StartCoroutine(LoadOgg(file, path));
            return;
        }

        var embedded = LoadEmbedded(file);
        if (embedded != null)
            Store(file, embedded);
    }

    private IEnumerator LoadOgg(string file, string path)
    {
        var request = UnityWebRequestMultimedia.GetAudioClip("file:///" + path.Replace('\\', '/'), AudioType.OGGVORBIS);
        yield return request.SendWebRequest();
        _loading.Remove(file);

        if (request.result == UnityWebRequest.Result.Success)
        {
            var clip = DownloadHandlerAudioClip.GetContent(request);
            request.Dispose();
            if (clip != null)
            {
                clip.name = file;
                if (!clip.LoadAudioData())
                    Plugin.Log.LogWarning("[Chill Clock] LoadAudioData failed: " + file);
                Store(file, clip);
                yield break;
            }
            Plugin.Log.LogWarning("[Chill Clock] ogg decode null: " + file);
        }
        else
        {
            Plugin.Log.LogWarning("[Chill Clock] ogg load failed: " + file + " " + request.error);
            request.Dispose();
        }

        var embedded = LoadEmbedded(file);
        if (embedded != null)
            Store(file, embedded);
    }

    private static AudioClip LoadEmbedded(string fileName)
    {
        try
        {
            var wav = fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : fileName;
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".Voices." + Path.GetFileNameWithoutExtension(wav) + ".wav", StringComparison.OrdinalIgnoreCase));
            if (resource == null)
                return null;

            using var stream = assembly.GetManifestResourceStream(resource);
            return stream == null ? null : LoadWave(stream, Path.GetFileName(resource));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] embedded voice failed: " + e.Message);
            return null;
        }
    }

    private void LoadCatalog()
    {
        try
        {
            var external = string.IsNullOrEmpty(_externalDir) ? null : Path.Combine(_externalDir, "voice_catalog.tsv");
            if (external != null && File.Exists(external))
            {
                ParseCatalog(File.ReadAllLines(external));
                Plugin.Log.LogInfo("[Chill Clock] external voice catalog: " + _catalog.Count + " lines");
            }
            else
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resource = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(".Voices.voice_catalog.tsv", StringComparison.OrdinalIgnoreCase));
                if (resource != null)
                {
                    using var stream = assembly.GetManifestResourceStream(resource);
                    using var reader = new StreamReader(stream);
                    var rows = new List<string>();
                    string row;
                    while ((row = reader.ReadLine()) != null)
                        rows.Add(row);
                    ParseCatalog(rows);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] voice catalog load failed: " + e.Message);
        }

        BuildPools();
        BuildChains();
    }

    private void ParseCatalog(IEnumerable<string> rows)
    {
        var first = true;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row))
                continue;
            if (first)
            {
                first = false;
                continue;
            }

            var parts = row.Split('\t');
            if (parts.Length < 5)
                continue;

            var line = new VoiceLine
            {
                File = parts[0].Trim(),
                Japanese = parts[1],
                Chinese = parts[2],
                English = parts[3],
                Emotion = parts[4].Trim(),
                Trigger = parts.Length > 5 ? parts[5].Trim() : string.Empty,
                SeqGroup = parts.Length > 6 ? parts[6].Trim() : string.Empty,
                SeqOrder = parts.Length > 7 && int.TryParse(parts[7].Trim(), out var order) ? order : 0
            };
            _catalog[line.File] = line;
        }
    }

    private void BuildPools()
    {
        foreach (var line in _catalog.Values)
        {
            var trigger = string.IsNullOrEmpty(line.Trigger) ? GuessTrigger(line.File) : line.Trigger;
            if (string.IsNullOrEmpty(trigger))
                continue;

            // 联动组只把第一句放进候选池
            if (!string.IsNullOrEmpty(line.SeqGroup) && line.SeqOrder > 1)
                continue;

            if (!_pools.TryGetValue(trigger, out var pool))
            {
                pool = new List<string>();
                _pools[trigger] = pool;
            }
            pool.Add(line.File);
        }
    }

    private static string GuessTrigger(string file)
    {
        if (file.StartsWith("New_Rest", StringComparison.OrdinalIgnoreCase))
            return "Rest";
        if (file.StartsWith("New_TaskManager", StringComparison.OrdinalIgnoreCase))
            return "TaskManager";
        if (file.StartsWith("New_Exit", StringComparison.OrdinalIgnoreCase))
            return "Exit";
        return "Distraction";
    }

    private void BuildChains()
    {
        var groups = new Dictionary<string, List<VoiceLine>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in _catalog.Values)
        {
            if (string.IsNullOrEmpty(line.SeqGroup))
                continue;
            if (!groups.TryGetValue(line.SeqGroup, out var list))
            {
                list = new List<VoiceLine>();
                groups[line.SeqGroup] = list;
            }
            list.Add(line);
        }

        foreach (var group in groups.Values)
        {
            group.Sort((a, b) => a.SeqOrder.CompareTo(b.SeqOrder));
            if (group.Count <= 1)
                continue;

            var files = new List<string>(group.Count);
            foreach (var line in group)
                files.Add(line.File);
            _chains[files[0]] = files;
        }
    }

    private static AudioClip LoadWave(Stream stream, string clipName)
    {
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
            return null;
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            return null;

        var channels = 1;
        var sampleRate = 32000;
        var bitsPerSample = 16;
        byte[] data = null;

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();
            var chunkEnd = reader.BaseStream.Position + chunkSize;

            if (chunkId == "fmt ")
            {
                var audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                if (audioFormat != 1 || bitsPerSample != 16)
                    return null;
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
            }

            if (reader.BaseStream.Position < chunkEnd)
                reader.BaseStream.Position = chunkEnd;
            if ((chunkSize & 1) != 0 && reader.BaseStream.Position < reader.BaseStream.Length)
                reader.BaseStream.Position++;
        }

        if (data == null || data.Length < 2 || channels <= 0)
            return null;

        var sampleCount = data.Length / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;

        var clip = AudioClip.Create(clipName, sampleCount / channels, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private sealed class VoiceLine
    {
        public string File;
        public string Japanese;
        public string Chinese;
        public string English;
        public string Emotion;
        public string Trigger;
        public string SeqGroup;
        public int SeqOrder;
    }
}

/// <summary>
/// 语音播放需要一个 MonoBehaviour 来跑协程。
/// </summary>
internal sealed class VoiceRunner : MonoBehaviour
{
}
