using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using verpixeld.Configuration;
using verpixeld.MediaPlayer.Audio;
using verpixeld.Services;

namespace verpixeld.MediaPlayer;

/// <summary>A saved internet radio station.</summary>
public sealed class RadioStationEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Favicon { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Country { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string Language { get; set; } = "";
    public string Codec { get; set; } = "";
    public int Bitrate { get; set; }
    /// <summary>Radio Browser station id, when the station came from a search.</summary>
    public string StationUuid { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedAt { get; set; }
    public int PlayCount { get; set; }

    public RadioStationEntry Copy() => (RadioStationEntry)MemberwiseClone();
}

/// <summary>What the radio is playing right now.</summary>
public sealed record RadioNowPlaying(
    RadioStationEntry? Station,
    bool Playing,
    bool Saved,
    string? StreamTitle,
    DateTime? TitleSince,
    string? IcyName,
    string? IcyGenre,
    int IcyBitrate,
    DateTime? StartedAt,
    IReadOnlyList<RadioTitleHistory> Titles);

public sealed record RadioTitleHistory(string Title, DateTime At);

/// <summary>
///     Internet radio: a saved station list, playing a station through the media player, stepping
///     through the list, now-playing titles from the stream's ICY metadata, and recently played
///     stations. Radio Browser (radio-browser.info) does the search, in RadioBrowserService.
/// </summary>
public sealed class RadioService
{
    private readonly ILogger<RadioService> _log;

    private const int MaxRecent = 20;
    private const int MaxTitles = 15;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly MediaPlayerService _media;
    private readonly RadioBrowserService _browser;
    private readonly PixToneOutputService _board;
    private readonly object _gate = new();
    private List<RadioStationEntry> _stations = new();
    private List<RadioStationEntry> _recent = new();
    private readonly List<RadioTitleHistory> _titles = new();

    private RadioStationEntry? _current;
    private RadioStationEntry? _last;
    private DateTime? _startedAt;
    private string? _streamTitle;
    private DateTime? _titleSince;
    private string? _icyName, _icyGenre;
    private int _icyBitrate;
    private string? _icyUrl;
    private CancellationTokenSource? _icyCts;
    private readonly Dictionary<string, string> _covers = new();
    private int _coverGen;

    /// <summary>The saved list changed (add, remove, rename, order).</summary>
    public event Action? StationsChanged;

    /// <summary>The station or the now-playing title changed.</summary>
    public event Action? NowChanged;

    /// <summary>Read the stream's song titles. Costs a second connection to the station.</summary>
    public bool ReadTitles { get; set; } = true;

    public RadioService(MediaPlayerService media, RadioBrowserService browser, PixToneOutputService board, ILogger<RadioService> log)
    {
        _log = log;
        _media = media;
        _browser = browser;
        _board = board;
        Load();
        _ = Task.Run(WatchAsync);
    }

    public IReadOnlyList<RadioStationEntry> Stations
    {
        get { lock (_gate) return _stations.Select(s => s.Copy()).ToList(); }
    }

    public IReadOnlyList<RadioStationEntry> Recent
    {
        get { lock (_gate) return _recent.Select(s => s.Copy()).ToList(); }
    }

    /// <summary>The last station that played, for "play" on a stopped panel.</summary>
    public RadioStationEntry? LastStation
    {
        get { lock (_gate) return (_current ?? _last)?.Copy(); }
    }

    public RadioNowPlaying Now
    {
        get
        {
            lock (_gate)
            {
                var playing = _media.IsRadioPlaying && _current != null;
                var saved = _current != null && _stations.Any(s => SameStation(s, _current));
                return new RadioNowPlaying(_current?.Copy(), playing, saved, _streamTitle, _titleSince,
                    _icyName, _icyGenre, _icyBitrate, _startedAt, _titles.ToList());
            }
        }
    }

    /// <summary>Index of the playing station in the saved list, or -1.</summary>
    public int CurrentIndex
    {
        get
        {
            lock (_gate)
            {
                if (_current == null || !_media.IsRadioPlaying) return -1;
                return _stations.FindIndex(s => SameStation(s, _current));
            }
        }
    }

    // ---- saved list ---------------------------------------------------------------------

    /// <summary>Adds a station, or returns the saved one with the same stream or Radio Browser id.</summary>
    public RadioStationEntry Add(RadioStationEntry station)
    {
        Validate(station);
        lock (_gate)
        {
            var existing = _stations.FirstOrDefault(s => SameStation(s, station));
            if (existing != null) return existing.Copy();
            var add = station.Copy();
            if (string.IsNullOrWhiteSpace(add.Id) || _stations.Any(s => s.Id == add.Id))
                add.Id = Guid.NewGuid().ToString("N")[..12];
            add.AddedAt = DateTime.UtcNow;
            _stations.Add(add);
            SaveLocked();
            station = add.Copy();
        }
        StationsChanged?.Invoke();
        return station;
    }

