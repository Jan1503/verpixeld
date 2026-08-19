using Microsoft.Extensions.DependencyInjection;
using verpixeld.Configuration;
using verpixeld.MediaPlayer;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for favorites and play history management
/// </summary>
public static class FavoritesEndpoints
{
    /// <summary>
    ///     Play a single auto-play item using the appropriate media service method
    /// </summary>
    private static async Task PlayAutoPlayItem(AutoPlayItem item, FavoritesService favoritesService, MediaPlayerService mediaService)
    {
        Console.WriteLine($"[AUTOPLAY] Playing item: {item.Name} (type={item.Type})");

        // Apply saved settings for video types
        if (item.Type != "local-audio" && item.Type != "network-audio")
        {
            mediaService.AudioSyncOffsetMs = (int)item.AvSyncOffset;
            if (!string.IsNullOrEmpty(item.ScaleFilter))
                mediaService.ScaleFilter = item.ScaleFilter;
        }

        switch (item.Type)
        {
            case "youtube":
                if (!string.IsNullOrEmpty(item.Url))
                {
                    var success = await mediaService.PlayYouTubeVideoAsync(item.Url);
                    if (!success)
                        throw new InvalidOperationException($"Failed to play YouTube: {mediaService.LastPlaybackError}");
                }
                break;

            case "local-video":
                if (!string.IsNullOrEmpty(item.FilePath))
                {
                    var videoPath = Path.Combine(AppPaths.VideosDir, item.FilePath);
                    if (File.Exists(videoPath))
                        await mediaService.PlayVideoAsync(videoPath);
                }
                break;

            case "local-audio":
                if (!string.IsNullOrEmpty(item.FilePath))
                {
                    var audioPath = Path.Combine(AppPaths.AudioDir, item.FilePath);
                    if (File.Exists(audioPath))
                        await mediaService.PlayAudioAsync(audioPath);
                }
                break;

            case "network-video":
                if (!string.IsNullOrEmpty(item.Url))
                {
                    var fav = favoritesService.GetFavorite(item.FavoriteId);
                    if (fav != null)
                    {
                        await mediaService.PlayNetworkVideoAsync(
                            fav.NetworkUrl!,
                            fav.NetworkShareName ?? "Network",
                            fav.NetworkFilePath ?? "",
                            fav.NetworkProtocol ?? "smb");
                    }
                }
                break;

            case "network-audio":
                if (!string.IsNullOrEmpty(item.Url))
                {
                    var fav = favoritesService.GetFavorite(item.FavoriteId);
                    if (fav != null)
                    {
                        await mediaService.PlayNetworkAudioAsync(
                            fav.NetworkUrl!,
                            fav.NetworkShareName ?? "Network",
                            fav.NetworkFilePath ?? "");
                    }
                }
                break;

            default:
                Console.WriteLine($"[AUTOPLAY] Unknown item type: {item.Type}");
                break;
        }

        // Mark as played in favorites
        favoritesService.MarkFavoritePlayed(item.FavoriteId);
    }

