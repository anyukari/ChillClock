using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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
/// 语音包按这个顺序找：
///   1. DLL 内嵌的 Voices.pack          —— 发布形态，整个 mod 只有一个 ChillClock.dll
///   2. plugins\ChillClock\Voices.pack  —— 外置包，不重新构建也能换
///   3. plugins\ChillClock\Voices\      —— 散放的 OGG 目录
///   4. DLL 内嵌的那几十条 WAV          —— 最后的兜底
/// </summary>
internal sealed class VoiceManager
{
    /// <summary>
    /// 解码好的语音缓存条数。一条 3 秒的语音解码后大约 0.5MB，64 条 ≈ 30MB。
    /// 缓存太小的话，点着点着就要反复"解包 → 解码"，那一步会卡帧。
    /// </summary>
    private const int MaxCachedClips = 64;
    private const float ChainGap = 0.42f;

    /// <summary>
    /// 闭嘴的时间要卡在音频结束点之前一点点。
    /// 嘴型开关（Animator 的 Enable_Talk）在整段音频期间都是开的，如果按
    /// clip.length + ChainGap 关，话说完之后嘴还会多动大半秒。
    /// </summary>
    private const float MouthTailMargin = 0.15f;

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
    private readonly string _tempDir;
    private byte[] _packBytes;
    private readonly HashSet<string> _packNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool _chainRunning;
    private bool _abortRequested;
    /// <summary>当前这句是不是走游戏语音系统播的（决定"还在不在响"该问谁）。</summary>
    private bool _playingNative;
    private string _lastPlayed;
    private float _nextAttempt;
    private bool _nextIsClick;
    private int _gestureChance;

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

        _tempDir = Path.Combine(Path.GetTempPath(), "ChillClockVoice");
        LoadPack();

