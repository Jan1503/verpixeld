using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     ALSA-based audio service for media playback
///     Uses aplay for WAV playback, mikmod/xmp for MOD files
/// </summary>
public class AlsaAudioService : IDisposable
{
    // Audio generation parameters
    private const int SampleRate = 44100;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private readonly string _tempDir;
    private Process? _currentProcess;
    private readonly bool _isLinux;

    // MOD player detection
    private string? _modPlayer;
    private string? _modPlayerArgs;
    private Process? _modProcess;
    private float _volume = 0.7f;

    public AlsaAudioService()
    {
        _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        _tempDir = Path.Combine(Path.GetTempPath(), "verpixeld_audio");

        if (!Directory.Exists(_tempDir)) Directory.CreateDirectory(_tempDir);

        // Check if aplay is available
        if (_isLinux)
        {
            try
            {
                var psi = new ProcessStartInfo("which", "aplay")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit();
                IsEnabled = proc?.ExitCode == 0;

                if (IsEnabled)
                    Console.WriteLine("[AUDIO] ALSA audio enabled (aplay found)");
                else
                    Console.WriteLine("[AUDIO] ALSA audio disabled (aplay not found)");
            }
            catch
            {
                IsEnabled = false;
                Console.WriteLine("[AUDIO] ALSA audio disabled (error checking aplay)");
            }

            // Detect MOD player
            DetectModPlayer();
        }
        else
        {
            Console.WriteLine("[AUDIO] ALSA audio disabled (not running on Linux)");
            IsEnabled = false;
        }
    }

    public bool IsEnabled { get; }

    public bool ModPlayerAvailable => _modPlayer != null;
    public bool IsModPlaying { get; private set; }

    public string? CurrentModFile { get; private set; }

