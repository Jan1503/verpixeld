using System.Text.Json;
using verpixeld.Configuration;
using verpixeld.Services;

namespace verpixeld.MediaPlayer;

/// <summary>
///     Service for managing user favorites - saves media items with their settings
///     for quick replay later
/// </summary>
public class FavoritesService
{
    private readonly ILogger<FavoritesService> _log;

    private readonly string _favoritesFilePath;
    private readonly string _historyFilePath;
    private readonly object _lock = new();
    private List<FavoriteItem> _favorites = new();
    private List<PlayHistoryItem> _history = new();
    
    private const int MaxHistoryItems = 20;

    public FavoritesService(ILogger<FavoritesService> log)
    {
        _log = log;
        _favoritesFilePath = AppPaths.Favorites;
        _historyFilePath = AppPaths.PlayHistory;

        LoadFavorites();
        LoadHistory();
    }

    /// <summary>
    ///     Get all favorites
    /// </summary>
    public IReadOnlyList<FavoriteItem> Favorites
    {
        get
        {
            lock (_lock)
            {
                return _favorites.OrderByDescending(f => f.LastPlayedAt ?? f.AddedAt).ToList();
            }
        }
    }

    /// <summary>
    ///     Get play history
    /// </summary>
    public IReadOnlyList<PlayHistoryItem> History
    {
        get
        {
            lock (_lock)
            {
                return _history.ToList();
            }
        }
    }

    /// <summary>
    ///     Add a favorite
    /// </summary>
    public FavoriteItem AddFavorite(FavoriteItem item)
    {
        lock (_lock)
        {
            // Generate ID if not set
            if (string.IsNullOrEmpty(item.Id))
                item.Id = Guid.NewGuid().ToString("N")[..8];
            
            SanitizeNetwork(item);
            item.AddedAt = DateTime.UtcNow;
            
            // Check for duplicate source
            var existing = _favorites.FirstOrDefault(f => 
                f.Type == item.Type && 
                f.GetSourceIdentifier() == item.GetSourceIdentifier());
            
            if (existing != null)
            {
                // Update existing instead of adding duplicate
                existing.Name = item.Name;
                existing.AvSyncOffset = item.AvSyncOffset;
                existing.AudioTrack = item.AudioTrack;
                if (!string.IsNullOrEmpty(item.Thumbnail)) existing.Thumbnail = item.Thumbnail;
                SaveFavorites();
                return existing;
            }

            // A thumbnail the history already has saves another ffmpeg run.
            if (string.IsNullOrEmpty(item.Thumbnail))
                item.Thumbnail = _history.FirstOrDefault(h =>
                    h.Type == item.Type && h.GetSourceIdentifier() == item.GetSourceIdentifier() &&
                    !string.IsNullOrEmpty(h.Thumbnail))?.Thumbnail;

            _favorites.Add(item);
            SaveFavorites();
            
            _log.LogInformation("Added: {Name} ({Type})", item.Name, item.Type);
            return item;
        }
    }

    /// <summary>
    ///     Remove a favorite by ID
    /// </summary>
    public bool RemoveFavorite(string id)
    {
        lock (_lock)
        {
            var item = _favorites.FirstOrDefault(f => f.Id == id);
            if (item == null) return false;
            
            _favorites.Remove(item);
            SaveFavorites();
            
            _log.LogInformation("Removed: {Name}", item.Name);
            return true;
        }
    }

    /// <summary>
    ///     Update a favorite
    /// </summary>
    public bool UpdateFavorite(string id, string? name = null, int? avSyncOffset = null)
    {
        lock (_lock)
        {
            var item = _favorites.FirstOrDefault(f => f.Id == id);
            if (item == null) return false;
            
            if (name != null) item.Name = name;
            if (avSyncOffset.HasValue) item.AvSyncOffset = avSyncOffset.Value;
            
            SaveFavorites();
            return true;
        }
    }

