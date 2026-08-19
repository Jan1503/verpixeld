using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using verpixeld.Services;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     Service for managing audio output devices via PulseAudio.
///     Bluetooth management is handled by <see cref="BluetoothAudioService"/>.
/// </summary>
public class AudioOutputService : IAudioOutputService
{
    /// <summary>Available audio output types.</summary>
    public enum AudioOutputType
    {
        Default,
        HDMI,
        Analog, // 3.5mm jack
        USB,
        Bluetooth
    }

    // Cache for PulseAudio mode detection
    private bool? _isPulseAudioSystemMode;
    private bool _hasLoggedPulseMode;

    // Cache whether FFmpeg has PulseAudio support
    private bool? _ffmpegHasPulseSupport;

    private readonly object _lock = new();

    public string CurrentSinkName { get; private set; } = "auto_null";

    /// <summary>
    ///     Whether PulseAudio is running in system mode (as user 'pulse' with --system flag).
    ///     Exposed for <see cref="BluetoothAudioService"/> to use.
    /// </summary>
    public bool IsPulseAudioSystemMode => IsPulseAudioSystemModeInternal();

    // ========================================================================
    // PULSEAUDIO DETECTION
    // ========================================================================

    private bool IsPulseAudioSystemModeInternal()
    {
        if (_isPulseAudioSystemMode.HasValue)
            return _isPulseAudioSystemMode.Value;

        try
        {
            var psi = new ProcessStartInfo("pgrep", "-af pulseaudio")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _isPulseAudioSystemMode = false;
                return false;
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            _isPulseAudioSystemMode = output.Contains("--system");
            Console.WriteLine($"[AUDIO] PulseAudio system mode: {_isPulseAudioSystemMode.Value}");
            return _isPulseAudioSystemMode.Value;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Error detecting PulseAudio mode: {ex.Message}");
            _isPulseAudioSystemMode = false;
            return false;
        }
    }