    public bool IsMuted { get; private set; }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            SetSystemVolume(_volume);
        }
    }

    public void Dispose()
    {
        Stop();

        // Clean up temp files
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }

    /// <summary>
    ///     Set the system volume (0.0 to 1.0) using amixer
    /// </summary>
    public void SetSystemVolume(float volume)
    {
        if (!_isLinux) return;

        var percent = (int)(Math.Clamp(volume, 0f, 1f) * 100);

        try
        {
            // Try different mixer controls
            var controls = new[] { "Master", "PCM", "Speaker", "Headphone" };

            foreach (var control in controls)
            {
                var psi = new ProcessStartInfo("amixer", $"sset {control} {percent}%")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                proc?.WaitForExit(500);

                if (proc?.ExitCode == 0)
                {
                    Console.WriteLine($"[AUDIO] Volume set to {percent}% via {control}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Failed to set volume: {ex.Message}");
        }
    }

    /// <summary>
    ///     Get the current system volume (0.0 to 1.0)
    /// </summary>
    public float GetSystemVolume()
    {
        if (!_isLinux) return _volume;

        try
        {
            var psi = new ProcessStartInfo("amixer", "get Master")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            if (proc == null) return _volume;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(500);

            // Parse output like "[50%]" or "Playback 50 [50%]"
            var match = Regex.Match(output, @"\[(\d+)%\]");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var percent)) return percent / 100f;
        }
        catch
        {
        }

        return _volume;
    }

    /// <summary>
    ///     Mute all audio output
    /// </summary>
    public void Mute()
    {
        if (!_isLinux) return;

        try
        {
            var controls = new[] { "Master", "PCM", "Speaker", "Headphone" };

            foreach (var control in controls)
            {
                var psi = new ProcessStartInfo("amixer", $"sset {control} mute")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                proc?.WaitForExit(500);
            }

            IsMuted = true;
            Console.WriteLine("[AUDIO] Muted");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Failed to mute: {ex.Message}");
        }
    }

    /// <summary>
    ///     Unmute all audio output
    /// </summary>
    public void Unmute()
    {
        if (!_isLinux) return;

        try
        {
            var controls = new[] { "Master", "PCM", "Speaker", "Headphone" };

            foreach (var control in controls)
            {
                var psi = new ProcessStartInfo("amixer", $"sset {control} unmute")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                proc?.WaitForExit(500);
            }

            IsMuted = false;
            Console.WriteLine("[AUDIO] Unmuted");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Failed to unmute: {ex.Message}");
        }
    }

    /// <summary>
    ///     Toggle mute state
    /// </summary>
    public void ToggleMute()
    {
        if (IsMuted)
            Unmute();
        else
            Mute();
    }

    /// <summary>
    ///     Detect available MOD player (mikmod, xmp, or openmpt123)
    /// </summary>
    private void DetectModPlayer()
    {
        // Try different MOD players in order of preference
        var players = new[]
        {
            ("xmp", "-l"), // Extended Module Player - loops with -l
            ("mikmod", "-q -l"), // MikMod - quiet mode, loop
            ("openmpt123", "--repeat -1") // OpenMPT - repeat forever
        };

        foreach (var (player, loopArgs) in players)
            if (IsCommandAvailable(player))
            {
                _modPlayer = player;
                _modPlayerArgs = loopArgs;
                Console.WriteLine($"[AUDIO] MOD player found: {player}");
                return;
            }

        Console.WriteLine("[AUDIO] No MOD player found (install xmp, mikmod, or openmpt123)");
    }

    private bool IsCommandAvailable(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("which", command)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Play a generated tone (non-blocking)
    /// </summary>
    public void PlayTone(float frequency, float durationMs, WaveformType waveform = WaveformType.Sine)
    {
        if (!IsEnabled) return;

        Task.Run(() =>
        {
            try
            {
                var wavFile = GenerateTone(frequency, durationMs, waveform);
                PlayWavFile(wavFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIO] Error playing tone: {ex.Message}");
            }
        });
    }

    /// <summary>
    ///     Play a sequence of notes (chip tune style)
    /// </summary>
    public void PlayMelody(Note[] notes)
    {
        if (!IsEnabled) return;

        Task.Run(async () =>
        {
            foreach (var note in notes)
                if (note.Frequency > 0)
                {
                    var wavFile = GenerateTone(note.Frequency, note.DurationMs, note.Waveform);
                    PlayWavFileSync(wavFile);
                }
                else
                {
                    // Rest
                    await Task.Delay((int)note.DurationMs);
                }
        });
    }

    /// <summary>
    ///     Play white noise (fire effect, etc.)
    /// </summary>
    public void PlayNoise(float durationMs)
    {
        if (!IsEnabled) return;

        Task.Run(() =>
        {
            try
            {
                var wavFile = GenerateNoise(durationMs);
                PlayWavFile(wavFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIO] Error playing noise: {ex.Message}");
            }
        });
    }

    /// <summary>
    ///     Generate and play a demoscene-style bass drum
    /// </summary>
    public void PlayKick()
    {
        if (!IsEnabled) return;

        Task.Run(() =>
        {
            try
            {
                var wavFile = GenerateKick();
                PlayWavFile(wavFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIO] Error playing kick: {ex.Message}");
            }
        });
    }

    /// <summary>
    ///     Generate and play a hi-hat sound
    /// </summary>
    public void PlayHiHat()
    {
        if (!IsEnabled) return;

        Task.Run(() =>
        {
            try
            {
                var wavFile = GenerateHiHat();
                PlayWavFile(wavFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIO] Error playing hi-hat: {ex.Message}");
            }
        });
    }

    /// <summary>
    ///     Play an arpeggio (classic demo scene sound)
    /// </summary>
    public void PlayArpeggio(float baseFreq, int[] semitones, float noteLength = 50f, int repeats = 4)
    {
        if (!IsEnabled) return;

        var notes = new List<Note>();
        for (var r = 0; r < repeats; r++)
            foreach (var semi in semitones)
            {
                var freq = baseFreq * MathF.Pow(2f, semi / 12f);
                notes.Add(new Note(freq, noteLength, WaveformType.Square));
            }

        PlayMelody(notes.ToArray());
    }

    /// <summary>
    ///     Stop any currently playing audio
    /// </summary>
    public void Stop()
    {
        StopMod();

        try
        {
            _currentProcess?.Kill();
            _currentProcess = null;
        }
        catch
        {
        }
    }

    /// <summary>
    ///     Play a MOD/XM/S3M/IT file in a loop
    /// </summary>
    /// <param name="modFilePath">Path to the MOD file</param>
    /// <returns>True if playback started successfully</returns>
    public bool PlayMod(string modFilePath)
    {
        if (_modPlayer == null)
        {
            Console.WriteLine("[AUDIO] No MOD player available");
            return false;
        }

        if (!File.Exists(modFilePath))
        {
            Console.WriteLine($"[AUDIO] MOD file not found: {modFilePath}");
            return false;
        }

        // Stop any currently playing MOD
        StopMod();

        try
        {
            var args = $"{_modPlayerArgs} \"{modFilePath}\"";
            Console.WriteLine($"[AUDIO] Starting MOD playback: {_modPlayer} {args}");

            var psi = new ProcessStartInfo(_modPlayer, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _modProcess = Process.Start(psi);

            if (_modProcess != null)
            {
                IsModPlaying = true;
                CurrentModFile = Path.GetFileName(modFilePath);
                Console.WriteLine($"[AUDIO] MOD playback started: {CurrentModFile}");

                // Monitor for process exit
                Task.Run(async () =>
                {
                    try
                    {
                        await _modProcess.WaitForExitAsync();
                        IsModPlaying = false;
                        CurrentModFile = null;
                        Console.WriteLine("[AUDIO] MOD playback ended");
                    }
                    catch
                    {
                    }
                });

                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Error starting MOD playback: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    ///     Stop MOD playback
    /// </summary>
    public void StopMod()
    {
        if (_modProcess != null)
            try
            {
                if (!_modProcess.HasExited)
                {
                    _modProcess.Kill();
                    Console.WriteLine("[AUDIO] MOD playback stopped");
                }
            }
            catch
            {
            }
            finally
            {
                _modProcess = null;
                IsModPlaying = false;
                CurrentModFile = null;
            }
    }

    private string GenerateTone(float frequency, float durationMs, WaveformType waveform)
    {
        var numSamples = (int)(SampleRate * durationMs / 1000f);
        var samples = new short[numSamples];
        var amplitude = (short)(short.MaxValue * _volume);

        for (var i = 0; i < numSamples; i++)
        {
            var t = (float)i / SampleRate;
            var value = waveform switch
            {
                WaveformType.Sine => MathF.Sin(2 * MathF.PI * frequency * t),
                WaveformType.Square => MathF.Sin(2 * MathF.PI * frequency * t) >= 0 ? 1f : -1f,
                WaveformType.Triangle => 2f * MathF.Abs(2f * (frequency * t - MathF.Floor(frequency * t + 0.5f))) - 1f,
                WaveformType.Sawtooth => 2f * (frequency * t - MathF.Floor(frequency * t + 0.5f)),
                _ => MathF.Sin(2 * MathF.PI * frequency * t)
            };

            // Apply simple envelope to reduce clicks
            var envelope = 1f;
            var fadeLen = Math.Min(numSamples / 10, 500);
            if (i < fadeLen) envelope = (float)i / fadeLen;
            else if (i > numSamples - fadeLen) envelope = (float)(numSamples - i) / fadeLen;

            samples[i] = (short)(value * amplitude * envelope);
        }

        return WriteWavFile(samples);
    }

    private string GenerateNoise(float durationMs)
    {
        var numSamples = (int)(SampleRate * durationMs / 1000f);
        var samples = new short[numSamples];
        var random = new Random();
        var amplitude = (short)(short.MaxValue * _volume * 0.5f);

        for (var i = 0; i < numSamples; i++)
        {
            // Apply envelope for smoother sound
            var envelope = 1f;
            var fadeLen = Math.Min(numSamples / 10, 500);
            if (i < fadeLen) envelope = (float)i / fadeLen;
            else if (i > numSamples - fadeLen) envelope = (float)(numSamples - i) / fadeLen;

            samples[i] = (short)((random.NextDouble() * 2 - 1) * amplitude * envelope);
        }

        return WriteWavFile(samples);
    }

    private string GenerateKick()
    {
        const float duration = 150f; // ms
        var numSamples = (int)(SampleRate * duration / 1000f);
        var samples = new short[numSamples];
        var amplitude = (short)(short.MaxValue * _volume);

        for (var i = 0; i < numSamples; i++)
        {
            var t = (float)i / SampleRate;
            var progress = (float)i / numSamples;

            // Frequency sweep from 150Hz down to 40Hz
            var freq = 150f * MathF.Pow(0.27f, progress);

            // Sine wave with pitch drop
            var value = MathF.Sin(2 * MathF.PI * freq * t);

            // Exponential decay envelope
            var envelope = MathF.Exp(-progress * 5f);

            samples[i] = (short)(value * amplitude * envelope);
        }

        return WriteWavFile(samples);
    }

    private string GenerateHiHat()
    {
        const float duration = 50f; // ms
        var numSamples = (int)(SampleRate * duration / 1000f);
        var samples = new short[numSamples];
        var random = new Random();
        var amplitude = (short)(short.MaxValue * _volume * 0.3f);

        for (var i = 0; i < numSamples; i++)
        {
            var progress = (float)i / numSamples;

            // High-pass filtered noise (simulated by mixing)
            var noise = (float)(random.NextDouble() * 2 - 1);

            // Fast exponential decay
            var envelope = MathF.Exp(-progress * 15f);

            samples[i] = (short)(noise * amplitude * envelope);
        }

        return WriteWavFile(samples);
    }

    private string WriteWavFile(short[] samples)
    {
        var filename = Path.Combine(_tempDir, $"audio_{Guid.NewGuid():N}.wav");

        using var fs = new FileStream(filename, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        var dataSize = samples.Length * 2; // 16-bit = 2 bytes per sample
        var fileSize = 44 + dataSize - 8;

        // WAV header
        bw.Write("RIFF"u8);
        bw.Write(fileSize);
        bw.Write("WAVE"u8);

        // Format chunk
        bw.Write("fmt "u8);
        bw.Write(16); // Chunk size
        bw.Write((short)1); // PCM format
        bw.Write((short)Channels);
        bw.Write(SampleRate);
        bw.Write(SampleRate * Channels * BitsPerSample / 8); // Byte rate
        bw.Write((short)(Channels * BitsPerSample / 8)); // Block align
        bw.Write((short)BitsPerSample);

        // Data chunk
        bw.Write("data"u8);
        bw.Write(dataSize);

        foreach (var sample in samples) bw.Write(sample);

        return filename;
    }

    private void PlayWavFile(string path)
    {
        if (!_isLinux || !File.Exists(path)) return;

        try
        {
            var psi = new ProcessStartInfo("aplay", $"-q \"{path}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _currentProcess = Process.Start(psi);

            // Clean up file after playback completes
            Task.Run(async () =>
            {
                await Task.Delay(5000); // Wait a bit before cleanup
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] aplay error: {ex.Message}");
        }
    }

    private void PlayWavFileSync(string path)
    {
        if (!_isLinux || !File.Exists(path)) return;

        try
        {
            var psi = new ProcessStartInfo("aplay", $"-q \"{path}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            proc?.WaitForExit();

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] aplay error: {ex.Message}");
        }
    }
}

/// <summary>
///     Waveform types for procedural audio
/// </summary>
public enum WaveformType
{
    Sine,
    Square,
    Triangle,
    Sawtooth
}

/// <summary>
///     Musical note definition
/// </summary>
public struct Note
{
    public float Frequency { get; }
    public float DurationMs { get; }
    public WaveformType Waveform { get; }

    public Note(float frequency, float durationMs, WaveformType waveform = WaveformType.Sine)
    {
        Frequency = frequency;
        DurationMs = durationMs;
        Waveform = waveform;
    }

    // Common note frequencies (A4 = 440Hz)
    public static class Frequencies
    {
        public const float C4 = 261.63f;
        public const float D4 = 293.66f;
        public const float E4 = 329.63f;
        public const float F4 = 349.23f;
        public const float G4 = 392.00f;
        public const float A4 = 440.00f;
        public const float B4 = 493.88f;
        public const float C5 = 523.25f;
        public const float D5 = 587.33f;
        public const float E5 = 659.25f;
        public const float F5 = 698.46f;
        public const float G5 = 783.99f;
        public const float A5 = 880.00f;
    }
}