    /// <summary>
    ///     Mark a favorite as played
    /// </summary>
    public void MarkFavoritePlayed(string id)
    {
        lock (_lock)
        {
            var item = _favorites.FirstOrDefault(f => f.Id == id);
            if (item != null)
            {
                item.LastPlayedAt = DateTime.UtcNow;
                SaveFavorites();
            }
        }
    }

    /// <summary>
    ///     Get a favorite by ID
    /// </summary>
    public FavoriteItem? GetFavorite(string id)
    {
        lock (_lock)
        {
            return _favorites.FirstOrDefault(f => f.Id == id);
        }
    }

    /// <summary>
    ///     Add item to play history
    /// </summary>
    public void AddToHistory(PlayHistoryItem item)
    {
        _log.LogInformation("[HISTORY] AddToHistory called: {Name} ({Type}), thumbnail: {Value}", item.Name, item.Type, (item.Thumbnail != null ? $"{item.Thumbnail.Length} chars" : "null"));
        lock (_lock)
        {
            SanitizeNetwork(item);
            var id = item.GetSourceIdentifier();

            // Replaying keeps the picture it had, or takes the favorite's.
            if (string.IsNullOrEmpty(item.Thumbnail))
                item.Thumbnail = _history.FirstOrDefault(h => h.Type == item.Type && h.GetSourceIdentifier() == id &&
                                                              !string.IsNullOrEmpty(h.Thumbnail))?.Thumbnail
                                 ?? _favorites.FirstOrDefault(f => f.Type == item.Type && f.GetSourceIdentifier() == id &&
                                                                   !string.IsNullOrEmpty(f.Thumbnail))?.Thumbnail;

            // Remove existing entry with same source
            _history.RemoveAll(h => h.Type == item.Type && h.GetSourceIdentifier() == id);

            item.PlayedAt = DateTime.UtcNow;
            
            // Add to beginning
            _history.Insert(0, item);
            _log.LogInformation("[HISTORY] History now has {Count} items", _history.Count);
            
            // Trim to max items
            while (_history.Count > MaxHistoryItems)
                _history.RemoveAt(_history.Count - 1);
            
            SaveHistory();
            _log.LogInformation("[HISTORY] History saved to disk");
        }
    }

