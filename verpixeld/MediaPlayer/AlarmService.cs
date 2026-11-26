using System.Text.Json;
using verpixeld.Configuration;
using verpixeld.MediaPlayer.Audio;

namespace verpixeld.MediaPlayer;

/// <summary>One alarm: a time, weekdays, a station, and how loud it gets over how long.</summary>
public sealed class AlarmEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public bool Enabled { get; set; } = true;
    public int Hour { get; set; } = 7;
    public int Minute { get; set; }
    /// <summary>Bit 0 Monday … bit 6 Sunday. 0 rings once and then switches itself off.</summary>
    public int Days { get; set; } = 0x1F;
    /// <summary>Saved radio station; empty plays the last station.</summary>
    public string StationId { get; set; } = "";
    /// <summary>Board volume at the end of the fade, 5–100.</summary>
    public int Volume { get; set; } = 40;
    /// <summary>Minutes from quiet to <see cref="Volume" />; 0 starts at full volume.</summary>
    public int FadeMinutes { get; set; } = 5;
    public string Label { get; set; } = "";
    public string? LastFired { get; set; }

    public AlarmEntry Copy() => (AlarmEntry)MemberwiseClone();
}

/// <summary>
///     Radio alarm clock. At the alarm time the board starts very quietly, a radio station
///     plays, and the volume climbs to the alarm's level. Touching the volume stops the climb.
///     Times are local (Europe/Berlin, the same clock the panel shows).
/// </summary>
public sealed class AlarmService
{
    private readonly ILogger<AlarmService> _log;

    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "alarms.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly RadioService _radio;
    private readonly PixToneOutputService _board;
    private readonly object _gate = new();
    private List<AlarmEntry> _alarms = new();
    private CancellationTokenSource? _fade;

    public event Action? Changed;

    /// <summary>The alarm that is ringing (fading in), or null.</summary>
    public AlarmEntry? Ringing { get; private set; }

    public AlarmService(RadioService radio, PixToneOutputService board, ILogger<AlarmService> log)
    {
        _log = log;
        _radio = radio;
        _board = board;
        Load();
        _ = Task.Run(LoopAsync);
    }