    /// <summary>
    ///     Create a ProcessStartInfo for running pactl commands.
    ///     Handles both system-mode and user-mode PulseAudio.
    ///     Exposed for <see cref="BluetoothAudioService"/> to run pactl commands in the correct context.
    /// </summary>
    public ProcessStartInfo CreatePactlProcessStartInfo(string args)
    {
        var psi = new ProcessStartInfo("pactl", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (IsPulseAudioSystemModeInternal())
        {
            PulseAudioHelper.ApplyPulseEnv(psi);

            if (!_hasLoggedPulseMode)
            {
                Console.WriteLine($"[AUDIO] Using system-mode PulseAudio (PULSE_SERVER={PulseAudioHelper.PulseServer})");
                _hasLoggedPulseMode = true;
            }
        }
        else
        {
            var isRoot = Environment.UserName == "root" || geteuid() == 0;
            if (isRoot)
            {
                var uid = GetUserUid("pi");
                psi = new ProcessStartInfo("sudo",
                    $"-u pi env XDG_RUNTIME_DIR=/run/user/{uid} DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{uid}/bus pactl {args}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (!_hasLoggedPulseMode)
                {
                    Console.WriteLine($"[AUDIO] Using user-mode PulseAudio (running as pi user, uid {uid})");
                    _hasLoggedPulseMode = true;
                }
            }
        }

        return psi;
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private static int GetUserUid(string username)
    {
        try
        {
            var psi = new ProcessStartInfo("id", $"-u {username}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return 1000;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return int.TryParse(output, out var uid) ? uid : 1000;
        }
        catch
        {
            return 1000;
        }
    }

    // ========================================================================
    // AUDIO OUTPUT MANAGEMENT
    // ========================================================================

    public bool IsPulseAudioAvailable()
    {
        try
        {
            var psi = CreatePactlProcessStartInfo("info");
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private bool IsFFmpegPulseSupported()
    {
        if (_ffmpegHasPulseSupport.HasValue)
            return _ffmpegHasPulseSupport.Value;

        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-formats")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                _ffmpegHasPulseSupport = output.Contains(" pulse ");

                if (_ffmpegHasPulseSupport.Value)
                    Console.WriteLine("[AUDIO] FFmpeg has PulseAudio output support");
                else
                {
                    Console.WriteLine("[AUDIO] FFmpeg does NOT have PulseAudio support - will use ALSA");
                    Console.WriteLine("[AUDIO] To enable PulseAudio: recompile FFmpeg with --enable-libpulse");
                }

                return _ffmpegHasPulseSupport.Value;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Error checking FFmpeg capabilities: {ex.Message}");
        }

        _ffmpegHasPulseSupport = false;
        return false;
    }

    public string GetFFmpegAudioOutput()
    {
        if (!IsFFmpegPulseSupported())
        {
            Console.WriteLine("[AUDIO] Using ALSA (FFmpeg lacks PulseAudio support)");
            return "-f alsa default";
        }

        if (IsPulseAudioAvailable())
        {
            try
            {
                var psi = CreatePactlProcessStartInfo("get-default-sink");
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var defaultSink = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(2000);

                    if (!string.IsNullOrEmpty(defaultSink) && proc.ExitCode == 0)
                    {
                        Console.WriteLine($"[AUDIO] Using PulseAudio with default sink: {defaultSink}");
                        return "-f pulse default";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIO] Error checking PulseAudio default sink: {ex.Message}");
            }

            Console.WriteLine("[AUDIO] Using PulseAudio (no default sink detected)");
            return "-f pulse default";
        }

        Console.WriteLine("[AUDIO] Using ALSA for audio output");
        return "-f alsa default";
    }

    public async Task<List<AudioSink>> GetAudioSinksAsync()
    {
        var sinks = new List<AudioSink>();

        if (!IsPulseAudioAvailable())
        {
            sinks.Add(new AudioSink
            {
                Name = "default",
                Description = "Default ALSA Output",
                Type = AudioOutputType.Default,
                IsDefault = true
            });
            return sinks;
        }

        try
        {
            var psi = CreatePactlProcessStartInfo("list sinks");
            using var proc = Process.Start(psi);
            if (proc == null) return sinks;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var defaultSink = await GetDefaultSinkAsync();
            var sinkBlocks = output.Split("Sink #", StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in sinkBlocks)
            {
                var sink = ParseSinkBlock(block);
                if (sink != null)
                {
                    sink.IsDefault = sink.Name == defaultSink;
                    sinks.Add(sink);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Error listing sinks: {ex.Message}");
        }

        return sinks;
    }

    private AudioSink? ParseSinkBlock(string block)
    {
        try
        {
            var sink = new AudioSink();

            var nameMatch = Regex.Match(block, @"Name:\s*(.+)");
            if (nameMatch.Success)
                sink.Name = nameMatch.Groups[1].Value.Trim();
            else return null;

            var descMatch = Regex.Match(block, @"Description:\s*(.+)");
            if (descMatch.Success) sink.Description = descMatch.Groups[1].Value.Trim();

            var volMatch = Regex.Match(block, @"Volume:.*?(\d+)%");
            if (volMatch.Success && int.TryParse(volMatch.Groups[1].Value, out var vol)) sink.Volume = vol;

            sink.IsMuted = block.Contains("Mute: yes");

            var lowerName = sink.Name.ToLowerInvariant();
            var lowerDesc = sink.Description.ToLowerInvariant();

            if (lowerName.Contains("bluez") || lowerDesc.Contains("bluetooth"))
            {
                sink.Type = AudioOutputType.Bluetooth;
                var addrMatch = Regex.Match(sink.Name,
                    @"([0-9A-F]{2}[_:][0-9A-F]{2}[_:][0-9A-F]{2}[_:][0-9A-F]{2}[_:][0-9A-F]{2}[_:][0-9A-F]{2})",
                    RegexOptions.IgnoreCase);
                if (addrMatch.Success) sink.BluetoothAddress = addrMatch.Groups[1].Value.Replace("_", ":");
            }
            else if (lowerName.Contains("hdmi") || lowerDesc.Contains("hdmi"))
            {
                sink.Type = AudioOutputType.HDMI;
            }
            else if (lowerName.Contains("usb") || lowerDesc.Contains("usb"))
            {
                sink.Type = AudioOutputType.USB;
            }
            else if (lowerName.Contains("analog") || lowerDesc.Contains("analog") ||
                     lowerName.Contains("headphone") || lowerDesc.Contains("headphone") ||
                     lowerName.Contains("bcm2835") || lowerDesc.Contains("built-in"))
            {
                sink.Type = AudioOutputType.Analog;
            }
            else
            {
                sink.Type = AudioOutputType.Default;
            }

            return sink;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> GetDefaultSinkAsync()
    {
        try
        {
            var psi = CreatePactlProcessStartInfo("get-default-sink");
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output.Trim();
        }
        catch
        {
            return "";
        }
    }

    public async Task<bool> SetDefaultSinkAsync(string sinkName)
    {
        if (!IsPulseAudioAvailable())
        {
            Console.WriteLine("[AUDIO] PulseAudio not available, cannot change sink");
            return false;
        }

        try
        {
            var psi = CreatePactlProcessStartInfo($"set-default-sink {sinkName}");
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                lock (_lock)
                {
                    CurrentSinkName = sinkName;
                }
                Console.WriteLine($"[AUDIO] Default sink set to: {sinkName}");
                return true;
            }

            var error = await proc.StandardError.ReadToEndAsync();
            Console.WriteLine($"[AUDIO] Failed to set sink: {error}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Error setting sink: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetVolumeAsync(int volumePercent)
    {
        volumePercent = Math.Clamp(volumePercent, 0, 150);

        try
        {
            Console.WriteLine($"[AUDIO] Setting PulseAudio sink volume to {volumePercent}%");

            var psi = CreatePactlProcessStartInfo($"set-sink-volume @DEFAULT_SINK@ {volumePercent}%");
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0) Console.WriteLine($"[AUDIO] pactl set-sink-volume failed: {stderr}");
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Error setting PulseAudio volume: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ToggleMuteAsync()
    {
        try
        {
            var psi = CreatePactlProcessStartInfo("set-sink-mute @DEFAULT_SINK@ toggle");
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIO] Error toggling mute: {ex.Message}");
            return false;
        }
    }

    // ========================================================================
    // MODELS
    // ========================================================================

    public class AudioSink
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public AudioOutputType Type { get; set; }
        public bool IsDefault { get; set; }
        public int Volume { get; set; }
        public bool IsMuted { get; set; }
        public string? BluetoothAddress { get; set; }
    }
}
