using System.Text.Json;
using verpixeld.Configuration;

namespace verpixeld.MediaPlayer;

public sealed class SongEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "";
    /// <summary>Radio station, or "" for files and streams.</summary>
    public string Source { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>
///     Songs heard on the radio (the stream titles, newest first) and songs remembered from the
///     panel or the web page. Both survive a restart.
/// </summary>
[LogTag("SONGS")]
public sealed class SongLogService
{
    private readonly ILogger<SongLogService> _log;

    private const int MaxHistory = 200;
    private const int MaxSaved = 500;
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "song_log.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly RadioService _radio;
    private readonly MediaPlayerService _media;
    private readonly object _gate = new();
    private List<SongEntry> _history = new();
    private List<SongEntry> _saved = new();
    private string? _lastTitle;

    public event Action<SongEntry>? Saved;

    public SongLogService(RadioService radio, MediaPlayerService media, ILogger<SongLogService> log)
    {
        _log = log;
        _radio = radio;
        _media = media;
        Load();
        _radio.NowChanged += OnRadio;
    }

    public IReadOnlyList<SongEntry> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public IReadOnlyList<SongEntry> SavedSongs
    {
        get { lock (_gate) return _saved.ToList(); }
    }

    /// <summary>What is playing, as a song line: the radio title, or the file's tags or name.</summary>
    public (string Title, string Source)? Current()
    {
        var now = _radio.Now;
        if (now.Playing && !string.IsNullOrWhiteSpace(now.StreamTitle))
            return (now.StreamTitle!, now.Station?.Name ?? "");
        if (!_media.IsRunning) return null;
        if (_media.IsRadioPlaying) return null;
        var meta = _media.Metadata;
        string? title = meta != null && meta.HasMetadata ? meta.DisplayString(null) : null;
        title ??= _media.CurrentYouTubeInfo?.Title ?? _media.CurrentAudio ?? _media.CurrentVideo;
        if (string.IsNullOrWhiteSpace(title)) return null;
        var q = title.IndexOf('?');
        if (q > 0 && title.StartsWith("http", StringComparison.OrdinalIgnoreCase)) title = title[..q];
        // A file name: drop folders and the extension.
        if (title.Contains('/') || title.Contains('\\') || Path.HasExtension(title))
        {
            var bare = Path.GetFileNameWithoutExtension(title);
            if (!string.IsNullOrWhiteSpace(bare)) title = bare;
        }
        return (title, "");
    }

    /// <summary>Remember what is playing. Returns null when nothing has a title.</summary>
    public SongEntry? SaveCurrent()
    {
        var cur = Current();
        return cur == null ? null : Save(cur.Value.Title, cur.Value.Source);
    }

    public SongEntry Save(string title, string source)
    {
        var e = new SongEntry { Title = title.Trim(), Source = (source ?? "").Trim() };
        lock (_gate)
        {
            var dup = _saved.FirstOrDefault(s => string.Equals(s.Title, e.Title, StringComparison.OrdinalIgnoreCase));
            if (dup != null) return dup;
            _saved.Insert(0, e);
            if (_saved.Count > MaxSaved) _saved.RemoveRange(MaxSaved, _saved.Count - MaxSaved);
            Persist();
        }
        _log.LogInformation("Saved \"{Title}\"", e.Title);
        Saved?.Invoke(e);
        return e;
    }

    public bool RemoveSaved(string id)
    {
        lock (_gate)
        {
            if (_saved.RemoveAll(s => s.Id == id) == 0) return false;
            Persist();
        }
        return true;
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            Persist();
        }
    }

    private void OnRadio()
    {
        var now = _radio.Now;
        var title = now.Playing ? now.StreamTitle : null;
        if (string.IsNullOrWhiteSpace(title) || title == _lastTitle) return;
        _lastTitle = title;
        lock (_gate)
        {
            _history.Insert(0, new SongEntry { Title = title.Trim(), Source = now.Station?.Name ?? "" });
            if (_history.Count > MaxHistory) _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
            Persist();
        }
    }

    private sealed class Store
    {
        public List<SongEntry> History { get; set; } = new();
        public List<SongEntry> Saved { get; set; } = new();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var s = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath));
            if (s == null) return;
            _history = s.History;
            _saved = s.Saved;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not read the song log: {Message}", ex.Message);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Store { History = _history, Saved = _saved }, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save the song log: {Message}", ex.Message);
        }
    }
}
