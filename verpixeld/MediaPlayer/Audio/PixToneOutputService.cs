using System.Diagnostics;
using System.Text.Json.Nodes;
using verpixeld.Configuration;
using PixTone;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     The selected verpixeld audio board. FFmpeg stays here; packets go out through <see cref="PixToneSender"/>.
/// </summary>
public sealed class PixToneOutputService
{
    private readonly object _gate = new();
    private TaskCompletionSource _audioStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PixToneSender? _liveSender;
    private List<AudioAnnouncement> _seen = [];

    public PixToneOutputService() => Load();

    public bool IsSelected { get; private set; }
    public string Host { get; private set; } = "";
    public int Volume { get; private set; } = 70;
    public bool Muted { get; private set; }

    public IReadOnlyList<AudioAnnouncement> Seen
    {
        get { lock (_gate) return _seen.ToList(); }
    }

    public void Select(string host)
    {
        host = host.Trim();
        if (!System.Net.IPAddress.TryParse(host, out _))
            throw new ArgumentException("IPv4 address required");
        Host = host;
        IsSelected = true;
        Save();
        PushVolume();
    }

    public void Deselect()
    {
        if (!IsSelected) return;
        IsSelected = false;
        Save();
    }

    public void SetVolume(int volumePercent, bool? mute = null)
    {
        Volume = Math.Clamp(volumePercent, 0, 100);
        if (mute.HasValue) Muted = mute.Value;
        Save();
        PushVolume();
    }

    public void ToggleMute()
    {
        Muted = !Muted;
        Save();
        PushVolume();
    }

    public async Task<IReadOnlyList<AudioAnnouncement>> ScanAsync(CancellationToken ct = default)
    {
        var found = await AudioDiscovery.ScanAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        lock (_gate) _seen = found.ToList();
        if (!string.IsNullOrWhiteSpace(Host))
        {
            var known = await AudioDiscovery.QueryStatusAsync(Host, ct: ct).ConfigureAwait(false);
            if (known != null)
            {
                lock (_gate)
                {
                    _seen.RemoveAll(b => b.Host == known.Host);
                    _seen.Insert(0, known);
                }
            }
        }
        return Seen;
    }

    /// <summary>
    ///     Starts FFmpeg writing 48 kHz stereo s16le to stdout and pumps it to the board.
    ///     The returned process is the caller's to kill; closing it ends the pump.
    /// </summary>
    public Process? StartDecoder(string argsBeforePcm, string? title, CancellationToken ct)
    {
        if (!IsSelected || string.IsNullOrWhiteSpace(Host)) return null;
        var args = argsBeforePcm.Trim() + " -c:a pcm_s16le -ar 48000 -ac 2 -f s16le pipe:1";
        var psi = new ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _audioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = _audioStarted;
        var proc = Process.Start(psi);
        if (proc == null) return null;

        var host = Host;
        var volume = Volume;
        var muted = Muted;
        var stdout = proc.StandardOutput.BaseStream;
        var stderr = proc.StandardError;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await stderr.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("[PIXTONE] " + line);
                }
            }
            catch { /* process gone */ }
        }, ct);
        _ = Task.Run(async () =>
        {
        var sender = new PixToneSender(host);
        _liveSender = sender;
        IsStreaming = true;
        try
        {
            sender.SetVolume(volume, muted);
            if (!string.IsNullOrWhiteSpace(title)) sender.SetTitle(title);
            await sender.PumpAsync(stdout, ct, () => started.TrySetResult()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("[PIXTONE] " + ex.Message); }
        finally
        {
            IsStreaming = false;
            if (ReferenceEquals(_liveSender, sender)) _liveSender = null;
            sender.Dispose();
        }
        }, ct);
        Console.WriteLine($"[PIXTONE] streaming to {host}:{PixToneProtocol.Port}");
        return proc;
    }

    /// <summary>True while a decoder is open, including while it is paused.</summary>
    public bool IsStreaming { get; private set; }

    /// <summary>True while a movie is actually feeding the board. A paused movie does not count.</summary>
    public bool BlocksExtensions => IsStreaming && _liveSender?.IsPaused != true;

    public void PauseClock() => _liveSender?.PauseClock();

    public void ResumeClock() => _liveSender?.ResumeClock();

    /// <summary>Completes when the first PCM packet has been handed to the board.</summary>
    public async Task WaitForAudioStartAsync(TimeSpan timeout, CancellationToken ct)
    {
        var started = _audioStarted.Task;
        await Task.WhenAny(started, Task.Delay(timeout, ct)).ConfigureAwait(false);
    }

    private void PushVolume()
    {
        if (string.IsNullOrWhiteSpace(Host)) return;
        try
        {
            using var sender = new PixToneSender(Host);
            sender.SetVolume(Volume, Muted);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PIXTONE] volume: " + ex.Message);
        }
    }

    private void Load()
    {
        var o = AppSettingsStore.Get<PixToneOptions>("PixTone");
        if (string.IsNullOrWhiteSpace(o.Host))
            o = AppSettingsStore.Get<PixToneOptions>("Vap1");
        Host = o.Host ?? "";
        IsSelected = o.Selected && !string.IsNullOrWhiteSpace(Host);
        Volume = Math.Clamp(o.Volume, 0, 100);
        Muted = o.Muted;
    }

    private void Save()
    {
        var root = AppSettingsStore.Load();
        var section = AppSettingsStore.Section(root, "PixTone");
        AppSettingsStore.Set(section, "Host", JsonValue.Create(Host));
        AppSettingsStore.Set(section, "Selected", JsonValue.Create(IsSelected));
        AppSettingsStore.Set(section, "Volume", JsonValue.Create(Volume));
        AppSettingsStore.Set(section, "Muted", JsonValue.Create(Muted));
        AppSettingsStore.Save(root);
    }

    private sealed class PixToneOptions
    {
        public string? Host { get; set; }
        public bool Selected { get; set; }
        public int Volume { get; set; } = 70;
        public bool Muted { get; set; }
    }
}