    public bool Update(string id, string? name, string? url, string? favicon, string? tags)
    {
        lock (_gate)
        {
            var s = _stations.FirstOrDefault(x => x.Id == id);
            if (s == null) return false;
            if (!string.IsNullOrWhiteSpace(name)) s.Name = name.Trim();
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (!IsHttp(url)) throw new ArgumentException("Stream address must be http or https");
                s.Url = url.Trim();
            }
            if (favicon != null) s.Favicon = favicon.Trim();
            if (tags != null) s.Tags = tags.Trim();
            SaveLocked();
        }
        StationsChanged?.Invoke();
        return true;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (_stations.RemoveAll(s => s.Id == id) == 0) return false;
            SaveLocked();
        }
        StationsChanged?.Invoke();
        return true;
    }

    /// <summary>Moves a station up (negative) or down (positive) in the list.</summary>
    public bool Move(string id, int delta)
    {
        lock (_gate)
        {
            var i = _stations.FindIndex(s => s.Id == id);
            if (i < 0) return false;
            var j = Math.Clamp(i + delta, 0, _stations.Count - 1);
            if (i == j) return true;
            var s = _stations[i];
            _stations.RemoveAt(i);
            _stations.Insert(j, s);
            SaveLocked();
        }
        StationsChanged?.Invoke();
        return true;
    }

