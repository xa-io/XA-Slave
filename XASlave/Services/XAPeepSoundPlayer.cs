using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;

namespace XASlave.Services;

internal static class XAPeepSoundPlayer
{
    private const int SampleRate = 44100;
    private const float ToneAmplitude = 0.90f;
    private static readonly object TonePlaybackSync = new();
    private static readonly SemaphoreSlim TonePlaybackGate = new(1, 1);
    private static CancellationTokenSource? activeTonePlaybackCancellation;
    private static readonly float[] BaseFrequencies =
    {
        392.00f,
        415.30f,
        440.00f,
        466.16f,
        493.88f,
        523.25f,
        554.37f,
        587.33f,
        622.25f,
        659.25f,
        698.46f,
        739.99f,
        783.99f,
        830.61f,
        880.00f,
        932.33f,
    };

    public static bool TryPlayAlert(int alertId, float volume, Dalamud.Plugin.Services.IPluginLog log)
    {
        alertId = Data.XAPeepData.ClampSoundEffectId(alertId);
        if (alertId == 0)
            return false;

        var clampedVolume = Math.Clamp(volume, 0f, 1f);
        var soundDevice = DirectSoundOut.Devices.FirstOrDefault();
        if (soundDevice == null)
            return false;

        try
        {
            var wavData = BuildAlertWaveData(alertId);
            var worker = new Thread(() => PlayAlertInternal(soundDevice.Guid, wavData, clampedVolume, log))
            {
                IsBackground = true,
                Name = "XAPeepSound",
            };
            worker.Start();
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] XA Peep could not prepare its direct alert sound.");
            return false;
        }
    }

    public static bool TryPlayTone(
        int toneId,
        int beepCount,
        float volume,
        Dalamud.Plugin.Services.IPluginLog log,
        Action? onPlaybackFailure = null)
    {
        toneId = Math.Clamp(toneId, 1, BaseFrequencies.Length);
        beepCount = Math.Clamp(beepCount, 1, 10);

        var clampedVolume = Math.Clamp(volume, 0f, 1f);
        StopTonePlayback();
        if (clampedVolume <= 0f)
            return true;

        try
        {
            var soundDevice = DirectSoundOut.Devices.FirstOrDefault();
            if (soundDevice == null)
                return false;

            var wavData = BuildToneWaveData(toneId, beepCount);
            var playbackCancellation = new CancellationTokenSource();
            var worker = new Thread(() => PlayToneInternal(
                soundDevice.Guid,
                wavData,
                clampedVolume,
                playbackCancellation,
                log,
                onPlaybackFailure))
            {
                IsBackground = true,
                Name = "XACombatTypingSound",
            };

            lock (TonePlaybackSync)
            {
                activeTonePlaybackCancellation = playbackCancellation;
            }

            try
            {
                worker.Start();
            }
            catch
            {
                lock (TonePlaybackSync)
                {
                    if (ReferenceEquals(activeTonePlaybackCancellation, playbackCancellation))
                        activeTonePlaybackCancellation = null;
                }

                playbackCancellation.Dispose();
                throw;
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Alert When Typing In Combat could not prepare its direct alert sound.");
            return false;
        }
    }

    public static void StopTonePlayback()
    {
        lock (TonePlaybackSync)
        {
            activeTonePlaybackCancellation?.Cancel();
            activeTonePlaybackCancellation = null;
        }
    }

    public static string GetToneLabel(int toneId)
    {
        var clampedId = Math.Clamp(toneId, 1, BaseFrequencies.Length);
        return $"Tone {clampedId} ({BaseFrequencies[clampedId - 1]:0.##} Hz)";
    }

    private static void PlayAlertInternal(Guid deviceGuid, byte[] wavData, float volume, Dalamud.Plugin.Services.IPluginLog log)
    {
        try
        {
            using var stream = new MemoryStream(wavData, writable: false);
            using var reader = new WaveFileReader(stream);
            using var channel = new WaveChannel32(reader)
            {
                Volume = volume,
                PadWithZeroes = false,
            };
            using var output = new DirectSoundOut(deviceGuid);
            output.Init(channel);
            output.Play();

            while (output.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(25);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] XA Peep direct alert playback failed.");
        }
    }

    private static void PlayToneInternal(
        Guid deviceGuid,
        byte[] wavData,
        float volume,
        CancellationTokenSource playbackCancellation,
        Dalamud.Plugin.Services.IPluginLog log,
        Action? onPlaybackFailure)
    {
        var ownsPlaybackGate = false;
        try
        {
            TonePlaybackGate.Wait(playbackCancellation.Token);
            ownsPlaybackGate = true;

            if (playbackCancellation.IsCancellationRequested)
                return;

            using var stream = new MemoryStream(wavData, writable: false);
            using var reader = new WaveFileReader(stream);
            using var channel = new WaveChannel32(reader)
            {
                Volume = volume,
                PadWithZeroes = false,
            };
            using var output = new DirectSoundOut(deviceGuid);
            output.Init(channel);
            output.Play();

            while (output.PlaybackState == PlaybackState.Playing && !playbackCancellation.IsCancellationRequested)
                Thread.Sleep(25);

            if (playbackCancellation.IsCancellationRequested && output.PlaybackState == PlaybackState.Playing)
                output.Stop();
        }
        catch (OperationCanceledException) when (playbackCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!playbackCancellation.IsCancellationRequested)
            {
                log.Warning(ex, "[XASlave] Alert When Typing In Combat direct alert playback failed.");
                try
                {
                    onPlaybackFailure?.Invoke();
                }
                catch (Exception callbackEx)
                {
                    log.Warning(callbackEx, "[XASlave] Alert When Typing In Combat could not queue fallback playback.");
                }
            }
        }
        finally
        {
            if (ownsPlaybackGate)
                TonePlaybackGate.Release();

            lock (TonePlaybackSync)
            {
                if (ReferenceEquals(activeTonePlaybackCancellation, playbackCancellation))
                    activeTonePlaybackCancellation = null;
            }

            playbackCancellation.Dispose();
        }
    }

    private static byte[] BuildAlertWaveData(int alertId)
    {
        var clampedId = Math.Clamp(alertId, 1, BaseFrequencies.Length);
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(SampleRate, 16, 1)))
        {
            foreach (var segment in GetSegments(clampedId))
            {
                WriteTone(writer, segment.Frequency, segment.DurationMs);
                WriteSilence(writer, 35);
            }
        }

        return stream.ToArray();
    }

    private static byte[] BuildToneWaveData(int toneId, int beepCount)
    {
        var frequency = BaseFrequencies[Math.Clamp(toneId, 1, BaseFrequencies.Length) - 1];
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(SampleRate, 16, 1)))
        {
            for (var index = 0; index < beepCount; index++)
            {
                WriteTone(writer, frequency, 160);
                if (index + 1 < beepCount)
                    WriteSilence(writer, 120);
            }
        }

        return stream.ToArray();
    }

    private static IEnumerable<(float Frequency, int DurationMs)> GetSegments(int alertId)
    {
        var baseFrequency = BaseFrequencies[alertId - 1];
        return alertId switch
        {
            <= 4 => new[]
            {
                (baseFrequency, 180 + (alertId * 10)),
            },
            <= 8 => new[]
            {
                (baseFrequency, 120),
                (baseFrequency * 1.12f, 120),
            },
            <= 12 => new[]
            {
                (baseFrequency, 90),
                (baseFrequency * 1.20f, 90),
                (baseFrequency, 110),
            },
            _ => new[]
            {
                (baseFrequency, 75),
                (baseFrequency * 1.12f, 75),
                (baseFrequency * 1.25f, 75),
                (baseFrequency * 1.50f, 95),
            },
        };
    }

    private static void WriteTone(WaveFileWriter writer, float frequency, int durationMs)
    {
        var sampleCount = Math.Max(1, (int)(SampleRate * (durationMs / 1000d)));
        var buffer = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var envelope = GetEnvelope(i, sampleCount);
            var value = MathF.Sin((2f * MathF.PI * frequency * i) / SampleRate) * ToneAmplitude * envelope;
            var sample = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2, 2), sample);
        }

        writer.Write(buffer, 0, buffer.Length);
    }

    private static void WriteSilence(WaveFileWriter writer, int durationMs)
    {
        var sampleCount = Math.Max(1, (int)(SampleRate * (durationMs / 1000d)));
        var buffer = new byte[sampleCount * 2];
        writer.Write(buffer, 0, buffer.Length);
    }

    private static float GetEnvelope(int sampleIndex, int totalSamples)
    {
        var attackSamples = Math.Max(1, totalSamples / 12);
        var releaseSamples = Math.Max(1, totalSamples / 8);

        if (sampleIndex < attackSamples)
            return sampleIndex / (float)attackSamples;

        var releaseStart = totalSamples - releaseSamples;
        if (sampleIndex >= releaseStart)
            return Math.Max(0f, (totalSamples - sampleIndex) / (float)releaseSamples);

        return 1f;
    }
}