        LoadCatalog();
    }

    /// <summary>
    /// 先找 DLL 内嵌的语音包，再找外置的 Voices.pack，整个读到内存里。
    ///
    /// 这里**不长期持有 ZipArchive**，只留字节：Mono 的 ZipArchive 一旦释放过
    /// 条目流，底层流就可能一起被关掉，之后所有条目都读不出来
    /// （日志里就是 "Cannot access a disposed object."）。每次取语音时现开一个
    /// 包，用完整个丢掉，就不会被这件事影响。
    /// </summary>
    private void LoadPack()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Voices.pack", StringComparison.OrdinalIgnoreCase));
            if (resource != null)
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream != null)
                {
                    _packBytes = ReadAllBytes(stream);
                    Plugin.Log.LogInfo("[Chill Clock] voice pack: embedded (" + IndexPack() + " entries)");
                    return;
                }
            }
        }
        catch (Exception e)
        {
            _packBytes = null;
            Plugin.Log.LogWarning("[Chill Clock] embedded voice pack failed: " + e);
        }

        try
        {
            var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var path = Path.Combine(pluginDir, "ChillClock", "Voices.pack");
                if (File.Exists(path))
                {
                    _packBytes = File.ReadAllBytes(path);
                    Plugin.Log.LogInfo("[Chill Clock] voice pack: " + path + " (" + IndexPack() + " entries)");
                    return;
                }
            }
        }
        catch (Exception e)
        {
            _packBytes = null;
            Plugin.Log.LogWarning("[Chill Clock] external voice pack failed: " + e);
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>开一个一次性的语音包，只用来取这一次的数据。</summary>
    private ZipArchive OpenArchive()
    {
        var bytes = _packBytes;
        return bytes == null ? null : new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
    }

    /// <summary>把包里的条目名记下来，省得每次取语音都去问一遍包。</summary>
    private int IndexPack()
    {
        _packNames.Clear();
        using var archive = OpenArchive();
        if (archive == null)
            return 0;

        foreach (var entry in archive.Entries)
            _packNames.Add(entry.FullName);
        return archive.Entries.Count;
    }

    /// <summary>
    /// 立刻停掉我们正在说的这句，并把口型/视线收干净。
    ///
    /// 用在两种情况：番茄钟到点（游戏马上要自己开口）、以及发现游戏已经在说话。
    /// 否则两边会叠在一起。
    /// </summary>
    public void Abort()
    {
        if (!_chainRunning && !_source.isPlaying && !HeroineActionBridge.IsNativeVoicePlaying())
            return;

        Interrupt();
    }

    /// <summary>
    /// 立刻闭嘴：停掉我们的音频（不管它是走游戏语音系统还是走自己的 AudioSource）、
    /// 收掉字幕、关掉口型，并且让连播里还没说的句子作废。
    ///
    /// 为什么要连"游戏语音系统里那条"一起停：我们的语音是借游戏自己的 VoiceManager 播的，
    /// 而游戏每次自己开口都会先 VoiceManager.Stop()，那一下会把它管的所有 voice player
    /// 全停掉——我们的也在其中。以前这里只停自己的 AudioSource，于是音频已经被游戏掐断，
    /// 连播的第二句却还照念：听感就是"话说一半断了，等她的动作完了又接上后半段"。
    /// </summary>
    private void Interrupt()
    {
        _abortRequested = true;

        try
        {
            _source.Stop();
        }
        catch
        {
            // ignore
        }

        HeroineActionBridge.StopNativeVoice();

        try
        {
            _subtitle?.HideNow();
        }
        catch
        {
            // ignore
        }

        HeroineActionBridge.SetMouthTalk(false);
        HeroineActionBridge.EndLineReaction();
    }

    /// <summary>退出时丢开语音包。</summary>
    public void Dispose()
    {
        try
        {
            _packBytes = null;
            _packNames.Clear();
        }
        catch
        {
            // ignore
        }
    }

    public VoiceStartResult PlayDistraction() => Play("Distraction", 3f);
    public VoiceStartResult PlayTaskManager() => Play("TaskManager", 4f);
    public VoiceStartResult PlayExitAttempt() => Play("Exit", 4f);
    public VoiceStartResult PlayRestReminder() => Play("Rest", 30f);
    public VoiceStartResult PlayAmbient() => Play("Ambient", 45f);

    /// <summary>
    /// 点击聪音时的反应台词。state 取 Work / Break / Normal，
    /// 对应她此刻是在工作、休息还是普通待机；再按当前时段筛选。
    /// </summary>
    public VoiceStartResult PlayClick(string state)
    {
        var result = Play("Click_" + state, 4f);
        if (result == VoiceStartResult.Started)
            _nextIsClick = true;
        return result;
    }

    private VoiceStartResult Play(string trigger, float cooldown)
    {
        if (_source == null || _runner == null || _chainRunning || _source.isPlaying)
            return VoiceStartResult.Deferred;

        // 上一句的字幕还在显示 = 她还没说完。这时候不开口（点击也是一样），
        // 免得新句子把上一句的字幕顶掉、或者两句叠在一起。
        if (_subtitle != null && _subtitle.IsShowing)
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
        _gestureChance = GestureChanceFor(trigger);
        _abortRequested = false;
        _runner.StartCoroutine(PlayChain(chain));
        return VoiceStartResult.Started;
    }

    /// <summary>
    /// 各池子"顺便做个动作"的概率。
    /// 提醒类（走神 / 任务管理器 / 退出）一律不做动作 —— 那时候她该看着你说话，
    /// 而不是换姿势；闲聊和点击才偶尔来一下。
    /// </summary>
    private static int GestureChanceFor(string trigger)
    {
        switch (trigger)
        {
            case "Distraction":
            case "TaskManager":
            case "Exit":
                return 0;
            default:
                return 10;
        }
    }

    /// <summary>
    /// 从候选池里抽一条，优先当前时段的专属台词。
    /// 别的时段的台词不会被抽到；一条都不匹配时才退回"未标时段"的中性台词。
    /// </summary>
    private string Pick(List<string> pool)
    {
        if (pool.Count == 0)
            return null;

        var now = HeroineActionBridge.GetTimeOfDay();
        if (!string.IsNullOrEmpty(now))
        {
            var matches = new List<string>();
            foreach (var file in pool)
            {
                if (_catalog.TryGetValue(file, out var line) &&
                    !string.IsNullOrEmpty(line.Time) &&
                    string.Equals(line.Time, now, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(file);
                }
            }

            if (matches.Count > 0)
                return matches[UnityEngine.Random.Range(0, matches.Count)];
        }

        var neutral = new List<string>();
        foreach (var file in pool)
        {
            if (_catalog.TryGetValue(file, out var line) && string.IsNullOrEmpty(line.Time))
                neutral.Add(file);
        }

        var source = neutral.Count > 0 ? neutral : pool;
        return source[UnityEngine.Random.Range(0, source.Count)];
    }

    private IEnumerator PlayChain(List<string> files)
    {
        _chainRunning = true;
        try
        {
            // 连播组只在第一句转头：每句都转一次头会看着像"来回扭头"
            var firstLine = true;
            foreach (var file in files)
            {
                // 上一句被游戏打断了（她去喝茶吹气、说自己的台词……），连播剩下的不说了
                if (_abortRequested)
                    break;

                // 游戏自己正在说话时开口，只会两边叠在一起：这句也一起放弃
                if (!firstLine && HeroineActionBridge.IsGameVoiceBusy())
                {
                    Interrupt();
                    break;
                }

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

                PlayLine(file, clip, firstLine);

                // 口型跟着"真正在出声"的时间段走，台词中间的停顿会闭嘴
                var line = _catalog.TryGetValue(file, out var found) ? found : null;
                yield return DriveMouth(clip, line);

                // 下一句开口前，先让上一句的字幕读得完：
                // 短句（语音 1.5 秒、字幕要停 2.5 秒）以前会被下一句直接顶掉，看着就是"一闪"。
                var gap = ChainGap;
                if (line != null)
                {
                    var english = string.IsNullOrEmpty(line.English) ? line.Japanese : line.English;
                    var text = LocalizedText.Pick(line.Chinese, english, line.Japanese);
                    gap = Mathf.Max(gap, GameSubtitle.DisplaySeconds(text, clip.length) - clip.length);
                }

                yield return new WaitForSecondsRealtime(gap);

                // 等上一句字幕真的收掉再开口（含打字机还没打完的情况，最多等 8 秒）。
                // 判据用字幕组件自己的状态，而不是我们估的时长。
                var waitedSubtitle = 0f;
                while (_subtitle != null && _subtitle.IsShowing && waitedSubtitle < 8f)
                {
                    waitedSubtitle += Time.unscaledDeltaTime;
                    yield return null;
                }

                firstLine = false;
            }
        }
        finally
        {
            _chainRunning = false;

            // 不管中间怎么结束，都收拾干净：视线慢慢回正、表情复位
            HeroineActionBridge.EndLineReaction();
        }
    }

    /// <summary>
    /// 说话期间跟着音频开关口型。
    ///
    /// 原来是一条语音从头开到尾，台词中间有停顿的时候嘴还在动。
    /// 这里用离线算好的时间段（目录第 10 列）来管：说到哪一段就开嘴，空档就闭嘴。
    /// 没有分段信息的老语音包保持原来的行为（整条开着，结束前一点闭嘴）。
    /// </summary>
    private IEnumerator DriveMouth(AudioClip clip, VoiceLine line)
    {
        var spans = line?.TalkSpans;
        if (spans == null || spans.Length < 2)
        {
            // 老语音包没有分段信息：整条开着嘴。但中途被游戏掐了也要立刻收，
            // 不能傻等一整条放完（那样会出现"声音没了嘴还在动"）。
            var waited = 0f;
            var tail = Mathf.Max(0.1f, clip.length - MouthTailMargin);
            while (waited < tail)
            {
                if (IsInterrupted(clip, waited))
                {
                    Interrupt();
                    yield break;
                }

                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            HeroineActionBridge.SetMouthTalk(false);
            yield break;
        }

        var elapsed = 0f;
        var speaking = true;
        while (elapsed < clip.length)
        {
            if (IsInterrupted(clip, elapsed))
            {
                Interrupt();
                yield break;
            }

            var shouldTalk = InSpans(spans, elapsed);
            if (shouldTalk != speaking)
            {
                speaking = shouldTalk;
                HeroineActionBridge.SetMouthTalk(speaking);
            }

            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (speaking)
            HeroineActionBridge.SetMouthTalk(false);
    }

    /// <summary>
    /// 这句是不是该让路 / 已经废了。判两条：
    ///
    ///   1. 游戏自己开口了（剧情台词、野生动作的碎碎念……）：它一开口就先
    ///      VoiceManager.Stop()，我们那条借它的 player 播的语音会被一起掐掉，
    ///      继续动嘴只会没声音。
    ///   2. 我们那条音频确实已经没在响了，而按时间还没到结尾 —— 就是被掐的那一下。
    ///
    /// 第 2 条要跳过开头那零点几秒：刚 Play 的同一帧里 Unity 的 isPlaying 还没翻过来，
    /// 直接判会把每一句都当成"被掐了"。
    /// </summary>
    private bool IsInterrupted(AudioClip clip, float elapsed)
    {
        if (_abortRequested || HeroineActionBridge.IsGameVoiceBusy())
            return true;

        if (elapsed < 0.2f || elapsed >= clip.length - 0.25f)
            return false;

        var alive = _playingNative ? HeroineActionBridge.IsNativeVoicePlaying() : _source.isPlaying;
        return !alive;
    }

    private static bool InSpans(float[] spans, float t)
    {
        for (var i = 0; i + 1 < spans.Length; i += 2)
        {
            if (t >= spans[i] && t <= spans[i + 1])
                return true;
        }

        return false;
    }

    /// <summary>
    /// 播放一条语音。优先把音频交给游戏自己的语音系统（这样音量走游戏设置），
    /// 拿不到时才退回自己的 AudioSource。口型一律由我们自己开关。
    /// </summary>
    private void PlayLine(string fileName, AudioClip clip, bool firstLineOfChain)
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

        _playingNative = native;

        // 口型由我们自己开关：VoiceManager.Play 不管这个，
        // 也不能走 HeroineVoiceController.PlayVoice（会把 _isFinishedVoice 卡死）。
        HeroineActionBridge.SetMouthTalk(true);

        if (!_catalog.TryGetValue(fileName, out var line))
            return;

        // 念台词时按游戏自己的规则来（她干活时不动身体、只转头；不在干活时只换表情），
        // 链子结束时统一把视线放回去。连播组只在第一句转头。
        HeroineActionBridge.Play(line.Emotion, _nextIsClick, firstLineOfChain, _gestureChance);
        _nextIsClick = false;

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

        if (_packBytes != null && _packNames.Contains(file))
        {
            _loading.Add(file);
            _runner.StartCoroutine(LoadOggFromPack(file));
            return;
        }
        else
        {
            var path = string.IsNullOrEmpty(_externalDir) ? null : Path.Combine(_externalDir, file);
            if (path != null && File.Exists(path))
            {
                _loading.Add(file);
                _runner.StartCoroutine(LoadOgg(file, path, false));
                return;
            }
        }

        var embedded = LoadEmbedded(file);
        if (embedded != null)
            Store(file, embedded);
    }

    /// <summary>从 Voices.pack 里取出这一条，落到临时文件再交给 Unity 解码。</summary>
    private IEnumerator LoadOggFromPack(string file)
    {
        string temp = null;
        var extractWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(_tempDir);
            temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".ogg");

            using (var archive = OpenArchive())
            {
                var entry = archive?.GetEntry(file);
                if (entry == null)
                    throw new FileNotFoundException("pack 里没有这一条");

                using var src = entry.Open();
                using var dst = File.Create(temp);
                src.CopyTo(dst);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] pack extract failed: " + file + " " + e);
            _loading.Remove(file);
            TryDelete(temp);

            var fallback = LoadEmbedded(file);
            if (fallback != null)
                Store(file, fallback);
            yield break;
        }

        PerfProbe.Mark("语音解包", extractWatch);
        yield return LoadOgg(file, temp, true);
    }

    private IEnumerator LoadOgg(string file, string path, bool deleteAfterLoad)
    {
        var request = UnityWebRequestMultimedia.GetAudioClip("file:///" + path.Replace('\\', '/'), AudioType.OGGVORBIS);
        yield return request.SendWebRequest();
        _loading.Remove(file);

        if (request.result == UnityWebRequest.Result.Success)
        {
            var decodeWatch = System.Diagnostics.Stopwatch.StartNew();
            var clip = DownloadHandlerAudioClip.GetContent(request);
            request.Dispose();
            if (clip != null)
            {
                clip.name = file;
                if (!clip.LoadAudioData())
                    Plugin.Log.LogWarning("[Chill Clock] LoadAudioData failed: " + file);
                PerfProbe.Mark("语音解码", decodeWatch);
                Store(file, clip);
                if (deleteAfterLoad)
                    TryDelete(path);
                yield break;
            }
            Plugin.Log.LogWarning("[Chill Clock] ogg decode null: " + file);
        }
        else
        {
            Plugin.Log.LogWarning("[Chill Clock] ogg load failed: " + file + " " + request.error);
            request.Dispose();
        }

        if (deleteAfterLoad)
            TryDelete(path);

        var embedded = LoadEmbedded(file);
        if (embedded != null)
            Store(file, embedded);
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件删不掉就算了，下次启动会覆盖
        }
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
            if (ReadCatalogFromPack() || ReadCatalogFromFolder())
            {
                // 已从外部语音包读到
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

    private bool ReadCatalogFromPack()
    {
        if (_packBytes == null)
            return false;

        try
        {
            var rows = new List<string>();
            using (var archive = OpenArchive())
            {
                var entry = archive?.GetEntry("voice_catalog.tsv");
                if (entry == null)
                    return false;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                string row;
                while ((row = reader.ReadLine()) != null)
                    rows.Add(row);
            }

            ParseCatalog(rows);
            Plugin.Log.LogInfo("[Chill Clock] voice catalog from pack: " + _catalog.Count + " lines");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] pack catalog failed: " + e);
            return false;
        }
    }

    private bool ReadCatalogFromFolder()
    {
        try
        {
            var external = string.IsNullOrEmpty(_externalDir) ? null : Path.Combine(_externalDir, "voice_catalog.tsv");
            if (external == null || !File.Exists(external))
                return false;

            ParseCatalog(File.ReadAllLines(external));
            Plugin.Log.LogInfo("[Chill Clock] external voice catalog: " + _catalog.Count + " lines");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] external catalog failed: " + e.Message);
            return false;
        }
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
                SeqOrder = parts.Length > 7 && int.TryParse(parts[7].Trim(), out var order) ? order : 0,
                // 第 9 列（可选）：Morning / Noon / Evening / Night，留空表示任何时段都能用
                Time = parts.Length > 8 ? parts[8].Trim() : string.Empty,
                // 第 10 列（可选）：真正在出声的时间段，用来管口型
                TalkSpans = parts.Length > 9 ? ParseSpans(parts[9]) : null
            };
            _catalog[line.File] = line;
        }
    }

    /// <summary>把 "0.08-1.24;1.62-3.05" 解析成 start,end,start,end… 的扁平数组。</summary>
    private static float[] ParseSpans(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var spans = new List<float>();
        foreach (var part in value.Split(';'))
        {
            var dash = part.IndexOf('-');
            if (dash <= 0)
                continue;

            if (float.TryParse(part.Substring(0, dash), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) &&
                float.TryParse(part.Substring(dash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var end) &&
                end > start)
            {
                spans.Add(start);
                spans.Add(end);
            }
        }

        return spans.Count >= 2 ? spans.ToArray() : null;
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
        public string Time;

        /// <summary>
        /// 这条语音"真正在出声"的时间段，扁平存成 start,end,start,end…（秒）。
        /// 由 tools/analyze-speech-spans.py 离线算好写进目录的第 10 列。
        /// 空表示没有分段信息，口型按整条处理。
        /// </summary>
        public float[] TalkSpans;
    }
}

/// <summary>
/// 语音播放需要一个 MonoBehaviour 来跑协程。
/// </summary>
internal sealed class VoiceRunner : MonoBehaviour
{
}