    public static DateTime LocalNow()
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTime.Now;
        }
    }

    public IReadOnlyList<AlarmEntry> Alarms
    {
        get { lock (_gate) return _alarms.Select(a => a.Copy()).ToList(); }
    }

    /// <summary>The alarm the panel edits: the first one.</summary>
    public AlarmEntry? PanelAlarm
    {
        get { lock (_gate) return _alarms.FirstOrDefault()?.Copy(); }
    }

    public AlarmEntry Save(AlarmEntry alarm)
    {
        Normalize(alarm);
        lock (_gate)
        {
            var i = _alarms.FindIndex(a => a.Id == alarm.Id);
            if (i >= 0) _alarms[i] = alarm.Copy();
            else _alarms.Add(alarm.Copy());
            Persist();
        }
        Changed?.Invoke();
        return alarm.Copy();
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (_alarms.RemoveAll(a => a.Id == id) == 0) return false;
            Persist();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Panel: the first alarm on or off; creates 07:00 Monday–Friday when there is none.</summary>
    public void TogglePanelAlarm()
    {
        lock (_gate)
        {
            if (_alarms.Count == 0) _alarms.Add(new AlarmEntry());
            else _alarms[0].Enabled = !_alarms[0].Enabled;
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Panel: the first alarm's time in minutes after midnight.</summary>
    public void SetPanelTime(int minutes)
    {
        minutes = ((minutes % 1440) + 1440) % 1440;
        lock (_gate)
        {
            if (_alarms.Count == 0) _alarms.Add(new AlarmEntry { Enabled = false });
            _alarms[0].Hour = minutes / 60;
            _alarms[0].Minute = minutes % 60;
            _alarms[0].LastFired = null;
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>When an alarm rings next (local time), or null when it is off.</summary>
    public static DateTime? NextFire(AlarmEntry a, DateTime now)
    {
        if (!a.Enabled) return null;
        for (var d = 0; d <= 7; d++)
        {
            var day = now.Date.AddDays(d);
            var at = day.AddHours(a.Hour).AddMinutes(a.Minute);
            if (at <= now) continue;
            if (a.Days == 0 || (a.Days & DayBit(day.DayOfWeek)) != 0) return at;
        }
        return null;
    }

    public DateTime? NextAny()
    {
        var now = LocalNow();
        return Alarms.Select(a => NextFire(a, now)).Where(t => t != null).Min();
    }

    /// <summary>Ring an alarm now (the web page's "test" button).</summary>
    public Task TestAsync(string id)
    {
        var a = Alarms.FirstOrDefault(x => x.Id == id);
        return a == null ? Task.CompletedTask : FireAsync(a);
    }

    public void StopRinging()
    {
        _fade?.Cancel();
        Ringing = null;
        Changed?.Invoke();
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            await Task.Delay(5000).ConfigureAwait(false);
            try
            {
                var now = LocalNow();
                var today = now.ToString("yyyy-MM-dd");
                AlarmEntry? due = null;
                lock (_gate)
                {
                    foreach (var a in _alarms)
                    {
                        if (!a.Enabled || a.LastFired == today) continue;
                        if (a.Hour != now.Hour || a.Minute != now.Minute) continue;
                        if (a.Days != 0 && (a.Days & DayBit(now.DayOfWeek)) == 0) continue;
                        a.LastFired = today;
                        if (a.Days == 0) a.Enabled = false;
                        due = a.Copy();
                        Persist();
                        break;
                    }
                }
                if (due != null)
                {
                    Changed?.Invoke();
                    await FireAsync(due);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("{Message}", ex.Message);
            }
        }
    }

    private async Task FireAsync(AlarmEntry a)
    {
        var station = _radio.Stations.FirstOrDefault(s => s.Id == a.StationId)
                      ?? _radio.LastStation
                      ?? _radio.Stations.FirstOrDefault();
        if (station == null)
        {
            _log.LogInformation("No radio station to play");
            return;
        }
        _log.LogInformation("{Hour:00}:{Minute:00} → {Name}", a.Hour, a.Minute, station.Name);
        _fade?.Cancel();
        var cts = new CancellationTokenSource();
        _fade = cts;
        Ringing = a;
        Changed?.Invoke();
        var target = Math.Clamp(a.Volume, 5, 100);
        var fade = a.FadeMinutes > 0 && _board.IsSelected;
        var start = fade ? Math.Min(4, target) : target;
        if (_board.IsSelected) _board.SetVolume(start, false);
        await _radio.PlayAsync(station);
        if (!fade)
        {
            Ringing = null;
            Changed?.Invoke();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var total = TimeSpan.FromMinutes(a.FadeMinutes);
                var t0 = DateTime.UtcNow;
                var last = start;
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(3000, cts.Token);
                    if (_board.Volume != last) break;          // someone set the volume
                    var share = Math.Clamp((DateTime.UtcNow - t0) / total, 0, 1);
                    var v = (int)Math.Round(start + (target - start) * share);
                    if (v != last)
                    {
                        _board.SetVolume(v, false);
                        last = v;
                    }
                    if (share >= 1) break;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (_fade == cts)
                {
                    Ringing = null;
                    Changed?.Invoke();
                }
            }
        });
    }

    private static int DayBit(DayOfWeek d) => 1 << (((int)d + 6) % 7);

    private static void Normalize(AlarmEntry a)
    {
        a.Hour = Math.Clamp(a.Hour, 0, 23);
        a.Minute = Math.Clamp(a.Minute, 0, 59);
        a.Days &= 0x7F;
        a.Volume = Math.Clamp(a.Volume, 5, 100);
        a.FadeMinutes = Math.Clamp(a.FadeMinutes, 0, 60);
        a.Label = (a.Label ?? "").Trim();
        a.StationId ??= "";
        if (string.IsNullOrWhiteSpace(a.Id)) a.Id = Guid.NewGuid().ToString("N")[..10];
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            _alarms = JsonSerializer.Deserialize<List<AlarmEntry>>(File.ReadAllText(FilePath)) ?? new();
            foreach (var a in _alarms) Normalize(a);
            _log.LogInformation("{Count} alarms", _alarms.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not read alarms: {Message}", ex.Message);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_alarms, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save alarms: {Message}", ex.Message);
        }
    }
}
