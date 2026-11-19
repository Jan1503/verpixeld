using System.Collections.Concurrent;
using CanvasManagement.Interfaces;
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
    private const int Slots = 4;
    private const int MaxTones = 12;
    private const int MaxNoise = 6;

    private readonly PixToneOutputService _vap1;
    private readonly object _lock = new();
    private readonly LoopVoice[] _loops = new LoopVoice[Slots];
    private readonly List<ToneVoice> _tones = new();
    private readonly List<NoiseVoice> _noises = new();
    private readonly List<SampleVoice> _shots = new();
    private readonly ConcurrentDictionary<ICanvasAudioSource, byte> _sources = new();
    private readonly CancellationTokenSource _cts = new();
    private uint _rng = 0x6d2b79f5;
    private int _appliedVolume = -1;
    private bool _appliedMute;
    private bool _loggedIdle;

    public ExtensionAudioMixer(PixToneOutputService vap1)
    {
        _vap1 = vap1;
        for (var i = 0; i < Slots; i++) _loops[i] = new LoopVoice();
    }

    public void Start() => Task.Run(() => Run(_cts.Token));

    public void Attach(ICanvasAudioSource source) => _sources[source] = 0;

    public void Detach(ICanvasAudioSource source) => _sources.TryRemove(source, out _);

    public void Tone(float hz, float seconds, float gain, float pan)
    {
        if (hz < 20 || seconds <= 0 || gain <= 0) return;
        lock (_lock)
        {
            if (_tones.Count >= MaxTones) _tones.RemoveAt(0);
            _tones.Add(new ToneVoice
            {
                Hz = hz,
                Gain = Math.Clamp(gain, 0, 1),
                Pan = Math.Clamp(pan, -1, 1),
                Left = (int)(Math.Clamp(seconds, 0.02f, 4f) * SampleRate)
            });
        }
    }

    public void Noise(float seconds, float gain, float pan)
    {
        if (seconds <= 0 || gain <= 0) return;
        lock (_lock)
        {
            if (_noises.Count >= MaxNoise) _noises.RemoveAt(0);
            _noises.Add(new NoiseVoice
            {
                Gain = Math.Clamp(gain, 0, 1),
                Pan = Math.Clamp(pan, -1, 1),
                Left = (int)(Math.Clamp(seconds, 0.02f, 2f) * SampleRate)
            });
        }
    }

    public void Loop(int slot, float hz, float gain)
    {
        slot = Math.Clamp(slot, 0, Slots - 1);
        lock (_lock)
        {
            var v = _loops[slot];
            v.Sample = null;
            v.Hz = Math.Clamp(hz, 20, 4000);
            v.Gain = Math.Clamp(gain, 0, 1);
            v.On = v.Gain > 0.001f;
        }
    }

    public void StopLoop(int slot)
    {
        slot = Math.Clamp(slot, 0, Slots - 1);
        lock (_lock) _loops[slot].On = false;
    }

    public void Play(float[] mono, int sampleRate, float gain, float pan, float playbackRate)
    {
        if (mono.Length < 2 || gain <= 0 || sampleRate < 8000) return;
        var copy = new float[mono.Length];
        mono.AsSpan().CopyTo(copy);
        lock (_lock)
        {
            if (_shots.Count >= MaxTones) _shots.RemoveAt(0);
            _shots.Add(new SampleVoice
            {
                Data = copy,
                SampleRate = sampleRate,
                Gain = Math.Clamp(gain, 0, 1),
                Pan = Math.Clamp(pan, -1, 1),
                Rate = Math.Clamp(playbackRate, 0.25f, 3f)
            });
        }
    }

    public void LoopSample(int slot, float[] mono, int sampleRate, float gain, float playbackRate)
    {
        slot = Math.Clamp(slot, 0, Slots - 1);
        lock (_lock)
        {
            var v = _loops[slot];
            if (mono.Length < 2 || gain <= 0.001f || sampleRate < 8000)
            {
                v.On = false;
                return;
            }
            if (!ReferenceEquals(v.Sample, mono))
            {
                v.Sample = mono;
                v.Cursor = 0;
            }
            v.SampleRate = sampleRate;
            v.Rate = Math.Clamp(playbackRate, 0.25f, 3f);
            v.Gain = Math.Clamp(gain, 0, 1);
            v.On = true;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private bool HasWork()
    {
        if (_sources.Keys.Any(s => s.IsAudible)) return true;
        lock (_lock)
        {
            if (_tones.Count > 0 || _noises.Count > 0 || _shots.Count > 0) return true;
            foreach (var loop in _loops)
                if (loop.On) return true;
        }
        return false;
    }

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
                    _appliedVolume = -1;
                    Drop();
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

                if (!HasWork())
                {
                    if (sender != null && (DateTime.UtcNow - idleSince).TotalMilliseconds > 400)
                    {
                        sender.Dispose();
                        sender = null;
                    }
                    await Task.Delay(30, ct).ConfigureAwait(false);
                    continue;
                }

                idleSince = DateTime.UtcNow;
                if (sender == null)
                    Console.WriteLine($"[AUDIO] extension -> {_vap1.Host} vol {_vap1.Volume}");
                sender ??= new PixToneSender(_vap1.Host);
                if (_appliedVolume != _vap1.Volume || _appliedMute != _vap1.Muted)
                {
                    sender.SetVolume(_vap1.Volume, _vap1.Muted);
                    _appliedVolume = _vap1.Volume;
                    _appliedMute = _vap1.Muted;
                }
                Array.Clear(stereo);
                Render(stereo, SampleRate);
                WritePcm(stereo, pcm);
                try
                {
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

    private void Drop()
    {
        lock (_lock)
        {
            _tones.Clear();
            _noises.Clear();
            _shots.Clear();
            foreach (var loop in _loops) loop.On = false;
        }
    }

    private void Render(float[] stereo, int sampleRate)
    {
        lock (_lock)
        {
            foreach (var loop in _loops)
            {
                if (!loop.On) continue;
                if (loop.Sample != null)
                {
                    var data = loop.Sample;
                    var step = loop.Rate * loop.SampleRate / sampleRate;
                    for (var i = 0; i < Frames; i++)
                    {
                        var idx = (int)loop.Cursor;
                        var frac = (float)(loop.Cursor - idx);
                        var a = data[idx % data.Length];
                        var b = data[(idx + 1) % data.Length];
                        Add(stereo, i, (a + (b - a) * frac) * loop.Gain, 0);
                        loop.Cursor += step;
                        if (loop.Cursor >= data.Length) loop.Cursor %= data.Length;
                    }
                    continue;
                }
                var synth = loop.Hz / sampleRate;
                for (var i = 0; i < Frames; i++)
                {
                    loop.Phase += synth;
                    if (loop.Phase >= 1f) loop.Phase -= 1f;
                    var tri = 1f - 4f * MathF.Abs(loop.Phase - 0.5f);
                    Add(stereo, i, tri * loop.Gain * 0.8f, 0);
                }
            }

            for (var t = _tones.Count - 1; t >= 0; t--)
            {
                var tone = _tones[t];
                var step = tone.Hz / sampleRate;
                var n = Math.Min(Frames, tone.Left);
                for (var i = 0; i < n; i++)
                {
                    tone.Phase += step;
                    if (tone.Phase >= 1f) tone.Phase -= 1f;
                    var env = tone.Left / (float)(tone.Left + i + 1);
                    var s = (tone.Phase < 0.5f ? 1f : -1f) * tone.Gain * env * 0.85f;
                    Add(stereo, i, s, tone.Pan);
                }
                tone.Left -= n;
                if (tone.Left <= 0) _tones.RemoveAt(t);
            }

            for (var t = _noises.Count - 1; t >= 0; t--)
            {
                var noise = _noises[t];
                var n = Math.Min(Frames, noise.Left);
                for (var i = 0; i < n; i++)
                {
                    var env = noise.Left / (float)(noise.Left + i + 1);
                    var s = (NextRand() * 2f - 1f) * noise.Gain * env * 0.7f;
                    Add(stereo, i, s, noise.Pan);
                }
                noise.Left -= n;
                if (noise.Left <= 0) _noises.RemoveAt(t);
            }

            for (var t = _shots.Count - 1; t >= 0; t--)
            {
                var shot = _shots[t];
                var step = shot.Rate * shot.SampleRate / sampleRate;
                var alive = false;
                for (var i = 0; i < Frames; i++)
                {
                    var idx = (int)shot.Cursor;
                    if (idx >= shot.Data.Length - 1) break;
                    var frac = (float)(shot.Cursor - idx);
                    var s = (shot.Data[idx] + (shot.Data[idx + 1] - shot.Data[idx]) * frac) * shot.Gain;
                    Add(stereo, i, s, shot.Pan);
                    shot.Cursor += step;
                    alive = true;
                }
                if (!alive || shot.Cursor >= shot.Data.Length - 1) _shots.RemoveAt(t);
            }
        }

        foreach (var source in _sources.Keys)
        {
            if (!source.IsAudible) continue;
            try { source.Mix(stereo, sampleRate); }
            catch (Exception ex) { Console.WriteLine("[AUDIO] extension mix: " + ex.Message); }
        }
    }

    private static void Add(float[] stereo, int frame, float sample, float pan)
    {
        var l = sample * (pan <= 0 ? 1f : 1f - pan);
        var r = sample * (pan >= 0 ? 1f : 1f + pan);
        var i = frame * 2;
        stereo[i] = Math.Clamp(stereo[i] + l, -1f, 1f);
        stereo[i + 1] = Math.Clamp(stereo[i + 1] + r, -1f, 1f);
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

    private float NextRand()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return (_rng & 0xFFFFFF) / 16777215f;
    }

    private sealed class LoopVoice
    {
        public bool On;
        public float Hz = 80;
        public float Gain;
        public float Phase;
        public float[]? Sample;
        public int SampleRate = 44100;
        public float Rate = 1;
        public double Cursor;
    }

    private sealed class SampleVoice
    {
        public float[] Data = [];
        public int SampleRate = 44100;
        public float Gain = 1;
        public float Pan;
        public float Rate = 1;
        public double Cursor;
    }

    private sealed class ToneVoice
    {
        public float Hz;
        public float Gain;
        public float Pan;
        public float Phase;
        public int Left;
    }

    private sealed class NoiseVoice
    {
        public float Gain;
        public float Pan;
        public int Left;
    }
}
