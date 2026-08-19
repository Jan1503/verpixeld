using Microsoft.Extensions.DependencyInjection;
using verpixeld.MediaPlayer;

namespace verpixeld.WebApi;

/// <summary>
/// API endpoints for YouTube video playback
/// </summary>
public static class YouTubeEndpoints
{
    public static void MapYouTubeEndpoints(this WebApplication app)
    {
        var mediaService = app.Services.GetRequiredService<MediaPlayerService>();
        var favoritesService = app.Services.GetRequiredService<FavoritesService>();

        var group = app.MapGroup("/api/youtube");

        // Check if YouTube support is available
        group.MapGet("/status", () =>
        {
            return Results.Json(new
            {
                success = true,
                ytDlpAvailable = MediaPlayerService.YtDlpAvailable,
                ffmpegAvailable = MediaPlayerService.FFmpegAvailable
            });
        });

        // Get video info without playing
        group.MapPost("/info", async (YouTubeInfoRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.Json(new { success = false, message = "URL is required" });
            }

            if (!MediaPlayerService.YtDlpAvailable)
            {
                return Results.Json(new { success = false, message = "yt-dlp not available. Install with: pip install yt-dlp" });
            }

            if (!YouTubeService.IsYouTubeUrl(request.Url))
            {
                return Results.Json(new { success = false, message = "Not a valid YouTube URL" });
            }

            var ytService = new YouTubeService();
            var info = await ytService.GetVideoInfoAsync(request.Url);

            if (info == null)
            {
                return Results.Json(new { success = false, message = "Failed to get video info" });
            }

            // Get format that would be selected for current canvas size
            var canvasWidth = request.CanvasWidth ?? 384;
            var canvasHeight = request.CanvasHeight ?? 192;
            var selectedFormat = ytService.SelectBestFormat(info, canvasWidth, canvasHeight);

            return Results.Json(new
            {
                success = true,
                video = new
                {
                    id = info.Id,
                    title = info.Title,
                    channel = info.Channel ?? info.Uploader,
                    duration = info.Duration.TotalSeconds,
                    durationFormatted = info.Duration.ToString(@"mm\:ss"),
                    thumbnail = info.Thumbnail,
                    formatCount = info.Formats.Count,
                    selectedFormat = selectedFormat != null ? new
                    {
                        formatId = selectedFormat.FormatId,
                        width = selectedFormat.Width,
                        height = selectedFormat.Height,
                        fps = selectedFormat.Fps,
                        isCombined = selectedFormat.IsCombined,
                        bitrate = selectedFormat.Tbr ?? selectedFormat.Vbr
                    } : null
                }
            });
        });

        // Play YouTube video or generic stream URL
        group.MapPost("/play", async (YouTubePlayRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.Json(new { success = false, message = "URL is required" });
            }

            var url = request.Url.Trim();
            
            // Branch: YouTube URL → yt-dlp flow, generic URL → direct FFmpeg playback
            if (YouTubeService.IsYouTubeUrl(url))
            {
                // ── YouTube path (existing logic) ──
                if (!MediaPlayerService.YtDlpAvailable)
                {
                    return Results.Json(new { success = false, message = "yt-dlp not available. Install with: pip install yt-dlp" });
                }

                Console.WriteLine($"[STREAM-API] YouTube play request: {url}");

                var success = await mediaService.PlayYouTubeVideoAsync(url, request.Loop);

                if (success)
                {
                    var info = mediaService.CurrentYouTubeInfo;
                    
                    // Record in play history
                    favoritesService?.AddToHistory(new PlayHistoryItem
                    {
                        Type = MediaItemType.YouTube,
                        Name = info?.Title ?? "YouTube video",
                        YouTubeUrl = url,
                        Thumbnail = info?.Thumbnail
                    });
                    
                    return Results.Json(new
                    {
                        success = true,
                        message = $"Playing: {info?.Title ?? "YouTube video"}",
                        isStream = false,
                        video = info != null ? new
                        {
                            id = info.VideoId,
                            title = info.Title,
                            channel = info.Channel,
                            duration = info.Duration.TotalSeconds,
                            width = info.Width,
                            height = info.Height,
                            isAdaptive = info.IsAdaptive
                        } : null
                    });
                }
                else
                {
                    var errorMsg = mediaService.LastPlaybackError ?? "Failed to play YouTube video. Check URL and try again.";
                    return Results.Json(new { success = false, message = errorMsg });
                }
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                // ── Generic stream path (camera, HLS, RTSP, etc.) ──
                Console.WriteLine($"[STREAM-API] Generic stream play request: {url}");

                try
                {
                    // Extract a display name from the URL
                    var uri = new Uri(url);
                    var displayName = !string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/"
                        ? Path.GetFileName(uri.AbsolutePath)
                        : uri.Host;
                    if (string.IsNullOrEmpty(displayName)) displayName = url;
                    
                    var protocol = uri.Scheme.ToLowerInvariant(); // http, https, rtsp
                    
                    await mediaService.PlayNetworkVideoAsync(
                        url,
                        "Stream",
                        displayName,
                        protocol,
                        request.Loop);

                    // Record in play history
                    favoritesService?.AddToHistory(new PlayHistoryItem
                    {
                        Type = MediaItemType.NetworkVideo,
                        Name = displayName,
                        NetworkUrl = url,
                        NetworkShareName = "Stream",
                        NetworkFilePath = displayName,
                        NetworkProtocol = protocol
                    });

                    return Results.Json(new
                    {
                        success = true,
                        message = $"Playing stream: {displayName}",
                        isStream = true,
                        video = (object?)null
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[STREAM-API] Stream play error: {ex.Message}");
                    return Results.Json(new { success = false, message = $"Failed to play stream: {ex.Message}" });
                }
            }
            else
            {
                return Results.Json(new { success = false, message = "Unsupported URL. Enter a YouTube, HTTP, HTTPS, or RTSP URL." });
            }
        });

        // List available formats for a video
        group.MapPost("/formats", async (YouTubeInfoRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.Json(new { success = false, message = "URL is required" });
            }

            if (!MediaPlayerService.YtDlpAvailable)
            {
                return Results.Json(new { success = false, message = "yt-dlp not available" });
            }

            var ytService = new YouTubeService();
            var info = await ytService.GetVideoInfoAsync(request.Url);

            if (info == null)
            {
                return Results.Json(new { success = false, message = "Failed to get video info" });
            }

            // Group formats by type
            var videoFormats = info.Formats
                .Where(f => f.HasVideo)
                .Select(f => new
                {
                    formatId = f.FormatId,
                    extension = f.Extension,
                    width = f.Width,
                    height = f.Height,
                    fps = f.Fps,
                    codec = f.VCodec,
                    bitrate = f.Vbr ?? f.Tbr,
                    hasAudio = f.HasAudio,
                    isCombined = f.IsCombined
                })
                .OrderByDescending(f => f.height)
                .ToList();

            var audioFormats = info.Formats
                .Where(f => f.HasAudio && !f.HasVideo)
                .Select(f => new
                {
                    formatId = f.FormatId,
                    extension = f.Extension,
                    codec = f.ACodec,
                    bitrate = f.Abr ?? f.Tbr
                })
                .OrderByDescending(f => f.bitrate)
                .ToList();

            return Results.Json(new
            {
                success = true,
                title = info.Title,
                videoFormats,
                audioFormats
            });
        });
    }
}

public record YouTubeInfoRequest(string Url, int? CanvasWidth = null, int? CanvasHeight = null);
public record YouTubePlayRequest(string Url, bool Loop = false);
