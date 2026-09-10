using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ChillFocusWhitelist.Core;

/// <summary>
/// 用聪音的既有语音做走神提醒。
/// 音频以资源形式内嵌在 DLL 中，不依赖额外文件。
/// </summary>
internal sealed class VoiceManager
{
    private static readonly string[] DistractionVoices =
    {
        "Voice_ClickHeroine_Word_Work_002.wav",
        "Voice_ClickHeroine_Word_Work_004.wav",
        "Voice_ClickHeroine_Word_Work_010.wav",
        "Voice_ClickHeroine_Word_Work_011.wav",
        "Voice_ClickHeroine_Word_Work_016.wav",
        "Voice_ClickHeroine_Word_Work_017.wav",
        "Voice_ClickHeroine_Word_Work_020.wav",
        "Voice_ClickHeroine_Word_Work_021.wav",
        "Voice_ClickHeroine_Word_Work_022.wav",
        "Voice_ClickHeroine_Word_Work_023.wav",
        "Voice_ClickHeroine_Word_Work_Evening_001.wav",
        "Voice_PomodoroFinish_Talk_19_Reaction_001.wav",
        "Voice_ClickHeroine_Word_Normal_021.wav",
        "Voice_ClickHeroine_Word_Normal_023.wav"
    };

    private static readonly string[] TaskManagerVoices =
    {
        "Voice_ClickHeroine_Word_Work_010.wav",
        "Voice_ClickHeroine_Word_Work_017.wav",
        "Voice_ClickHeroine_Word_Work_004.wav",
        "Voice_PomodoroFinish_Talk_19_Reaction_001.wav"
    };

    private static readonly string[] ExitAttemptVoices =
    {
        "Voice_ClickHeroine_Word_Work_002.wav",
        "Voice_ClickHeroine_Word_Work_010.wav",
        "Voice_PomodoroFinish_Talk_19_Reaction_001.wav"
    };

    private readonly AudioSource _source;
    private readonly Dictionary<string, AudioClip> _clipCache =
        new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

    private float _nextDistractionTime;
    private float _nextTaskManagerTime;
    private float _nextExitAttemptTime;
    private string _lastPlayed;

    public VoiceManager(GameObject host)
    {
        if (host.GetComponent<AudioSource>() == null)
            host.AddComponent<AudioSource>();
        _source = host.GetComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = false;
        _source.spatialBlend = 0f;
        _source.volume = 0.9f;
    }

    public void PlayDistraction()
    {
        Play(DistractionVoices, ref _nextDistractionTime, 8f);
    }

    public void PlayTaskManager()
    {
        Play(TaskManagerVoices, ref _nextTaskManagerTime, 5f);
    }

    public void PlayExitAttempt()
    {
        Play(ExitAttemptVoices, ref _nextExitAttemptTime, 6f);
    }

    private void Play(string[] voices, ref float nextTime, float cooldown)
    {
        var now = Time.realtimeSinceStartup;
        if (now < nextTime || _source == null || _source.isPlaying)
            return;

        var clip = PickClip(voices);
        if (clip == null)
            return;

        _source.PlayOneShot(clip);
        nextTime = now + cooldown;
    }

    private AudioClip PickClip(string[] voices)
    {
        if (voices == null || voices.Length == 0)
            return null;

        string picked;
        if (voices.Length == 1)
        {
            picked = voices[0];
        }
        else
        {
            do
            {
                picked = voices[UnityEngine.Random.Range(0, voices.Length)];
            } while (string.Equals(picked, _lastPlayed, StringComparison.OrdinalIgnoreCase));
        }

        var clip = LoadClip(picked);
        if (clip != null)
            _lastPlayed = picked;
        return clip;
    }

    private AudioClip LoadClip(string fileName)
    {
        if (_clipCache.TryGetValue(fileName, out var cached))
            return cached;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = null;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(".Voices." + fileName, StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = name;
                    break;
                }
            }

            if (resourceName == null)
            {
                Plugin.Log.LogWarning("[Chill Clock] voice resource not found: " + fileName);
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            var clip = stream == null ? null : LoadWave(stream, fileName);
            if (clip != null)
                _clipCache[fileName] = clip;
            return clip;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] voice load failed: " + e.Message);
            return null;
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

        var clip = AudioClip.Create(
            clipName,
            sampleCount / channels,
            channels,
            sampleRate,
            false);
        clip.SetData(samples, 0);
        return clip;
    }
}