    public static void MapFavoritesEndpoints(this WebApplication app)
    {
        var favoritesService = app.Services.GetRequiredService<FavoritesService>();
        var mediaService = app.Services.GetRequiredService<MediaPlayerService>();

        var group = app.MapGroup("/api/favorites");

        // ============================================================
        // FAVORITES
        // ============================================================

        // Get all favorites
        group.MapGet("/", () =>
        {
            return Results.Json(new
            {
                success = true,
                favorites = favoritesService.Favorites.Select(f => new
                {
                    f.Id,
                    f.Name,
                    type = f.Type.ToString(),
                    icon = f.GetIcon(),
                    f.Thumbnail,
                    f.AvSyncOffset,
                    scaleFilter = f.ScaleFilter,
                    addedAt = f.AddedAt,
                    lastPlayedAt = f.LastPlayedAt,
                    // Source info for display
                    source = f.Type switch
                    {
                        MediaItemType.YouTube => f.YouTubeUrl,
                        MediaItemType.LocalVideo or MediaItemType.LocalAudio => Path.GetFileName(f.LocalPath ?? ""),
                        MediaItemType.NetworkVideo or MediaItemType.NetworkAudio => 
                            $"{f.NetworkShareName}/{f.NetworkFilePath}",
                        _ => ""
                    }
                })
            });
        });

        // Add current playing as favorite
        group.MapPost("/add-current", (string? name) =>
        {
            // Determine what's currently playing
            // Priority: check audio first, then video type (YouTube vs network vs local),
            // then fall back to last-played state
            FavoriteItem? item = null;
            
            // Check if currently playing audio (takes priority - audio state is explicit)
            if (mediaService.IsAudioPlayback && mediaService.CurrentAudio != null)
            {
                if (mediaService.NetworkShareName != null)
                {
                    // Network audio - no AV sync needed for audio-only
                    item = new FavoriteItem
                    {
                        Type = MediaItemType.NetworkAudio,
                        Name = name ?? mediaService.CurrentAudio,
                        NetworkUrl = mediaService.LastPlayedAudioUrl,
                        NetworkShareName = mediaService.NetworkShareName,
                        NetworkFilePath = mediaService.NetworkFilePath,
                        NetworkProtocol = mediaService.NetworkProtocol,
                        AvSyncOffset = 0
                    };
                }
                else
                {
                    // Local audio - no AV sync needed for audio-only
                    item = new FavoriteItem
                    {
                        Type = MediaItemType.LocalAudio,
                        Name = name ?? mediaService.CurrentAudio,
                        LocalPath = mediaService.CurrentAudio,
                        AvSyncOffset = 0
                    };
                }
            }
            // Check if currently playing YouTube (NetworkShareName == "YouTube" while video is running)
            else if (mediaService.IsRunning && !string.IsNullOrEmpty(mediaService.LastPlayedYouTubeUrl) 
                     && mediaService.NetworkShareName == "YouTube")
            {
                item = new FavoriteItem
                {
                    Type = MediaItemType.YouTube,
                    Name = name ?? mediaService.LastPlayedYouTubeTitle ?? "YouTube Video",
                    YouTubeUrl = mediaService.LastPlayedYouTubeUrl,
                    Thumbnail = mediaService.CurrentYouTubeInfo?.Thumbnail,
                    AvSyncOffset = mediaService.AudioSyncOffsetMs,
                    ScaleFilter = mediaService.ScaleFilter
                };
            }
            else if (mediaService.CurrentVideo != null)
            {
                // Video (network or local)
                if (mediaService.NetworkShareName != null && mediaService.NetworkShareName != "YouTube")
                {
                    // Network video (includes generic HTTP/RTSP streams)
                    item = new FavoriteItem
                    {
                        Type = MediaItemType.NetworkVideo,
                        Name = name ?? Path.GetFileName(mediaService.NetworkFilePath ?? mediaService.CurrentVideo),
                        NetworkUrl = mediaService.NetworkVideoUrl,
                        NetworkShareName = mediaService.NetworkShareName,
                        NetworkFilePath = mediaService.NetworkFilePath,
                        NetworkProtocol = mediaService.NetworkProtocol,
                        AvSyncOffset = mediaService.AudioSyncOffsetMs,
                        ScaleFilter = mediaService.ScaleFilter
                    };
                }
                else
                {
                    // Local video
                    item = new FavoriteItem
                    {
                        Type = MediaItemType.LocalVideo,
                        Name = name ?? mediaService.CurrentVideo,
                        LocalPath = mediaService.CurrentVideo,
                        AvSyncOffset = mediaService.AudioSyncOffsetMs,
                        ScaleFilter = mediaService.ScaleFilter
                    };
                }
            }
            // Nothing currently playing - check last-played state for stopped media
            else if (!string.IsNullOrEmpty(mediaService.LastPlayedYouTubeUrl))
            {
                // Last played was YouTube (stopped but URL preserved for replay)
                item = new FavoriteItem
                {
                    Type = MediaItemType.YouTube,
                    Name = name ?? mediaService.LastPlayedYouTubeTitle ?? "YouTube Video",
                    YouTubeUrl = mediaService.LastPlayedYouTubeUrl,
                    Thumbnail = mediaService.CurrentYouTubeInfo?.Thumbnail,
                    AvSyncOffset = mediaService.AudioSyncOffsetMs,
                    ScaleFilter = mediaService.ScaleFilter
                };
            }
            else if (!string.IsNullOrEmpty(mediaService.LastPlayedAudio))
            {
                // Last played was audio - no AV sync needed for audio-only
                if (mediaService.LastPlayedWasNetwork && !string.IsNullOrEmpty(mediaService.LastPlayedAudioUrl))
                {
                    item = new FavoriteItem
                    {
                        Type = MediaItemType.NetworkAudio,
                        Name = name ?? mediaService.LastPlayedAudio,
                        NetworkUrl = mediaService.LastPlayedAudioUrl,
                        AvSyncOffset = 0
                    };
                }
                else
                {
                    item = new FavoriteItem
                    {
                        Type = MediaItemType.LocalAudio,
                        Name = name ?? mediaService.LastPlayedAudio,
                        LocalPath = mediaService.LastPlayedAudio,
                        AvSyncOffset = 0
                    };
                }
            }

            if (item == null)
            {
                return Results.Json(new 
                { 
                    success = false, 
                    message = "Nothing to add. Play a video/audio/YouTube first." 
                });
            }

            var added = favoritesService.AddFavorite(item);
            
            return Results.Json(new
            {
                success = true,
                message = $"Added to favorites: {added.Name}",
                favorite = new
                {
                    added.Id,
                    added.Name,
                    type = added.Type.ToString()
                }
            });
        });

        // Add a specific favorite (manual)
        group.MapPost("/", (FavoriteItem item) =>
        {
            var added = favoritesService.AddFavorite(item);
            return Results.Json(new
            {
                success = true,
                message = $"Added: {added.Name}",
                favorite = new { added.Id, added.Name }
            });
        });

        // Update a favorite
        group.MapPut("/{id}", (string id, string? name, int? avSyncOffset) =>
        {
            var success = favoritesService.UpdateFavorite(id, name, avSyncOffset);
            
            if (!success)
                return Results.Json(new { success = false, message = "Favorite not found" });
            
            return Results.Json(new
            {
                success = true,
                message = "Favorite updated"
            });
        });

        // Mark a favorite as played (without triggering playback)
        group.MapPost("/{id}/mark-played", (string id) =>
        {
            favoritesService.MarkFavoritePlayed(id);
            return Results.Json(new { success = true });
        });

        // Delete a favorite
        group.MapDelete("/{id}", (string id) =>
        {
            var success = favoritesService.RemoveFavorite(id);
            
            if (!success)
                return Results.Json(new { success = false, message = "Favorite not found" });
            
            return Results.Json(new
            {
                success = true,
                message = "Favorite removed"
            });
        });

        // ============================================================
        // AUTO-PLAY
        // ============================================================

        // Get auto-play status
        group.MapGet("/auto-play", () =>
        {
            return Results.Json(new
            {
                success = true,
                enabled = mediaService.AutoPlayFavorites,
                currentId = mediaService.AutoPlayCurrentId,
                currentName = mediaService.AutoPlayCurrentName,
                currentIndex = mediaService.AutoPlayCurrentIndex,
                total = mediaService.AutoPlayTotal
            });
        });

        // Start auto-play
        group.MapPost("/auto-play/start", (bool? shuffle) =>
        {
            var favorites = favoritesService.Favorites;
            if (favorites.Count == 0)
                return Results.Json(new { success = false, message = "No favorites to play" });

            // Build the auto-play queue from all favorites
            var items = favorites.Select(f => new AutoPlayItem
            {
                FavoriteId = f.Id,
                Name = f.Name,
                Type = f.Type switch
                {
                    MediaItemType.YouTube => "youtube",
                    MediaItemType.NetworkAudio => "network-audio",
                    MediaItemType.NetworkVideo => "network-video",
                    MediaItemType.LocalVideo => "local-video",
                    MediaItemType.LocalAudio => "local-audio",
                    _ => "unknown"
                },
                Url = f.YouTubeUrl ?? f.NetworkUrl,
                FilePath = f.LocalPath,
                AvSyncOffset = f.AvSyncOffset,
                ScaleFilter = f.ScaleFilter
            }).ToList();

            // Register the play callback that uses the existing play logic
            mediaService.PlayAutoPlayItemCallback = async (item) =>
            {
                await PlayAutoPlayItem(item, favoritesService, mediaService);
            };

            mediaService.StartAutoPlay(items, shuffle ?? false);

            // Start playing the first item via AdvanceAutoPlay (which handles guards)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100); // Brief delay to let StartAutoPlay finish
                    await mediaService.SkipAutoPlayAsync(); // Advances from -1 to 0
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AUTOPLAY] Error starting first item: {ex.Message}");
                    mediaService.StopAutoPlay();
                }
            });

            return Results.Json(new 
            { 
                success = true, 
                message = $"Auto-play started with {items.Count} items",
                total = items.Count
            });
        });

        // Stop auto-play
        group.MapPost("/auto-play/stop", async () =>
        {
            mediaService.StopAutoPlay();
            await mediaService.StopAsync();
            return Results.Json(new { success = true, message = "Auto-play stopped" });
        });

        // Skip to next in auto-play
        group.MapPost("/auto-play/skip", async () =>
        {
            if (!mediaService.AutoPlayFavorites)
                return Results.Json(new { success = false, message = "Auto-play is not active" });

            await mediaService.SkipAutoPlayAsync();
            return Results.Json(new 
            { 
                success = true, 
                message = "Skipped to next track",
                currentId = mediaService.AutoPlayCurrentId,
                currentIndex = mediaService.AutoPlayCurrentIndex
            });
        });

        // Play a favorite
        group.MapPost("/{id}/play", async (string id) =>
        {
            var favorite = favoritesService.GetFavorite(id);
            
            if (favorite == null)
                return Results.Json(new { success = false, message = "Favorite not found" });

            // Apply saved settings (only for video types, audio-only doesn't need them)
            if (favorite.Type != MediaItemType.LocalAudio && favorite.Type != MediaItemType.NetworkAudio)
            {
                mediaService.AudioSyncOffsetMs = favorite.AvSyncOffset;
                if (!string.IsNullOrEmpty(favorite.ScaleFilter))
                    mediaService.ScaleFilter = favorite.ScaleFilter;
            }
            
            bool success;
            string message;

            switch (favorite.Type)
            {
                case MediaItemType.YouTube:
                    if (string.IsNullOrEmpty(favorite.YouTubeUrl))
                        return Results.Json(new { success = false, message = "YouTube URL missing" });
                    
                    success = await mediaService.PlayYouTubeVideoAsync(favorite.YouTubeUrl);
                    message = success 
                        ? $"Playing: {favorite.Name}" 
                        : (mediaService.LastPlaybackError ?? "Failed to play YouTube video");
                    break;

                case MediaItemType.LocalVideo:
                    var videoPath = Path.Combine(AppPaths.VideosDir, favorite.LocalPath ?? "");
                    if (!File.Exists(videoPath))
                        return Results.Json(new { success = false, message = "Video file not found" });
                    
                    await mediaService.PlayVideoAsync(videoPath);
                    success = true;
                    message = $"Playing: {favorite.Name}";
                    break;

                case MediaItemType.LocalAudio:
                    var audioPath = Path.Combine(AppPaths.AudioDir, favorite.LocalPath ?? "");
                    if (!File.Exists(audioPath))
                        return Results.Json(new { success = false, message = "Audio file not found" });
                    
                    await mediaService.PlayAudioAsync(audioPath);
                    success = true;
                    message = $"Playing: {favorite.Name}";
                    break;

                case MediaItemType.NetworkVideo:
                    if (string.IsNullOrEmpty(favorite.NetworkUrl))
                        return Results.Json(new { success = false, message = "Network URL missing" });
                    
                    await mediaService.PlayNetworkVideoAsync(
                        favorite.NetworkUrl,
                        favorite.NetworkShareName ?? "Network",
                        favorite.NetworkFilePath ?? "",
                        favorite.NetworkProtocol ?? "smb");
                    success = true;
                    message = $"Playing: {favorite.Name}";
                    break;

                case MediaItemType.NetworkAudio:
                    if (string.IsNullOrEmpty(favorite.NetworkUrl))
                        return Results.Json(new { success = false, message = "Network URL missing" });
                    
                    await mediaService.PlayNetworkAudioAsync(
                        favorite.NetworkUrl,
                        favorite.NetworkShareName ?? "Network",
                        favorite.NetworkFilePath ?? "");
                    success = true;
                    message = $"Playing: {favorite.Name}";
                    break;

                default:
                    return Results.Json(new { success = false, message = "Unknown media type" });
            }

            if (success)
            {
                favoritesService.MarkFavoritePlayed(id);
            }

            return Results.Json(new
            {
                success,
                message,
                favorite = new { favorite.Id, favorite.Name, type = favorite.Type.ToString() }
            });
        });

        // ============================================================
        // PLAY HISTORY
        // ============================================================

        var historyGroup = app.MapGroup("/api/history");

        // Get play history
        historyGroup.MapGet("/", () =>
        {
            return Results.Json(new
            {
                success = true,
                history = favoritesService.History.Select((h, index) => new
                {
                    index,
                    h.Name,
                    type = h.Type.ToString(),
                    icon = h.GetIcon(),
                    h.Thumbnail,
                    playedAt = h.PlayedAt,
                    source = h.Type switch
                    {
                        MediaItemType.YouTube => h.YouTubeUrl,
                        MediaItemType.LocalVideo or MediaItemType.LocalAudio => Path.GetFileName(h.LocalPath ?? ""),
                        MediaItemType.NetworkVideo or MediaItemType.NetworkAudio => 
                            $"{h.NetworkShareName}/{h.NetworkFilePath}",
                        _ => ""
                    }
                })
            });
        });

        // Clear history
        historyGroup.MapDelete("/", () =>
        {
            favoritesService.ClearHistory();
            return Results.Json(new { success = true, message = "History cleared" });
        });

        // Remove history item
        historyGroup.MapDelete("/{index:int}", (int index) =>
        {
            var success = favoritesService.RemoveHistoryItem(index);
            return Results.Json(new 
            { 
                success, 
                message = success ? "Item removed" : "Item not found" 
            });
        });

        // Play from history
        historyGroup.MapPost("/{index:int}/play", async (int index) =>
        {
            var history = favoritesService.History;
            
            if (index < 0 || index >= history.Count)
                return Results.Json(new { success = false, message = "History item not found" });

            var item = history[index];
            bool success;
            string message;

            switch (item.Type)
            {
                case MediaItemType.YouTube:
                    if (string.IsNullOrEmpty(item.YouTubeUrl))
                        return Results.Json(new { success = false, message = "YouTube URL missing" });
                    
                    success = await mediaService.PlayYouTubeVideoAsync(item.YouTubeUrl);
                    message = success 
                        ? $"Playing: {item.Name}" 
                        : (mediaService.LastPlaybackError ?? "Failed to play YouTube video");
                    break;

                case MediaItemType.LocalVideo:
                    var videoPath = Path.Combine(AppPaths.VideosDir, item.LocalPath ?? "");
                    if (!File.Exists(videoPath))
                        return Results.Json(new { success = false, message = "Video file not found" });
                    
                    await mediaService.PlayVideoAsync(videoPath);
                    success = true;
                    message = $"Playing: {item.Name}";
                    break;

                case MediaItemType.LocalAudio:
                    var audioPath = Path.Combine(AppPaths.AudioDir, item.LocalPath ?? "");
                    if (!File.Exists(audioPath))
                        return Results.Json(new { success = false, message = "Audio file not found" });
                    
                    await mediaService.PlayAudioAsync(audioPath);
                    success = true;
                    message = $"Playing: {item.Name}";
                    break;

                case MediaItemType.NetworkVideo:
                    if (string.IsNullOrEmpty(item.NetworkUrl))
                        return Results.Json(new { success = false, message = "Network URL missing" });
                    
                    await mediaService.PlayNetworkVideoAsync(
                        item.NetworkUrl,
                        item.NetworkShareName ?? "Network",
                        item.NetworkFilePath ?? "",
                        item.NetworkProtocol ?? "smb");
                    success = true;
                    message = $"Playing: {item.Name}";
                    break;

                case MediaItemType.NetworkAudio:
                    if (string.IsNullOrEmpty(item.NetworkUrl))
                        return Results.Json(new { success = false, message = "Network URL missing" });
                    
                    await mediaService.PlayNetworkAudioAsync(
                        item.NetworkUrl,
                        item.NetworkShareName ?? "Network",
                        item.NetworkFilePath ?? "");
                    success = true;
                    message = $"Playing: {item.Name}";
                    break;

                default:
                    return Results.Json(new { success = false, message = "Unknown media type" });
            }

            return Results.Json(new
            {
                success,
                message
            });
        });

        // Add history item to favorites
        historyGroup.MapPost("/{index:int}/favorite", (int index, string? name) =>
        {
            var history = favoritesService.History;
            
            if (index < 0 || index >= history.Count)
                return Results.Json(new { success = false, message = "History item not found" });

            var item = history[index];
            var favorite = item.ToFavoriteItem(name ?? item.Name);
            
            var added = favoritesService.AddFavorite(favorite);
            
            return Results.Json(new
            {
                success = true,
                message = $"Added to favorites: {added.Name}",
                favorite = new { added.Id, added.Name }
            });
        });
    }
}
