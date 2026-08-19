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
    private readonly string _favoritesFilePath;
    private readonly string _historyFilePath;
    private readonly object _lock = new();
    private List<FavoriteItem> _favorites = new();
    private List<PlayHistoryItem> _history = new();
    
    private const int MaxHistoryItems = 20;

    public FavoritesService()
    {
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
                existing.Thumbnail = item.Thumbnail;
                SaveFavorites();
                return existing;
            }
            
            _favorites.Add(item);
            SaveFavorites();
            
            Console.WriteLine($"[FAVORITES] Added: {item.Name} ({item.Type})");
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
            
            Console.WriteLine($"[FAVORITES] Removed: {item.Name}");
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
        Console.WriteLine($"[HISTORY] AddToHistory called: {item.Name} ({item.Type}), thumbnail: {(item.Thumbnail != null ? $"{item.Thumbnail.Length} chars" : "null")}");
        lock (_lock)
        {
            // Remove existing entry with same source
            _history.RemoveAll(h => 
                h.Type == item.Type && 
                h.GetSourceIdentifier() == item.GetSourceIdentifier());
            
            item.PlayedAt = DateTime.UtcNow;
            
            // Add to beginning
            _history.Insert(0, item);
            Console.WriteLine($"[HISTORY] History now has {_history.Count} items");
            
            // Trim to max items
            while (_history.Count > MaxHistoryItems)
                _history.RemoveAt(_history.Count - 1);
            
            SaveHistory();
            Console.WriteLine($"[HISTORY] History saved to disk");
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
                Console.WriteLine($"[HISTORY] Updated thumbnail for: {item.Name}");
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
                Console.WriteLine($"[FAVORITES] Loaded {_favorites.Count} favorites");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAVORITES] Error loading favorites: {ex.Message}");
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
            Console.WriteLine($"[FAVORITES] Error saving favorites: {ex.Message}");
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
                Console.WriteLine($"[HISTORY] Loaded {_history.Count} history items");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HISTORY] Error loading history: {ex.Message}");
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
            Console.WriteLine($"[HISTORY] Error saving history: {ex.Message}");
        }
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
    public string? NetworkUrl { get; set; }         // For SMB/HTTP shares
    public string? NetworkShareName { get; set; }   // Display name for network shares
    public string? NetworkFilePath { get; set; }    // Path within share
    public string? NetworkProtocol { get; set; }    // smb, http, ftp
    
    // Settings
    public int AvSyncOffset { get; set; }
    public string? ScaleFilter { get; set; }  // FFmpeg scaling algorithm (null = auto)
    
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
            MediaItemType.NetworkVideo or MediaItemType.NetworkAudio => NetworkUrl ?? "",
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
            MediaItemType.NetworkVideo or MediaItemType.NetworkAudio => NetworkUrl ?? "",
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
            NetworkUrl = NetworkUrl,
            NetworkShareName = NetworkShareName,
            NetworkFilePath = NetworkFilePath,
            NetworkProtocol = NetworkProtocol,
            Thumbnail = Thumbnail,
            AvSyncOffset = avSyncOffset
        };
    }
}
