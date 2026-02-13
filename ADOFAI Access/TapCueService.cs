using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.Networking;

namespace ADOFAI_Access
{
    internal static class TapCueService
    {
        private const int MaxPoolSources = 32;
        private const string EmbeddedCueResourceName = "ADOFAI_Access.Audio.tap.wav";

        private sealed class CueSourceSlot
        {
            public AudioSource Source;
            public double BusyUntilDsp;
        }

        private static AudioSource _cueSource;
        private static readonly List<CueSourceSlot> CuePool = new List<CueSourceSlot>();
        private static AudioClip _cueClip;
        private static AudioClip _fallbackClip;
        private static bool _embeddedCueLoadAttempted;
        private static bool _isLoadingCue;
        private static bool _customCueLoadFailed;
        private static string _loadedCuePath = string.Empty;

        public static string CueFilePath
        {
            get
            {
                string gameRoot = GetGameRoot();
                return Path.Combine(gameRoot, "UserData", "ADOFAI_Access", "Audio", "tap.wav");
            }
        }

        public static void PlayCueNow()
        {
            EnsureAudioReady();
            if (_cueSource == null)
            {
                return;
            }

            AudioClip clip = SelectClip();
            if (clip != null)
            {
                _cueSource.PlayOneShot(clip, 1f);
            }
        }

        public static void PlayCueAt(double dspTime)
        {
            EnsureAudioReady();

            AudioClip clip = SelectClip();
            if (clip == null)
            {
                return;
            }

            CueSourceSlot slot = AcquirePoolSlot(dspTime, clip.length);
            if (slot == null || slot.Source == null)
            {
                return;
            }

            slot.Source.clip = clip;
            slot.Source.PlayScheduled(dspTime);
        }

        public static void StopAllCues()
        {
            if (_cueSource != null)
            {
                _cueSource.Stop();
            }

            for (int i = 0; i < CuePool.Count; i++)
            {
                CueSourceSlot slot = CuePool[i];
                if (slot?.Source == null)
                {
                    continue;
                }

                slot.Source.Stop();
                slot.BusyUntilDsp = 0d;
            }
        }

        private static CueSourceSlot AcquirePoolSlot(double dspTime, float clipLengthSeconds)
        {
            EnsureAudioSource();
            if (CuePool.Count == 0)
            {
                return null;
            }

            double nowDsp = AudioSettings.dspTime;
            double startDsp = Math.Max(dspTime, nowDsp);
            double busyUntil = startDsp + Math.Max(clipLengthSeconds, 0.05f);

            for (int i = 0; i < CuePool.Count; i++)
            {
                CueSourceSlot existing = CuePool[i];
                if (existing.BusyUntilDsp <= nowDsp + 0.0001)
                {
                    existing.BusyUntilDsp = busyUntil;
                    return existing;
                }
            }

            if (CuePool.Count >= MaxPoolSources)
            {
                return null;
            }

            CueSourceSlot created = CreateCueSourceSlot();
            created.BusyUntilDsp = busyUntil;
            CuePool.Add(created);
            return created;
        }

        private static void EnsureAudioReady()
        {
            EnsureAudioSource();
            EnsureFallbackClip();

            string cuePath = CueFilePath;
            if (_cueClip != null && string.Equals(_loadedCuePath, cuePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_isLoadingCue)
            {
                return;
            }

            if (!File.Exists(cuePath))
            {
                _cueClip = null;
                _loadedCuePath = string.Empty;
                _customCueLoadFailed = false;
                return;
            }

            _isLoadingCue = true;
            _customCueLoadFailed = false;
            MelonCoroutines.Start(LoadCueClip(cuePath));
        }

        private static void EnsureAudioSource()
        {
            if (_cueSource != null)
            {
                return;
            }

            GameObject go = new GameObject("ADOFAI_Access_TapCueAudio");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _cueSource = go.AddComponent<AudioSource>();
            _cueSource.playOnAwake = false;
            _cueSource.spatialBlend = 0f;
            _cueSource.volume = 1f;

            CuePool.Add(CreateCueSourceSlot());
            CuePool.Add(CreateCueSourceSlot());
            CuePool.Add(CreateCueSourceSlot());
            CuePool.Add(CreateCueSourceSlot());
        }

        private static void EnsureFallbackClip()
        {
            if (_fallbackClip != null)
            {
                return;
            }

            if (!_embeddedCueLoadAttempted)
            {
                _embeddedCueLoadAttempted = true;
                _fallbackClip = TryLoadEmbeddedCueClip();
                if (_fallbackClip != null)
                {
                    return;
                }
            }

            const int sampleRate = 44100;
            const float durationSeconds = 0.045f;
            int sampleCount = Mathf.CeilToInt(sampleRate * durationSeconds);
            float[] samples = new float[sampleCount];
            float frequency = 1760f;
            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = 1f - i / (float)sampleCount;
                samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.25f;
            }

            _fallbackClip = AudioClip.Create("ADOFAI_Access_DefaultPreviewCue", sampleCount, 1, sampleRate, false);
            _fallbackClip.SetData(samples, 0);
        }

