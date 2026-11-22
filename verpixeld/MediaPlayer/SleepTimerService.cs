using verpixeld.MediaPlayer.Audio;

namespace verpixeld.MediaPlayer;

/// <summary>
///     Stops playback after a while. On the audio board the last 20 seconds fade out, then the
///     board's own volume comes back for the next time.
/// </summary>
public sealed class SleepTimerService
{
    private static readonly int[] Steps = { 15, 30, 60, 90 };
    private const int FadeSeconds = 20;

    private readonly MediaPlayerService _media;
    private readonly PixToneOutputService _board;
    private readonly object _gate = new();
    private DateTime? _until;
    private int _generation;

    public event Action? Changed;

    public SleepTimerService(MediaPlayerService media, PixToneOutputService board)
    {
        _media = media;
        _board = board;
    }

    public DateTime? Until
    {
        get { lock (_gate) return _until; }
    }

    /// <summary>Whole minutes left, rounded up; 0 when off.</summary>
    public int MinutesLeft
    {
        get
        {
            var u = Until;
            if (u == null) return 0;
            var left = u.Value - DateTime.UtcNow;
            return left <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(left.TotalMinutes);
        }
    }

    /// <summary>Sets the timer; 0 turns it off.</summary>
    public void Set(int minutes)
    {
        int gen;
        lock (_gate)
        {
            _generation++;
            gen = _generation;
            _until = minutes > 0 ? DateTime.UtcNow.AddMinutes(Math.Min(minutes, 24 * 60)) : null;
        }
        Console.WriteLine(minutes > 0 ? $"[SLEEP] Stop in {minutes} min" : "[SLEEP] Off");
        if (minutes > 0) _ = Task.Run(() => RunAsync(gen));
        Changed?.Invoke();
    }

    /// <summary>The panel button: off → 15 → 30 → 60 → 90 → off, starting above what is left.</summary>
    public void Cycle()
    {
        var left = MinutesLeft;
        if (left == 0)
        {
            Set(Steps[0]);
            return;
        }
        foreach (var s in Steps)
        {
            if (s > left)
            {
                Set(s);
                return;
            }
        }
        Set(0);
    }

    private async Task RunAsync(int gen)
    {
        int? fadeFrom = null;
        try
        {
            while (true)
            {
                await Task.Delay(500).ConfigureAwait(false);
                DateTime until;
                lock (_gate)
                {
                    if (gen != _generation || _until == null) break;
                    until = _until.Value;
                }
                var left = until - DateTime.UtcNow;
                if (!_media.IsRunning && left > TimeSpan.FromSeconds(FadeSeconds))
                    continue;
                if (left <= TimeSpan.FromSeconds(FadeSeconds) && _board.IsSelected && _media.IsRunning)
                {
                    fadeFrom ??= _board.Muted ? 0 : _board.Volume;
                    var share = Math.Clamp(left.TotalSeconds / FadeSeconds, 0, 1);
                    var v = (int)Math.Round(fadeFrom.Value * share);
                    if (v != _board.Volume) _board.SetVolume(v);
                }
                if (left > TimeSpan.Zero) continue;
                Console.WriteLine("[SLEEP] Time is up");
                await _media.StopAsync();
                lock (_gate)
                {
                    if (gen == _generation) _until = null;
                }
                Changed?.Invoke();
                break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[SLEEP] " + ex.Message);
        }
        finally
        {
            // Give the board its volume back, quietly, after playback has stopped.
            if (fadeFrom != null)
            {
                await Task.Delay(1500).ConfigureAwait(false);
                _board.SetVolume(fadeFrom.Value);
            }
        }
    }
}