    /// <summary>
    ///     Clear play history
    /// </summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            _history.Clear();
            SaveHistory();
        }
    }

    /// <summary>
    ///     Remove a history item by index
    /// </summary>
    public bool RemoveHistoryItem(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _history.Count) return false;
            _history.RemoveAt(index);
            SaveHistory();
            return true;
        }
    }

    /// <summary>True when history or favorites already hold a picture for this source.</summary>
    public bool HasThumbnail(MediaItemType type, string sourceId)
    {
        lock (_lock)
        {
            return _history.Any(h => h.Type == type && h.GetSourceIdentifier() == sourceId && !string.IsNullOrEmpty(h.Thumbnail)) &&
                   _favorites.Where(f => f.Type == type && f.GetSourceIdentifier() == sourceId)
                       .All(f => !string.IsNullOrEmpty(f.Thumbnail));
        }
    }

    /// <summary>
    ///     A picture made after the fact (network files take a while): every history entry and
    ///     favorite for this source that has none gets it.
    /// </summary>
    public void SetThumbnail(MediaItemType type, string sourceId, string thumbnail)
    {
        if (string.IsNullOrEmpty(thumbnail)) return;
        lock (_lock)
        {
            var h = 0;
            foreach (var item in _history.Where(x => x.Type == type && x.GetSourceIdentifier() == sourceId &&
                                                     string.IsNullOrEmpty(x.Thumbnail)))
            {
                item.Thumbnail = thumbnail;
                h++;
            }
            var f = 0;
            foreach (var fav in _favorites.Where(x => x.Type == type && x.GetSourceIdentifier() == sourceId &&
                                                      string.IsNullOrEmpty(x.Thumbnail)))
            {
                fav.Thumbnail = thumbnail;
                f++;
            }
            if (h > 0) SaveHistory();
            if (f > 0) SaveFavorites();
            if (h + f > 0) _log.LogInformation("[HISTORY] Thumbnail for {SourceId} ({H} history, {F} favorites)", sourceId, h, f);
        }
    }

    /// <summary>
    ///     Update thumbnail for a history entry by source URL (used for async thumbnail extraction)
    /// </summary>
    public void UpdateHistoryThumbnail(string sourceUrl, string thumbnail)
    {
        lock (_lock)
        {
            var item = _history.FirstOrDefault(h => h.GetSourceIdentifier() == sourceUrl);
            if (item != null && string.IsNullOrEmpty(item.Thumbnail))
            {
                item.Thumbnail = thumbnail;
                SaveHistory();
                _log.LogInformation("[HISTORY] Updated thumbnail for: {Name}", item.Name);
            }
        }
    }

    private void LoadFavorites()
    {
        try
        {
            if (File.Exists(_favoritesFilePath))
            {
                var json = File.ReadAllText(_favoritesFilePath);
                _favorites = JsonSerializer.Deserialize<List<FavoriteItem>>(json) ?? new();
                var dirty = false;
                foreach (var fav in _favorites)
                    dirty |= SanitizeNetwork(fav);
                if (dirty) SaveFavorites();
                _log.LogInformation("Loaded {Count} favorites", _favorites.Count);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("Error loading favorites: {Message}", ex.Message);
            _favorites = new();
        }
    }

    private void SaveFavorites()
    {
        try
        {
            var json = JsonSerializer.Serialize(_favorites, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            FileHelper.AtomicWriteAllText(_favoritesFilePath, json);
        }
        catch (Exception ex)
        {
            _log.LogError("Error saving favorites: {Message}", ex.Message);
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                var json = File.ReadAllText(_historyFilePath);
                _history = JsonSerializer.Deserialize<List<PlayHistoryItem>>(json) ?? new();
                var dirty = false;
                foreach (var item in _history)
                    dirty |= SanitizeNetwork(item);
                if (dirty) SaveHistory();
                _log.LogInformation("[HISTORY] Loaded {Count} history items", _history.Count);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("[HISTORY] Error loading history: {Message}", ex.Message);
            _history = new();
        }
    }

    private void SaveHistory()
    {
        try
        {
            var json = JsonSerializer.Serialize(_history, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            FileHelper.AtomicWriteAllText(_historyFilePath, json);
        }
        catch (Exception ex)
        {
            _log.LogError("[HISTORY] Error saving history: {Message}", ex.Message);
        }
    }

    internal static bool SanitizeNetwork(FavoriteItem item)
    {
        var redacted = MediaPath.Redact(item.NetworkUrl);
        if (redacted == item.NetworkUrl) return false;
        item.NetworkUrl = redacted;
        return true;
    }

    internal static bool SanitizeNetwork(PlayHistoryItem item)
    {
        var redacted = MediaPath.Redact(item.NetworkUrl);
        if (redacted == item.NetworkUrl) return false;
        item.NetworkUrl = redacted;
        return true;
    }
}

/// <summary>
///     Type of media item
/// </summary>
public enum MediaItemType
{
    LocalVideo,
    LocalAudio,
    NetworkVideo,
    NetworkAudio,
    YouTube
}

/// <summary>
///     A saved favorite media item
/// </summary>
public class FavoriteItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public MediaItemType Type { get; set; }
    
    // Source information (only one is set based on Type)
    public string? LocalPath { get; set; }          // For local files
    public string? YouTubeUrl { get; set; }         // For YouTube (original URL)
    public string? NetworkUrl { get; set; }         // Display-safe URL only (no password)
    public string? NetworkShareId { get; set; }     // Rebuild SMB URL from the saved share
    public string? NetworkShareName { get; set; }   // Display name for network shares
    public string? NetworkFilePath { get; set; }    // Path within share
    public string? NetworkProtocol { get; set; }    // smb, http, ftp
    
    // Settings
    public int AvSyncOffset { get; set; }
    public string? ScaleFilter { get; set; }  // FFmpeg scaling algorithm (null = auto)
    /// <summary>Audio track (0-based) chosen when the favorite was saved; null = the first.</summary>
    public int? AudioTrack { get; set; }
    
    // Optional thumbnail (base64 or URL)
    public string? Thumbnail { get; set; }
    
    // Timestamps
    public DateTime AddedAt { get; set; }
    public DateTime? LastPlayedAt { get; set; }

    /// <summary>
    ///     Get a unique identifier for the source (used to detect duplicates)
    /// </summary>
    public string GetSourceIdentifier()
    {
        return Type switch
        {
            MediaItemType.LocalVideo or MediaItemType.LocalAudio => LocalPath ?? "",
            MediaItemType.YouTube => YouTubeUrl ?? "",
            MediaItemType.NetworkVideo or MediaItemType.NetworkAudio =>
                !string.IsNullOrEmpty(NetworkShareId)
                    ? $"{NetworkShareId}:{NetworkFilePath}"
                    : MediaPath.Redact(NetworkUrl) ?? "",
            _ => ""
        };
    }

    /// <summary>
    ///     Get display icon based on type
    /// </summary>
    public string GetIcon()
    {
        return Type switch
        {
            MediaItemType.LocalVideo => "🎬",
            MediaItemType.LocalAudio => "🎵",
            MediaItemType.NetworkVideo => "📺",
            MediaItemType.NetworkAudio => "📻",
            MediaItemType.YouTube => "▶️",
            _ => "📁"
        };
    }
}

/// <summary>
///     An item in play history
/// </summary>
public class PlayHistoryItem
{
    public MediaItemType Type { get; set; }
    public string Name { get; set; } = "";
    
    // Source information
    public string? LocalPath { get; set; }
    public string? YouTubeUrl { get; set; }
    public string? NetworkUrl { get; set; }
    public string? NetworkShareId { get; set; }
    public string? NetworkShareName { get; set; }
    public string? NetworkFilePath { get; set; }
    public string? NetworkProtocol { get; set; }
    
    // Optional thumbnail
    public string? Thumbnail { get; set; }
    
    // When played
    public DateTime PlayedAt { get; set; }

    /// <summary>
    ///     Get a unique identifier for the source
    /// </summary>
    public string GetSourceIdentifier()
    {
        return Type switch
        {
            MediaItemType.LocalVideo or MediaItemType.LocalAudio => LocalPath ?? "",
            MediaItemType.YouTube => YouTubeUrl ?? "",
            MediaItemType.NetworkVideo or MediaItemType.NetworkAudio =>
                !string.IsNullOrEmpty(NetworkShareId)
                    ? $"{NetworkShareId}:{NetworkFilePath}"
                    : MediaPath.Redact(NetworkUrl) ?? "",
            _ => ""
        };
    }

    /// <summary>
    ///     Get display icon based on type
    /// </summary>
    public string GetIcon()
    {
        return Type switch
        {
            MediaItemType.LocalVideo => "🎬",
            MediaItemType.LocalAudio => "🎵",
            MediaItemType.NetworkVideo => "📺",
            MediaItemType.NetworkAudio => "📻",
            MediaItemType.YouTube => "▶️",
            _ => "📁"
        };
    }

    /// <summary>
    ///     Convert to a FavoriteItem for saving
    /// </summary>
    public FavoriteItem ToFavoriteItem(string name, int avSyncOffset = 0)
    {
        return new FavoriteItem
        {
            Name = name,
            Type = Type,
            LocalPath = LocalPath,
            YouTubeUrl = YouTubeUrl,
            NetworkUrl = MediaPath.Redact(NetworkUrl),
            NetworkShareId = NetworkShareId,
            NetworkShareName = NetworkShareName,
            NetworkFilePath = NetworkFilePath,
            NetworkProtocol = NetworkProtocol,
            Thumbnail = Thumbnail,
            AvSyncOffset = avSyncOffset
        };
    }
}
