using System.Diagnostics;

namespace verpixeld.Services;

/// <summary>
///     Shared PulseAudio utilities used across voice, audio, and media services.
///     Centralises the PULSE_SERVER environment variable and common pactl operations.
/// </summary>
public static class PulseAudioHelper
{
    /// <summary>
    ///     The PulseAudio server socket path used when running as root with system-mode PA.
    /// </summary>
    public const string PulseServer = "unix:/var/run/pulse/native";

    /// <summary>
    ///     Apply PULSE_SERVER to a <see cref="ProcessStartInfo"/> so that child processes
    ///     (parec, paplay, ffmpeg, etc.) connect to the correct PulseAudio daemon.
    ///     Optional <paramref name="latencyMsec"/> sets PULSE_LATENCY_MSEC for low-latency
    ///     capture/playback (visualizer + MP3).
    /// </summary>
    public static void ApplyPulseEnv(ProcessStartInfo psi, int? latencyMsec = null)
    {
        psi.Environment["PULSE_SERVER"] = PulseServer;
        if (latencyMsec is > 0)
            psi.Environment["PULSE_LATENCY_MSEC"] = latencyMsec.Value.ToString();
    }

    /// <summary>
    ///     Get all current PulseAudio sink input indexes and their volumes.
    /// </summary>
    public static async Task<List<(int Index, int Volume)>> GetSinkInputsAsync()
    {
        var results = new List<(int, int)>();
        try
        {
            var psi = new ProcessStartInfo("pactl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("sink-inputs");

            using var proc = Process.Start(psi);
            if (proc == null) return results;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            int currentIndex = -1;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Sink Input #"))
                {
                    if (int.TryParse(trimmed["Sink Input #".Length..], out var idx))
                        currentIndex = idx;
                }
                else if (trimmed.StartsWith("Volume:") && currentIndex >= 0)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"(\d+)%");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var vol))
                    {
                        results.Add((currentIndex, vol));
                        currentIndex = -1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PA] Failed to get sink inputs: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    ///     Set volume for a specific PulseAudio sink input.
    /// </summary>
    public static async Task SetSinkInputVolumeAsync(int index, int volumePercent)
    {
        try
        {
            var psi = new ProcessStartInfo("pactl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("set-sink-input-volume");
            psi.ArgumentList.Add(index.ToString());
            psi.ArgumentList.Add($"{volumePercent}%");

            using var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PA] Failed to set sink input volume: {ex.Message}");
        }
    }

    /// <summary>
    ///     Maps an ALSA device like "plughw:3,0" to a PulseAudio source name.
    /// </summary>
    public static string? FindPaSourceForAlsaDevice(string alsaDevice)
    {
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(alsaDevice, @"(\d+)");
            if (!match.Success) return null;
            var cardNum = match.Groups[1].Value;

            string? cardShortName = null;
            if (File.Exists("/proc/asound/cards"))
            {
                foreach (var line in File.ReadAllLines("/proc/asound/cards"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        line, @"^\s*" + cardNum + @"\s+\[(\w+)\s*\]");
                    if (m.Success)
                    {
                        cardShortName = m.Groups[1].Value;
                        break;
                    }
                }
            }

            var psi = new ProcessStartInfo("pactl", "list sources short")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var sourceName = parts[1].Trim();

                if (sourceName.Contains(".monitor")) continue;
                if (!sourceName.StartsWith("alsa_input.")) continue;

                if (cardShortName != null &&
                    sourceName.Contains(cardShortName, StringComparison.OrdinalIgnoreCase))
                    return sourceName;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PA] Source resolution error: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    ///     Lists currently available PulseAudio capture sources (monitors excluded).
    ///     Returns an empty list when PulseAudio is unreachable (e.g. running as root without
    ///     access to the user's PA session) or when no microphone is connected.
    /// </summary>
    public static List<string> GetInputSources()
    {
        var sources = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("pactl", "list sources short")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return sources;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var sourceName = parts[1].Trim();
                if (sourceName.Contains(".monitor")) continue; // skip output-loopback monitors
                sources.Add(sourceName);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PA] Failed to list capture sources: {ex.Message}");
        }

        return sources;
    }

    /// <summary>
    ///     Returns true when a usable capture source is available. When
    ///     <paramref name="resolvedSource" /> is supplied it must be present in the live source
    ///     list; otherwise any non-monitor input source qualifies. Returns false when PulseAudio
    ///     is unreachable or no microphone is connected.
    /// </summary>
    public static bool HasInputSource(string? resolvedSource)
    {
        var sources = GetInputSources();
        if (sources.Count == 0) return false;
        if (string.IsNullOrEmpty(resolvedSource)) return true;
        return sources.Any(s => string.Equals(s, resolvedSource, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Resolves an audio device string to a PulseAudio source name.
    ///     Handles PA source names, ALSA device IDs, and "default".
    /// </summary>
    public static string? ResolvePaSource(string? device)
    {
        if (string.IsNullOrEmpty(device))
            return null;

        if (device.StartsWith("alsa_input.") || device.StartsWith("bluez_"))
            return device;

        if (device.StartsWith("plughw:") || device.StartsWith("hw:"))
        {
            Console.WriteLine($"[PA] Converting ALSA device '{device}' to PA source...");
            var paSource = FindPaSourceForAlsaDevice(device);
            if (paSource != null)
            {
                Console.WriteLine($"[PA] Resolved to PA source: {paSource}");
                return paSource;
            }
            Console.WriteLine("[PA] Could not resolve to PA source, using default");
            return null;
        }

        if (device == "default")
            return null;

        return device;
    }

}
