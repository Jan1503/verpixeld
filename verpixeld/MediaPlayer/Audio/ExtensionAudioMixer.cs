using System.Collections.Concurrent;
using CanvasManagement.Interfaces;
using CanvasManagement;
using PixTone;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     The only extension path to the audio board. One-shots and loops come from
///     <see cref="IExtensionAudio"/>. Extensions that implement <see cref="ICanvasAudioSource"/>
///     are mixed in as well. A playing media stream keeps the board; otherwise the first
///     audible extension owns it.
/// </summary>
public sealed class ExtensionAudioMixer : IExtensionAudio, IDisposable
{
    private const int SampleRate = 48000;
    private const int Frames = 360;

    /// <summary>
    ///     After the last voice ends the stream keeps sending silence this long. Short gaps between
    ///     effects then stay on one schedule; a fresh stream would make the board re-buffer and cut them.
    /// </summary>
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(3);

    private readonly PixToneOutputService _vap1;
    private readonly ExtensionVoiceMixer _voices = new(SampleRate);
    private readonly ConcurrentDictionary<ICanvasAudioSource, byte> _sources = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _loggedIdle;

    public ExtensionAudioMixer(PixToneOutputService vap1)
    {
        _vap1 = vap1;
    }

    public void Start()
    {
        Task.Run(() => Run(_cts.Token));
        Task.Run(() => PanelLoop(_cts.Token));
    }

    /// <summary>Running extensions (display name, instance); set by the content manager.</summary>
    public Func<IReadOnlyList<(string Name, object? Instance)>>? RunningExtensions { get; set; }

    private string? _panelKey;

    /// <summary>
    ///     While no medium owns the audio board (film, YouTube, radio), the panel shows what runs on the wall:
    ///     an extension that plays a medium (<see cref="ICanvasNowPlaying"/>, e.g. VLC) with its title and cover,
    ///     otherwise the names of the running extensions and no cover. So a game no longer sits under the
    ///     poster of the last film.
    /// </summary>
    private async Task PanelLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                try
                {
                    if (!_vap1.IsSelected || string.IsNullOrWhiteSpace(_vap1.Host)) continue;
                    if (_vap1.IsStreaming)
                    {
                        // The media player or radio set their own title; take over again when they stop.
                        _panelKey = null;
                        continue;
                    }

                    var running = RunningExtensions?.Invoke() ?? Array.Empty<(string, object?)>();
                    string? title = null, cover = null;
                    foreach (var (_, instance) in running)
                    {
                        var inner = instance is DynamicExtension d ? d.Instance : instance;
                        if (inner is ICanvasNowPlaying np && !string.IsNullOrWhiteSpace(np.NowPlayingTitle))
                        {
                            title = np.NowPlayingTitle;
                            cover = np.NowPlayingCover;
                            break;
                        }
                    }
                    if (title == null && running.Count > 0)
                        title = string.Join(" · ", running.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());

                    var key = (title ?? "") + "\n" + (cover ?? "");
                    if (key == _panelKey) continue;
                    // Nothing running and nothing shown by us yet: leave whatever the panel has.
                    if (_panelKey == null && running.Count == 0) continue;
                    _panelKey = key;
                    _vap1.SetTrackTitle(title ?? "");
                    _vap1.ShowCover(cover);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[AUDIO] panel title: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Attach(ICanvasAudioSource source) => _sources[source] = 0;

    public void Detach(ICanvasAudioSource source) => _sources.TryRemove(source, out _);

    public void Tone(float hz, float seconds, float gain, float pan) => _voices.Tone(hz, seconds, gain, pan);

    public void Noise(float seconds, float gain, float pan) => _voices.Noise(seconds, gain, pan);

    public void Loop(int slot, float hz, float gain) => _voices.Loop(slot, hz, gain);

    public void StopLoop(int slot) => _voices.StopLoop(slot);

    public void Play(float[] mono, int sampleRate, float gain, float pan, float playbackRate) =>
        _voices.Play(mono, sampleRate, gain, pan, playbackRate);

    public void LoopSample(int slot, float[] mono, int sampleRate, float gain, float playbackRate) =>
        _voices.LoopSample(slot, mono, sampleRate, gain, playbackRate);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private bool HasWork() => _voices.HasWork || _sources.Keys.Any(s => s.IsAudible);

    private async Task Run(CancellationToken ct)
    {
        PixToneSender? sender = null;
        var idleSince = DateTime.UtcNow;
        var stereo = new float[Frames * 2];
        var pcm = new byte[Frames * 4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_vap1.BlocksExtensions || !_vap1.IsSelected || string.IsNullOrWhiteSpace(_vap1.Host))
                {
                    sender?.Dispose();
                    sender = null;
                    _voices.Clear();
                    if (!_loggedIdle)
                    {
                        _loggedIdle = true;
                        Console.WriteLine(_vap1.BlocksExtensions
                            ? "[AUDIO] extension sound waits until the movie stops"
                            : "[AUDIO] extension sound has no audio board selected");
                    }
                    await Task.Delay(40, ct).ConfigureAwait(false);
                    continue;
                }

                _loggedIdle = false;

                if (HasWork())
                {
                    idleSince = DateTime.UtcNow;
                }
                else if (sender == null || DateTime.UtcNow - idleSince > KeepAlive)
                {
                    sender?.Dispose();
                    sender = null;
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    continue;
                }

                if (sender == null)
                    Console.WriteLine($"[AUDIO] extension -> {_vap1.Host} vol {_vap1.Volume}");
                sender ??= new PixToneSender(_vap1.Host);
                Array.Clear(stereo);
                _voices.Render(stereo, MixSources);
                WritePcm(stereo, pcm);
                try
                {
                    // Blocks until the packet is due, so this loop runs at 48 kHz.
                    sender.Submit(pcm);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[AUDIO] " + ex.Message);
                    sender.Dispose();
                    sender = null;
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            sender?.Dispose();
        }
    }

    private void MixSources(Span<float> stereo)
    {
        if (_sources.IsEmpty) return;
        foreach (var source in _sources.Keys)
        {
            if (!source.IsAudible) continue;
            try { source.Mix(stereo, SampleRate); }
            catch (Exception ex) { Console.WriteLine("[AUDIO] extension mix: " + ex.Message); }
        }
    }

    private static void WritePcm(float[] stereo, byte[] pcm)
    {
        for (var i = 0; i < stereo.Length; i++)
        {
            var s = (short)Math.Clamp((int)(stereo[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)s;
            pcm[i * 2 + 1] = (byte)(s >> 8);
        }
    }
}
