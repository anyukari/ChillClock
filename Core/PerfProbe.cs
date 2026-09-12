using System.Collections.Generic;
using System.Diagnostics;

namespace ChillFocusWhitelist.Core;

/// <summary>
/// 轻量的"慢操作"探针。
///
/// 这个 mod 里有几件周期性、而且天生不便宜的活：
///   * 扫窗口（EnumWindows + 每个窗口问一次 DWM/进程信息）
///   * 扫场景（Resources.FindObjectsOfTypeAll，会把场景里所有对象都返回）
///   * 从语音包里取一条 OGG 再交给 Unity 解码
///
/// 它们都跑在主线程上，一次几十毫秒就是肉眼可见的掉帧。这里把它们量一下，
/// **只有超过 30ms 才**写一条警告（同一个名字每秒最多一条），
/// 这样以后再出现"卡一下"，翻日志就能直接看到是谁干的，不用猜。
/// </summary>
internal static class PerfProbe
{
    /// <summary>超过这个耗时才算"慢"（一帧 60fps 是 16.7ms，30ms 已经能看出来）。</summary>
    public const double ThresholdMs = 30.0;

    private static readonly Dictionary<string, float> NextLogTimes = new Dictionary<string, float>();

    public static void Mark(string name, Stopwatch watch)
    {
        Mark(name, watch.Elapsed.TotalMilliseconds);
    }

    public static void Mark(string name, double milliseconds)
    {
        if (milliseconds < ThresholdMs)
            return;

        var now = UnityEngine.Time.realtimeSinceStartup;
        if (NextLogTimes.TryGetValue(name, out var next) && now < next)
            return;

        // 名字是有限的几个，正常不会长；保险起见超了就清一次
        if (NextLogTimes.Count > 64)
            NextLogTimes.Clear();

        NextLogTimes[name] = now + 1f;
        Plugin.Log.LogWarning("[Chill Clock] 慢操作：" + name + " " +
                              milliseconds.ToString("0.0") + " ms（超过 30ms 会掉帧）");
    }
}
