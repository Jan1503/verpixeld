using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.Configuration;
using verpixeld.MediaPlayer;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for media player - video and audio playback
/// </summary>
public static class MediaPlayerEndpoints
{
    public static void MapMediaPlayerEndpoints(this WebApplication app)
    {
        var mediaService = app.Services.GetRequiredService<MediaPlayerService>();
        var favoritesService = app.Services.GetRequiredService<FavoritesService>();
        var alertService = app.Services.GetRequiredService<AlertService>();

        var group = app.MapGroup("/api/media");

        // Get media player status
        group.MapGet("/status", () =>
        {
            var metadata = mediaService.Metadata;
            return Results.Json(new
            {
                success = true,
                data = new
                {
                    isRunning = mediaService.IsRunning,
                    isPaused = mediaService.IsPaused,
                    currentVideo = mediaService.CurrentVideo,
                    currentAudio = mediaService.CurrentAudio,
                    isAudioPlayback = mediaService.IsAudioPlayback,
                    videoPosition = mediaService.VideoPosition.TotalSeconds,
                    videoDuration = mediaService.VideoDuration.TotalSeconds,
                    videoFps = mediaService.VideoFps,
                    audioEnabled = mediaService.AudioEnabled,
                    audioAvailable = mediaService.AudioAvailable,
                    isMuted = mediaService.IsMuted,
                    volume = (int)(mediaService.Volume * 100),
                    audioSyncOffsetMs = mediaService.AudioSyncOffsetMs,
                    modPlayerAvailable = mediaService.ModPlayerAvailable,
                    isModPlaying = mediaService.IsModPlaying,
                    currentModFile = mediaService.CurrentModFile,
                    selectedModFile = mediaService.ModFilePath != null
                        ? Path.GetFileName(mediaService.ModFilePath)
                        : null,
                    ffmpegAvailable = MediaPlayerService.FFmpegAvailable,
                    availableVideos = mediaService.GetAvailableVideos(),
                    availableAudioFiles = mediaService.GetAvailableAudioFiles(),
                    availableModFiles = mediaService.GetAvailableModFiles(),
                    // Network video info
                    isNetworkVideo = mediaService.IsNetworkVideo,
                    networkShareName = mediaService.NetworkShareName,
                    networkFilePath = mediaService.NetworkFilePath,
                    networkProtocol = mediaService.NetworkProtocol,
                    seekingSupported = mediaService.SeekingSupported,
                    // Target canvas (dropdown) vs the canvas that is actually playing
                    targetCanvasName = mediaService.TargetCanvasName ?? "Main",
                    playbackCanvasName = mediaService.PlaybackCanvasName,
                    // Video scaling
                    scaleFilter = mediaService.ScaleFilter,
                    // Playlist info
                    playlistIndex = mediaService.CurrentPlaylistIndex,
                    playlistCount = mediaService.AudioPlaylist.Count,
                    autoAdvance = mediaService.AutoAdvance,
                    shuffleMode = mediaService.ShuffleMode,
                    repeatMode = mediaService.RepeatMode,
                    hasNextTrack = mediaService.HasNextTrack,
                    hasPreviousTrack = mediaService.HasPreviousTrack,
                    hasAudioPlaylist = mediaService.HasAudioPlaylist,
                    lastPlayedAudio = mediaService.LastPlayedAudio,
                    lastPlayedAudioUrl = mediaService.LastPlayedAudioUrl,
                    lastPlayedWasNetwork = mediaService.LastPlayedWasNetwork,
                    // YouTube replay info
                    lastPlayedYouTubeUrl = mediaService.LastPlayedYouTubeUrl,
                    lastPlayedYouTubeTitle = mediaService.LastPlayedYouTubeTitle,
                    hasYouTubeReplay = !string.IsNullOrEmpty(mediaService.LastPlayedYouTubeUrl),
                    // Media metadata (ID3 tags, etc.)
                    metadata = metadata != null
                        ? new
                        {
                            title = metadata.Title,
                            artist = metadata.Artist,
                            album = metadata.Album,
                            genre = metadata.Genre,
                            year = metadata.Year,
                            trackNumber = metadata.TrackNumber,
                            albumArtist = metadata.AlbumArtist,
                            composer = metadata.Composer,
                            hasMetadata = metadata.HasMetadata
                        }
                        : null,
                    // Auto-play favorites
                    autoPlayFavorites = mediaService.AutoPlayFavorites,
                    autoPlayCurrentId = mediaService.AutoPlayCurrentId,
                    autoPlayCurrentIndex = mediaService.AutoPlayCurrentIndex,
                    autoPlayTotal = mediaService.AutoPlayTotal,
                    // Camera alert
                    alertActive = alertService?.IsActive ?? false,
                    alertRemainingSeconds = alertService?.RemainingSeconds ?? 0
                }
            });
        });

        // Set target canvas for video playback
        group.MapPost("/target-canvas", (string? canvasName) =>
        {
            if (SystemOverlayCanvases.IsSystem(canvasName))
                return Results.Json(new { success = false, error = $"'{canvasName}' is a host overlay and cannot take media" });
            mediaService.TargetCanvasName = canvasName;
            Console.WriteLine($"[MEDIA] Target canvas set to: {canvasName ?? "Main"}");

            return Results.Json(new
            {
                success = true,
                targetCanvasName = mediaService.TargetCanvasName ?? "Main",
                message = $"Target canvas set to: {mediaService.TargetCanvasName ?? "Main"}"
            });
        });

        // List available videos
        group.MapGet("/videos", () =>
        {
            return Results.Json(new
            {
                success = true,
                ffmpegAvailable = MediaPlayerService.FFmpegAvailable,
                videos = mediaService.GetAvailableVideos()
            });
        });

        // Get video info
        group.MapGet("/videos/{filename}/info", async (string filename) =>
        {
            var filePath = Path.Combine(AppPaths.VideosDir, filename);

            var info = await mediaService.GetVideoInfoAsync(filePath);
            if (info == null) return Results.Json(new { success = false, message = "Failed to get video info" });

            return Results.Json(new
            {
                success = true,
                info = new
                {
                    filename = info.FileName,
                    width = info.Width,
                    height = info.Height,
                    fps = info.Fps,
                    duration = info.Duration.TotalSeconds,
                    durationFormatted = info.Duration.ToString(@"mm\:ss")
                }
            });
        });

        // Play a video
        group.MapPost("/play/{filename}", async (string filename, bool? loop) =>
        {
            if (!MediaPlayerService.FFmpegAvailable)
                return Results.Json(new
                {
                    success = false,
                    message = "FFmpeg not available. Install with: sudo apt install ffmpeg"
                });

            var filePath = Path.Combine(AppPaths.VideosDir, filename);

            if (!File.Exists(filePath))
                return Results.Json(new
                {
                    success = false,
                    message = $"Video not found: {filename}. Upload videos to the Media/Videos folder."
                });

            // Extract thumbnail first (fast for local files)
            Console.WriteLine($"[MEDIA-API] Extracting thumbnail for: {filePath}");
            var thumbnail = await MediaProbeService.ExtractThumbnailAsync(filePath);
            Console.WriteLine($"[MEDIA-API] Thumbnail extracted: {(thumbnail != null ? $"{thumbnail.Length} chars" : "null")}");
            
            await mediaService.PlayVideoAsync(filePath, loop ?? true);
            
            // Record in play history with thumbnail
            Console.WriteLine($"[MEDIA-API] Adding to history: {filename}");
            favoritesService?.AddToHistory(new PlayHistoryItem
            {
                Type = MediaItemType.LocalVideo,
                Name = filename,
                LocalPath = filename,
                Thumbnail = thumbnail
            });
            Console.WriteLine("[MEDIA-API] History updated");

            return Results.Json(new
            {
                success = true,
                message = $"Playing: {filename}",
                currentVideo = mediaService.CurrentVideo
            });
        });

        // Stop playback
        group.MapPost("/stop", async () =>
        {
            await mediaService.StopAsync();

            return Results.Json(new
            {
                success = true,
                message = "Playback stopped"
            });
        });

        // Pause/Resume
        group.MapPost("/pause", () =>
        {
            if (!mediaService.IsRunning) return Results.Json(new { success = false, message = "Nothing is playing" });

            mediaService.TogglePause();

            return Results.Json(new
            {
                success = true,
                isPaused = mediaService.IsPaused,
                message = mediaService.IsPaused ? "Paused" : "Resumed"
            });
        });

        // Seek to position (in seconds or percentage)
        group.MapPost("/seek", async (double? position, double? percent) =>
        {
            if (!mediaService.IsRunning) return Results.Json(new { success = false, message = "Nothing is playing" });

            // Check if seeking is supported for current video
            if (!mediaService.SeekingSupported)
            {
                var protocol = mediaService.NetworkProtocol ?? "unknown";
                return Results.Json(new
                {
                    success = false,
                    message =
                        $"Seeking not supported for {protocol.ToUpper()} streams. Use FTP or mount the share locally.",
                    isNetworkVideo = true,
                    protocol
                });
            }

            if (percent.HasValue)
            {
                // Seek by percentage (0-100)
                await mediaService.SeekPercentAsync(Math.Clamp(percent.Value, 0, 100));
                return Results.Json(new
                {
                    success = true,
                    message = $"Seeking to {percent.Value:F0}%",
                    position = mediaService.VideoPosition.TotalSeconds
                });
            }

            if (position.HasValue)
            {
                // Seek by seconds
                await mediaService.SeekAsync(TimeSpan.FromSeconds(Math.Max(0, position.Value)));
                return Results.Json(new
                {
                    success = true,
                    message = $"Seeking to {TimeSpan.FromSeconds(position.Value):mm\\:ss}",
                    position = mediaService.VideoPosition.TotalSeconds
                });
            }

            return Results.Json(new { success = false, message = "Specify position (seconds) or percent (0-100)" });
        });

        // Toggle mute (this actually mutes system audio)
        group.MapPost("/audio/mute", () =>
        {
            mediaService.ToggleMute();

            return Results.Json(new
            {
                success = true,
                isMuted = mediaService.IsMuted,
                message = mediaService.IsMuted ? "Audio muted" : "Audio unmuted"
            });
        });

        // Set volume (0-100)
        group.MapPost("/audio/volume", (int volume) =>
        {
            var clampedVolume = Math.Clamp(volume, 0, 100);
            mediaService.SetVolume(clampedVolume);

            return Results.Json(new
            {
                success = true,
                volume = clampedVolume,
                message = $"Volume set to {clampedVolume}%"
            });
        });

        // Get audio status
        group.MapGet("/audio", () =>
        {
            return Results.Json(new
            {
                success = true,
                audioAvailable = mediaService.AudioAvailable,
                isMuted = mediaService.IsMuted,
                volume = (int)(mediaService.Volume * 100),
                audioSyncOffsetMs = mediaService.AudioSyncOffsetMs
            });
        });

        // Set audio sync offset (in milliseconds)
        // Positive = delay audio (use when audio is ahead of video)
        // Negative = delay video (use when video is ahead of audio)
        // When changed during playback, automatically seeks to current position to apply new offset
        group.MapPost("/audio/sync", async (int offsetMs, bool? apply) =>
        {
            var oldOffset = mediaService.AudioSyncOffsetMs;
            mediaService.AudioSyncOffsetMs = offsetMs;
            var newOffset = mediaService.AudioSyncOffsetMs; // Get clamped value

            var direction = newOffset == 0 ? "sync reset" :
                newOffset > 0 ? $"audio delayed by {newOffset}ms" :
                $"video delayed by {-newOffset}ms";

            // If playing and offset changed significantly, auto-apply by seeking to current position
            // This restarts FFmpeg with the new adelay value without losing playback position
            var applied = false;
            if ((apply ?? true) && mediaService.IsRunning && oldOffset != newOffset && mediaService.SeekingSupported)
            {
                var currentPosition = mediaService.VideoPosition;
                if (currentPosition.TotalSeconds > 0.5) // Only if we have a meaningful position
                {
                    await mediaService.SeekAsync(currentPosition);
                    applied = true;
                    Console.WriteLine(
                        $"[MEDIA] Audio sync adjusted to {newOffset}ms, resynced at {currentPosition.TotalSeconds:F1}s");
                }
            }

            return Results.Json(new
            {
                success = true,
                audioSyncOffsetMs = newOffset,
                message = $"Audio sync: {direction}",
                applied,
                note = !applied && oldOffset != newOffset && mediaService.IsRunning && !mediaService.SeekingSupported
                    ? "Seeking not supported for this stream - restart playback to apply"
                    : null
            });
        });

        // Get available scale filters
        group.MapGet("/scale-filters", () =>
        {
            return Results.Json(new
            {
                success = true,
                current = mediaService.ScaleFilter,
                filters = FfmpegCapabilities.AvailableScaleFilters.Select(kv => new
                {
                    id = kv.Key,
                    name = kv.Value
                })
            });
        });

        // Set video scale filter
        group.MapPost("/scale-filter", async (string filter) =>
        {
            if (!FfmpegCapabilities.AvailableScaleFilters.ContainsKey(filter))
            {
                return Results.Json(new 
                { 
                    success = false, 
                    message = $"Unknown filter: {filter}. Available: {string.Join(", ", FfmpegCapabilities.AvailableScaleFilters.Keys)}" 
                });
            }

            var oldFilter = mediaService.ScaleFilter;
            mediaService.ScaleFilter = filter;

            // If currently playing, re-seek to apply the new filter immediately
            var applied = false;
            if (mediaService.IsRunning && oldFilter != filter && mediaService.SeekingSupported)
            {
                var currentPosition = mediaService.VideoPosition;
                if (currentPosition.TotalSeconds > 0.5)
                {
                    await mediaService.SeekAsync(currentPosition);
                    applied = true;
                    Console.WriteLine($"[MEDIA] Scale filter changed to '{filter}', resynced at {currentPosition.TotalSeconds:F1}s");
                }
            }

            return Results.Json(new
            {
                success = true,
                scaleFilter = filter,
                description = FfmpegCapabilities.AvailableScaleFilters[filter],
                message = $"Scale filter: {filter}",
                applied,
                note = !applied && mediaService.IsRunning 
                    ? "Restart playback to apply new filter" 
                    : null
            });
        });

        // List available MOD files
        group.MapGet("/music", () =>
        {
            return Results.Json(new
            {
                success = true,
                modPlayerAvailable = mediaService.ModPlayerAvailable,
                isModPlaying = mediaService.IsModPlaying,
                currentModFile = mediaService.CurrentModFile,
                availableFiles = mediaService.GetAvailableModFiles()
            });
        });

        // Set MOD file to play
        group.MapPost("/music/{filename}", (string filename) =>
        {
            if (!mediaService.ModPlayerAvailable)
                return Results.Json(new
                {
                    success = false,
                    message = "No MOD player available. Install xmp, mikmod, or openmpt123."
                });

            var filePath = Path.Combine(AppPaths.MusicDir, filename);

            if (!mediaService.SetModFile(filePath))
                return Results.Json(new
                {
                    success = false,
                    message = $"MOD file not found: {filename}"
                });

            return Results.Json(new
            {
                success = true,
                message = mediaService.IsRunning
                    ? $"Playing: {filename}"
                    : $"Music selected: {filename} (will play when video starts)",
                selectedModFile = filename,
                isModPlaying = mediaService.IsModPlaying
            });
        });

        // Stop MOD playback
        group.MapDelete("/music", () =>
        {
            mediaService.ClearModFile();

            return Results.Json(new
            {
                success = true,
                message = "Music stopped"
            });
        });

        // Upload a video file (up to 500MB)
        group.MapPost("/videos/upload", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType)
                return Results.Json(new { success = false, message = "Expected multipart/form-data" });

            // Read form with large file support
            var form = await request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = 500 * 1024 * 1024 // 500MB
            });
            var file = form.Files.FirstOrDefault();

            if (file == null || file.Length == 0)
                return Results.Json(new { success = false, message = "No file uploaded" });

            // Validate extension
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var validExtensions = new[] { ".mp4", ".webm", ".mkv", ".avi", ".mov", ".flv" };
            if (!validExtensions.Contains(ext))
                return Results.Json(new
                {
                    success = false,
                    message = $"Invalid file type. Supported: {string.Join(", ", validExtensions)}"
                });

            var filePath = Path.Combine(AppPaths.VideosDir, file.FileName);

            await using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream);

            Console.WriteLine($"[MEDIA] Video uploaded: {file.FileName} ({file.Length / 1024 / 1024}MB)");

            return Results.Json(new
            {
                success = true,
                message = $"Video uploaded: {file.FileName}",
                filename = file.FileName
            });
        }).DisableAntiforgery();

        // Upload a MOD file
        group.MapPost("/music/upload", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType)
                return Results.Json(new { success = false, message = "Expected multipart/form-data" });

            var form = await request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = 50 * 1024 * 1024 // 50MB for MOD files
            });
            var file = form.Files.FirstOrDefault();

            if (file == null || file.Length == 0)
                return Results.Json(new { success = false, message = "No file uploaded" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var validExtensions = new[] { ".mod", ".xm", ".s3m", ".it", ".stm", ".mtm" };
            if (!validExtensions.Contains(ext))
                return Results.Json(new
                {
                    success = false,
                    message = $"Invalid file type. Supported: {string.Join(", ", validExtensions)}"
                });

            var filePath = Path.Combine(AppPaths.MusicDir, file.FileName);

            await using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream);

            Console.WriteLine($"[MEDIA] MOD file uploaded: {file.FileName}");

            return Results.Json(new
            {
                success = true,
                message = $"Music uploaded: {file.FileName}",
                filename = file.FileName
            });
        }).DisableAntiforgery();

        // Delete a video
        group.MapDelete("/videos/{filename}", (string filename) =>
        {
            var filePath = Path.Combine(AppPaths.VideosDir, filename);

            if (!File.Exists(filePath)) return Results.Json(new { success = false, message = "Video not found" });

            try
            {
                File.Delete(filePath);
                Console.WriteLine($"[MEDIA] Video deleted: {filename}");

                return Results.Json(new
                {
                    success = true,
                    message = $"Deleted: {filename}"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    message = $"Failed to delete: {ex.Message}"
                });
            }
        });

        // ============================================================
        // AUDIO FILE ENDPOINTS
        // Audio playback with optional visualization
        // ============================================================

        // List available audio files
        group.MapGet("/audio/files", () =>
        {
            return Results.Json(new
            {
                success = true,
                files = mediaService.GetAvailableAudioFiles()
            });
        });

        // Upload an audio file
        group.MapPost("/audio/upload", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType)
                return Results.Json(new { success = false, message = "Expected multipart/form-data" });

            var form = await request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = 100 * 1024 * 1024 // 100MB for audio files
            });
            var file = form.Files.FirstOrDefault();

            if (file == null || file.Length == 0)
                return Results.Json(new { success = false, message = "No file uploaded" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var validExtensions = new[] { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a" };
            if (!validExtensions.Contains(ext))
                return Results.Json(new
                {
                    success = false,
                    message = $"Invalid file type. Supported: {string.Join(", ", validExtensions)}"
                });

            var filePath = Path.Combine(AppPaths.AudioDir, file.FileName);

            await using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream);

            Console.WriteLine($"[MEDIA] Audio uploaded: {file.FileName} ({file.Length / 1024}KB)");

            return Results.Json(new
            {
                success = true,
                message = $"Audio uploaded: {file.FileName}",
                filename = file.FileName
            });
        }).DisableAntiforgery();

        // Play an audio file (with optional visualization)
        group.MapPost("/audio/play/{filename}", async (string filename, bool? loop) =>
        {
            if (!MediaPlayerService.FFmpegAvailable)
                return Results.Json(new
                {
                    success = false,
                    message = "FFmpeg not available. Install with: sudo apt install ffmpeg"
                });

            var filePath = Path.Combine(AppPaths.AudioDir, filename);

            if (!File.Exists(filePath))
                return Results.Json(new
                {
                    success = false,
                    message = $"Audio file not found: {filename}. Upload audio files to the Media/Audio folder."
                });

            await mediaService.PlayAudioAsync(filePath, loop ?? false);
            
            // Record in play history
            favoritesService?.AddToHistory(new PlayHistoryItem
            {
                Type = MediaItemType.LocalAudio,
                Name = filename,
                LocalPath = filename
            });

            return Results.Json(new
            {
                success = true,
                message = $"Playing: {filename}",
                currentAudio = Path.GetFileName(filePath)
            });
        });

        // Replay last audio (handles both local and network)
        group.MapPost("/audio/replay", async () =>
        {
            if (string.IsNullOrEmpty(mediaService.LastPlayedAudio))
                return Results.Json(new { success = false, message = "No audio to replay. Play an audio file first." });

            if (mediaService.LastPlayedWasNetwork && !string.IsNullOrEmpty(mediaService.LastPlayedAudioUrl))
            {
                // Replay network audio
                Console.WriteLine($"[MEDIA] Replaying network audio: {mediaService.LastPlayedAudio}");
                await mediaService.PlayNetworkAudioAsync(
                    mediaService.LastPlayedAudioUrl,
                    "Network",
                    mediaService.LastPlayedAudio);

                return Results.Json(new
                {
                    success = true,
                    message = $"Playing: {mediaService.LastPlayedAudio}",
                    currentAudio = mediaService.LastPlayedAudio,
                    isNetwork = true
                });
            }

            // Replay local audio
            var filePath = Path.Combine(AppPaths.AudioDir, mediaService.LastPlayedAudio);

            if (!File.Exists(filePath))
                return Results.Json(new
                {
                    success = false,
                    message = $"Audio file not found: {mediaService.LastPlayedAudio}"
                });

            Console.WriteLine($"[MEDIA] Replaying local audio: {mediaService.LastPlayedAudio}");
            await mediaService.PlayAudioAsync(filePath);

            return Results.Json(new
            {
                success = true,
                message = $"Playing: {mediaService.LastPlayedAudio}",
                currentAudio = mediaService.LastPlayedAudio,
                isNetwork = false
            });
        });

        // Delete an audio file
        group.MapDelete("/audio/{filename}", (string filename) =>
        {
            var filePath = Path.Combine(AppPaths.AudioDir, filename);

            if (!File.Exists(filePath)) return Results.Json(new { success = false, message = "Audio file not found" });

            try
            {
                File.Delete(filePath);
                Console.WriteLine($"[MEDIA] Audio deleted: {filename}");

                return Results.Json(new
                {
                    success = true,
                    message = $"Deleted: {filename}"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    message = $"Failed to delete: {ex.Message}"
                });
            }
        });

        // ============================================================
        // PLAYLIST CONTROLS
        // Next/Previous track, shuffle, repeat
        // ============================================================

        // Skip to next track
        // Works even when stopped - just need a playlist
        group.MapPost("/next", async () =>
        {
            if (mediaService.AudioPlaylist.Count == 0)
                return Results.Json(new { success = false, message = "No audio playlist. Play an audio file first." });

            var success = await mediaService.PlayNextAsync();

            return Results.Json(new
            {
                success,
                message = success ? $"Playing: {mediaService.CurrentAudio}" : "End of playlist",
                currentAudio = mediaService.CurrentAudio,
                playlistIndex = mediaService.CurrentPlaylistIndex,
                playlistCount = mediaService.AudioPlaylist.Count
            });
        });

        // Skip to previous track
        // Works even when stopped - just need a playlist
        group.MapPost("/previous", async () =>
        {
            if (mediaService.AudioPlaylist.Count == 0)
                return Results.Json(new { success = false, message = "No audio playlist. Play an audio file first." });

            var success = await mediaService.PlayPreviousAsync();

            return Results.Json(new
            {
                success,
                message = success ? $"Playing: {mediaService.CurrentAudio}" : "Start of playlist",
                currentAudio = mediaService.CurrentAudio,
                playlistIndex = mediaService.CurrentPlaylistIndex,
                playlistCount = mediaService.AudioPlaylist.Count
            });
        });

        // Get playlist status
        group.MapGet("/playlist", () =>
        {
            return Results.Json(new
            {
                success = true,
                playlist = mediaService.AudioPlaylist,
                currentIndex = mediaService.CurrentPlaylistIndex,
                currentTrack = mediaService.CurrentAudio,
                autoAdvance = mediaService.AutoAdvance,
                shuffleMode = mediaService.ShuffleMode,
                repeatMode = mediaService.RepeatMode,
                hasNext = mediaService.HasNextTrack,
                hasPrevious = mediaService.HasPreviousTrack
            });
        });

        // Set auto-advance mode
        group.MapPost("/playlist/auto-advance", (bool enabled) =>
        {
            mediaService.AutoAdvance = enabled;

            return Results.Json(new
            {
                success = true,
                autoAdvance = mediaService.AutoAdvance,
                message = enabled ? "Auto-advance enabled" : "Auto-advance disabled"
            });
        });

        // Set shuffle mode
        group.MapPost("/playlist/shuffle", (bool enabled) =>
        {
            mediaService.ShuffleMode = enabled;

            return Results.Json(new
            {
                success = true,
                shuffleMode = mediaService.ShuffleMode,
                message = enabled ? "Shuffle enabled" : "Shuffle disabled"
            });
        });

        // Set repeat mode
        group.MapPost("/playlist/repeat", (bool enabled) =>
        {
            mediaService.RepeatMode = enabled;

            return Results.Json(new
            {
                success = true,
                repeatMode = mediaService.RepeatMode,
                message = enabled ? "Repeat enabled" : "Repeat disabled"
            });
        });

        // ============================================================
        // YOUTUBE REPLAY
        // Replay last played YouTube video (re-fetches stream URLs)
        // ============================================================
        
        group.MapPost("/youtube/replay", async () =>
        {
            if (string.IsNullOrEmpty(mediaService.LastPlayedYouTubeUrl))
                return Results.Json(new { success = false, message = "No YouTube video to replay. Play a YouTube video first." });

            Console.WriteLine($"[MEDIA] Replaying YouTube: {mediaService.LastPlayedYouTubeTitle}");
            
            var success = await mediaService.PlayYouTubeVideoAsync(mediaService.LastPlayedYouTubeUrl);

            return Results.Json(new
            {
                success,
                message = success 
                    ? $"Playing: {mediaService.LastPlayedYouTubeTitle}" 
                    : (mediaService.LastPlaybackError ?? "Failed to replay YouTube video (URL may have expired)"),
                youtubeUrl = mediaService.LastPlayedYouTubeUrl,
                title = mediaService.LastPlayedYouTubeTitle
            });
        });
    }
}