        private static AudioClip TryLoadEmbeddedCueClip()
        {
            try
            {
                Assembly assembly = typeof(TapCueService).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream(EmbeddedCueResourceName))
                {
                    if (stream == null)
                    {
                        MelonLogger.Warning($"[ADOFAI Access] Embedded tap cue resource not found: {EmbeddedCueResourceName}");
                        return null;
                    }

                    AudioClip clip = CreateAudioClipFromWavStream(stream, "ADOFAI_Access_EmbeddedPreviewCue");
                    if (clip == null)
                    {
                        MelonLogger.Warning("[ADOFAI Access] Embedded tap cue could not be decoded.");
                        return null;
                    }

                    return clip;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ADOFAI Access] Failed to load embedded tap cue: {ex}");
                return null;
            }
        }

        private static AudioClip CreateAudioClipFromWavStream(Stream stream, string clipName)
        {
            using (BinaryReader reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true))
            {
                if (stream.Length < 44)
                {
                    return null;
                }

                string riff = ReadFourCc(reader);
                if (riff != "RIFF")
                {
                    return null;
                }

                reader.ReadInt32(); // RIFF chunk size.
                string wave = ReadFourCc(reader);
                if (wave != "WAVE")
                {
                    return null;
                }

                bool hasFmt = false;
                bool hasData = false;
                ushort audioFormat = 1;
                ushort channels = 1;
                int sampleRate = 44100;
                ushort bitsPerSample = 16;
                byte[] dataBytes = null;

                while (stream.Position + 8 <= stream.Length)
                {
                    string chunkId = ReadFourCc(reader);
                    int chunkSize = reader.ReadInt32();
                    if (chunkSize < 0)
                    {
                        return null;
                    }

                    long chunkDataStart = stream.Position;
                    long nextChunkPos = chunkDataStart + chunkSize;
                    if (nextChunkPos > stream.Length)
                    {
                        return null;
                    }

                    if (chunkId == "fmt ")
                    {
                        if (chunkSize < 16)
                        {
                            return null;
                        }

                        audioFormat = reader.ReadUInt16();
                        channels = reader.ReadUInt16();
                        sampleRate = reader.ReadInt32();
                        reader.ReadInt32(); // byteRate
                        reader.ReadUInt16(); // blockAlign
                        bitsPerSample = reader.ReadUInt16();
                        hasFmt = true;
                    }
                    else if (chunkId == "data")
                    {
                        dataBytes = reader.ReadBytes(chunkSize);
                        hasData = true;
                    }

                    stream.Position = nextChunkPos;
                    if ((chunkSize & 1) == 1 && stream.Position < stream.Length)
                    {
                        stream.Position++;
                    }

                    if (hasFmt && hasData)
                    {
                        break;
                    }
                }

                if (!hasFmt || !hasData || dataBytes == null || dataBytes.Length == 0 || channels == 0 || sampleRate <= 0)
                {
                    return null;
                }

                int bytesPerSample = bitsPerSample / 8;
                if (bytesPerSample <= 0)
                {
                    return null;
                }

                int totalSampleValues = dataBytes.Length / bytesPerSample;
                if (totalSampleValues <= 0)
                {
                    return null;
                }

                int frameCount = totalSampleValues / channels;
                if (frameCount <= 0)
                {
                    return null;
                }

                float[] samples = ConvertWavSamplesToFloat(dataBytes, audioFormat, bitsPerSample, totalSampleValues);
                if (samples == null || samples.Length == 0)
                {
                    return null;
                }

                AudioClip clip = AudioClip.Create(clipName, frameCount, channels, sampleRate, false);
                clip.SetData(samples, 0);
                return clip;
            }
        }

        private static float[] ConvertWavSamplesToFloat(byte[] dataBytes, ushort audioFormat, ushort bitsPerSample, int totalSampleValues)
        {
            float[] samples = new float[totalSampleValues];

            if (audioFormat == 1) // PCM
            {
                switch (bitsPerSample)
                {
                    case 8:
                        for (int i = 0; i < totalSampleValues; i++)
                        {
                            samples[i] = (dataBytes[i] - 128f) / 128f;
                        }
                        return samples;
                    case 16:
                        for (int i = 0; i < totalSampleValues; i++)
                        {
                            short sample = (short)(dataBytes[i * 2] | (dataBytes[i * 2 + 1] << 8));
                            samples[i] = sample / 32768f;
                        }
                        return samples;
                    case 24:
                        for (int i = 0; i < totalSampleValues; i++)
                        {
                            int index = i * 3;
                            int sample = dataBytes[index] | (dataBytes[index + 1] << 8) | (dataBytes[index + 2] << 16);
                            if ((sample & 0x800000) != 0)
                            {
                                sample |= unchecked((int)0xFF000000);
                            }
                            samples[i] = sample / 8388608f;
                        }
                        return samples;
                    case 32:
                        for (int i = 0; i < totalSampleValues; i++)
                        {
                            int index = i * 4;
                            int sample = dataBytes[index] | (dataBytes[index + 1] << 8) | (dataBytes[index + 2] << 16) | (dataBytes[index + 3] << 24);
                            samples[i] = sample / 2147483648f;
                        }
                        return samples;
                    default:
                        return null;
                }
            }

            if (audioFormat == 3 && bitsPerSample == 32) // IEEE float
            {
                for (int i = 0; i < totalSampleValues; i++)
                {
                    int index = i * 4;
                    samples[i] = BitConverter.ToSingle(dataBytes, index);
                }
                return samples;
            }

            return null;
        }

        private static string ReadFourCc(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(4);
            if (bytes.Length < 4)
            {
                return string.Empty;
            }
            return Encoding.ASCII.GetString(bytes, 0, 4);
        }

        private static IEnumerator LoadCueClip(string cuePath)
        {
            string uri = new Uri(cuePath).AbsoluteUri;
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        _cueClip = clip;
                        _loadedCuePath = cuePath;
                        _customCueLoadFailed = false;
                    }
                    else
                    {
                        _cueClip = null;
                        _loadedCuePath = string.Empty;
                        _customCueLoadFailed = true;
                    }
                }
                else
                {
                    MelonLogger.Warning($"[ADOFAI Access] Failed to load tap cue from {cuePath}: {request.error}");
                    _cueClip = null;
                    _loadedCuePath = string.Empty;
                    _customCueLoadFailed = true;
                }
            }

            _isLoadingCue = false;
        }

        private static AudioClip SelectClip()
        {
            string cuePath = CueFilePath;
            if (_cueClip != null && string.Equals(_loadedCuePath, cuePath, StringComparison.OrdinalIgnoreCase))
            {
                return _cueClip;
            }

            if (File.Exists(cuePath) && !_customCueLoadFailed)
            {
                // Custom cue exists; wait until it is loaded instead of playing fallback first.
                return null;
            }

            return _fallbackClip;
        }

        private static CueSourceSlot CreateCueSourceSlot()
        {
            AudioSource pooled = _cueSource.gameObject.AddComponent<AudioSource>();
            pooled.playOnAwake = false;
            pooled.spatialBlend = 0f;
            pooled.volume = 1f;
            return new CueSourceSlot
            {
                Source = pooled,
                BusyUntilDsp = 0d
            };
        }

        private static string GetGameRoot()
        {
            if (!string.IsNullOrEmpty(Application.dataPath))
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                if (!string.IsNullOrEmpty(root))
                {
                    return root;
                }
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