    /// <summary>Puts the stations in the given order; ids not named keep their place at the end.</summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        lock (_gate)
        {
            var byId = _stations.ToDictionary(s => s.Id);
            var next = ids.Where(byId.ContainsKey).Distinct().Select(id => byId[id]).ToList();
            next.AddRange(_stations.Where(s => !next.Contains(s)));
            _stations = next;
            SaveLocked();
        }
        StationsChanged?.Invoke();
    }

    public string Export()
    {
        lock (_gate) return JsonSerializer.Serialize(_stations, Json);
    }

    /// <summary>Adds stations from an export. Returns how many were new.</summary>
    public int Import(IEnumerable<RadioStationEntry> stations, bool replace)
    {
        var added = 0;
        lock (_gate)
        {
            if (replace) _stations.Clear();
            foreach (var st in stations)
            {
                if (string.IsNullOrWhiteSpace(st.Name) || !IsHttp(st.Url)) continue;
                if (_stations.Any(s => SameStation(s, st))) continue;
                var add = st.Copy();
                if (string.IsNullOrWhiteSpace(add.Id) || _stations.Any(s => s.Id == add.Id))
                    add.Id = Guid.NewGuid().ToString("N")[..12];
                _stations.Add(add);
                added++;
            }
            SaveLocked();
        }
        StationsChanged?.Invoke();
        return added;
    }

    public void ClearRecent()
    {
        lock (_gate)
        {
            _recent.Clear();
            SaveLocked();
        }
    }

    // ---- playing ------------------------------------------------------------------------

    public async Task<bool> PlayAsync(string id)
    {
        RadioStationEntry? st;
        lock (_gate) st = _stations.FirstOrDefault(s => s.Id == id)?.Copy();
        if (st == null)
        {
            lock (_gate) st = _recent.FirstOrDefault(s => s.Id == id)?.Copy();
        }
        return st != null && await PlayAsync(st);
    }

    /// <summary>Plays any station, saved or not.</summary>
    public async Task<bool> PlayAsync(RadioStationEntry station)
    {
        Validate(station);
        lock (_gate)
        {
            _current = station.Copy();
            _last = _current;
            _startedAt = DateTime.UtcNow;
            _streamTitle = null;
            _titleSince = null;
            _icyName = null;
            _icyGenre = null;
            _icyBitrate = 0;
            _titles.Clear();
            var saved = _stations.FirstOrDefault(s => SameStation(s, station));
            if (saved != null)
            {
                saved.LastPlayedAt = DateTime.UtcNow;
                saved.PlayCount++;
            }
            _recent.RemoveAll(s => SameStation(s, station));
            var r = station.Copy();
            r.LastPlayedAt = DateTime.UtcNow;
            _recent.Insert(0, r);
            if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
            SaveLocked();
        }
        _log.LogInformation("Play \"{Name}\"", station.Name);
        _ = _browser.CountClickAsync(station.StationUuid);
        NowChanged?.Invoke();
        ShowCover(station);
        try
        {
            await _media.PlayRadioStreamAsync(station.Url, station.Name);
        }
        catch (Exception ex)
        {
            _log.LogWarning("{Name}: {Message}", station.Name, ex.Message);
            return false;
        }
        StartTitles(station.Url);
        return true;
    }

    /// <summary>Next (1) or previous (-1) saved station, wrapping around.</summary>
    public async Task<bool> StepAsync(int direction)
    {
        RadioStationEntry? next;
        lock (_gate)
        {
            if (_stations.Count == 0) return false;
            var i = _current == null ? -1 : _stations.FindIndex(s => SameStation(s, _current));
            var j = i < 0
                ? (direction >= 0 ? 0 : _stations.Count - 1)
                : ((i + Math.Sign(direction == 0 ? 1 : direction)) % _stations.Count + _stations.Count) % _stations.Count;
            next = _stations[j].Copy();
        }
        return await PlayAsync(next);
    }

    public async Task StopAsync()
    {
        StopTitles();
        await _media.StopAsync();
        lock (_gate)
        {
            _current = null;
            _streamTitle = null;
        }
        NowChanged?.Invoke();
    }

    // ---- now playing --------------------------------------------------------------------

    /// <summary>Follows the media player: radio started elsewhere (voice), stopped, or replaced.</summary>
    private async Task WatchAsync()
    {
        while (true)
        {
            await Task.Delay(2000).ConfigureAwait(false);
            try
            {
                var url = _media.IsRadioPlaying ? _media.RadioStreamUrl : null;
                if (url == null)
                {
                    bool had;
                    lock (_gate)
                    {
                        had = _current != null;
                        _current = null;
                        _streamTitle = null;
                    }
                    if (had || _icyCts != null)
                    {
                        StopTitles();
                        NowChanged?.Invoke();
                    }
                    continue;
                }
                bool adopt;
                lock (_gate) adopt = _current == null || _current.Url != url;
                if (adopt)
                {
                    lock (_gate)
                    {
                        _current = _stations.FirstOrDefault(s => s.Url == url)?.Copy()
                                   ?? new RadioStationEntry { Name = _media.RadioStationName ?? "Radio", Url = url };
                        _last = _current;
                        _startedAt = DateTime.UtcNow;
                        _streamTitle = null;
                        _titles.Clear();
                    }
                    var adopted = LastStation;
                    if (adopted != null) ShowCover(adopted);
                    NowChanged?.Invoke();
                }
                if (_icyUrl != url) StartTitles(url);
            }
            catch (Exception ex)
            {
                _log.LogWarning("watch: {Message}", ex.Message);
            }
        }
    }

    private void StartTitles(string url)
    {
        StopTitles();
        if (!ReadTitles) return;
        var cts = new CancellationTokenSource();
        _icyCts = cts;
        _icyUrl = url;
        _ = Task.Run(() => IcyLoopAsync(url, cts.Token));
    }

    private void StopTitles()
    {
        var cts = _icyCts;
        _icyCts = null;
        _icyUrl = null;
        try { cts?.Cancel(); } catch { /* gone */ }
    }

    private static readonly Regex StreamTitleRx = new("StreamTitle='(.*?)';", RegexOptions.Singleline);

    /// <summary>
    ///     A second, metadata-only reader of the stream. ICY servers put a block of text every
    ///     icy-metaint bytes; the audio in between is thrown away. Retries while the station plays.
    /// </summary>
    private async Task IcyLoopAsync(string url, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Icy-MetaData", "1");
                req.Headers.UserAgent.Add(new ProductInfoHeaderValue("verpixeld", "1.0"));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                string? Header(string name) =>
                    resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault()
                    : resp.Content.Headers.TryGetValues(name, out var c) ? c.FirstOrDefault() : null;
                lock (_gate)
                {
                    _icyName = Header("icy-name");
                    _icyGenre = Header("icy-genre");
                    _icyBitrate = int.TryParse(Header("icy-br")?.Split(',')[0], out var br) ? br : 0;
                }
                if (!int.TryParse(Header("icy-metaint"), out var metaInt) || metaInt <= 0)
                {
                    _log.LogInformation("Station sends no song titles");
                    return;
                }
                failures = 0;
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var skip = new byte[Math.Min(metaInt, 16384)];
                var meta = new byte[255 * 16];
                while (!ct.IsCancellationRequested)
                {
                    var left = metaInt;
                    while (left > 0)
                    {
                        var n = await stream.ReadAsync(skip.AsMemory(0, Math.Min(left, skip.Length)), ct);
                        if (n <= 0) throw new EndOfStreamException();
                        left -= n;
                    }
                    var lenByte = stream.ReadByte();
                    if (lenByte < 0) throw new EndOfStreamException();
                    var len = lenByte * 16;
                    if (len == 0) continue;
                    await stream.ReadExactlyAsync(meta.AsMemory(0, len), ct);
                    var text = DecodeMeta(meta.AsSpan(0, len));
                    var m = StreamTitleRx.Match(text);
                    if (m.Success) NoteTitle(m.Groups[1].Value.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (++failures >= 5)
                {
                    _log.LogWarning("Song titles unavailable: {Message}", ex.Message);
                    return;
                }
                try { await Task.Delay(5000, ct); } catch { return; }
            }
        }
    }

    private static string DecodeMeta(ReadOnlySpan<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        if (end >= 0) raw = raw[..end];
        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(raw);
        }
    }

    private void NoteTitle(string title)
    {
        string? station;
        lock (_gate)
        {
            if (title == _streamTitle) return;
            _streamTitle = string.IsNullOrWhiteSpace(title) ? null : title;
            _titleSince = DateTime.UtcNow;
            if (_streamTitle != null)
            {
                _titles.Insert(0, new RadioTitleHistory(_streamTitle, DateTime.UtcNow));
                if (_titles.Count > MaxTitles) _titles.RemoveRange(MaxTitles, _titles.Count - MaxTitles);
            }
            station = _current?.Name;
        }
        _log.LogInformation("Now: {Title}", title);
        // The panel splits "Artist - Title"; the station goes after the title.
        var board = string.IsNullOrWhiteSpace(title) ? station : $"{title} · {station}";
        _board.SetTrackTitle(board);
        NowChanged?.Invoke();
    }

    /// <summary>The station cover already built for this stream address, if any.</summary>
    public string? CachedCover(string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl)) return null;
        lock (_gate) return _covers.TryGetValue(streamUrl, out var c) ? c : null;
    }

    /// <summary>
    ///     The board's cover for a station, built once per stream address (StationCover) and sent
    ///     once the station plays: starting playback first stops the old one, which clears the cover.
    /// </summary>
    private void ShowCover(RadioStationEntry station)
    {
        var gen = Interlocked.Increment(ref _coverGen);
        _ = Task.Run(async () =>
        {
            try
            {
                string? data;
                lock (_gate) _covers.TryGetValue(station.Url, out data);
                if (data == null)
                {
                    data = await StationCover.BuildAsync(station);
                    lock (_gate)
                    {
                        if (_covers.Count > 64) _covers.Clear();
                        _covers[station.Url] = data;
                    }
                }
                for (var i = 0; i < 60 && gen == _coverGen; i++)
                {
                    if (_media.IsRadioPlaying && _media.RadioStreamUrl == station.Url) break;
                    await Task.Delay(250);
                }
                await Task.Delay(300);
                if (gen != _coverGen || !_media.IsRadioPlaying || _media.RadioStreamUrl != station.Url) return;
                _board.ShowCover(data);
            }
            catch (Exception ex)
            {
                _log.LogWarning("cover for {Name}: {Message}", station.Name, ex.Message);
            }
        });
    }

    // ---- storage ------------------------------------------------------------------------

    private sealed class Store
    {
        public List<RadioStationEntry> Stations { get; set; } = new();
        public List<RadioStationEntry> Recent { get; set; } = new();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.RadioStations)) return;
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(AppPaths.RadioStations));
            if (store == null) return;
            _stations = store.Stations.Where(s => IsHttp(s.Url)).ToList();
            _recent = store.Recent.Where(s => IsHttp(s.Url)).Take(MaxRecent).ToList();
            _log.LogInformation("{Count} saved stations", _stations.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not read the station list: {Message}", ex.Message);
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.RadioStations)!);
            var tmp = AppPaths.RadioStations + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Store { Stations = _stations, Recent = _recent }, Json));
            File.Move(tmp, AppPaths.RadioStations, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save the station list: {Message}", ex.Message);
        }
    }

    private static bool SameStation(RadioStationEntry a, RadioStationEntry b) =>
        (!string.IsNullOrEmpty(a.StationUuid) && a.StationUuid == b.StationUuid) ||
        string.Equals(a.Url.Trim(), b.Url.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsHttp(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static void Validate(RadioStationEntry s)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw new ArgumentException("Station name required");
        if (!IsHttp(s.Url)) throw new ArgumentException("Stream address must be http or https");
        s.Name = s.Name.Trim();
        s.Url = s.Url.Trim();
    }
}
