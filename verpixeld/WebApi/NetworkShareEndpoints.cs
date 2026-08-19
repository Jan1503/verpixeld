using Microsoft.Extensions.DependencyInjection;
using verpixeld.MediaPlayer;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for managing SMB network shares
/// </summary>
public static class NetworkShareEndpoints
{
    public static void MapNetworkShareEndpoints(this WebApplication app)
    {
        var shareService = app.Services.GetRequiredService<NetworkShareService>();
        var mediaService = app.Services.GetRequiredService<MediaPlayerService>();
        var favoritesService = app.Services.GetRequiredService<FavoritesService>();

        var group = app.MapGroup("/api/network");

        // List all shares
        group.MapGet("/shares", () =>
        {
            var ffmpegSmb = MediaPlayerService.FFmpegSmbSupported;

            string? message = null;
            if (!ffmpegSmb) message = "SMB streaming not available. Compile FFmpeg with --enable-libsmbclient.";

            return Results.Json(new
            {
                success = true,
                ffmpegSmbSupported = ffmpegSmb,
                networkStreamingSupported = ffmpegSmb,
                smbSupportMessage = message,
                shares = shareService.Shares.Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Server,
                    s.SharePath,
                    s.Domain,
                    s.Username,
                    hasPassword = !string.IsNullOrEmpty(s.EncryptedPassword),
                    s.IsDefault,
                    displayUrl = s.GetDisplayUrl()
                })
            });
        });

        // Add a new share
        group.MapPost("/shares", (NetworkShareRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) ||
                string.IsNullOrWhiteSpace(request.Server))
                return Results.Json(new { success = false, message = "Name and server are required" });

            var share = shareService.AddShare(
                request.Name,
                request.Server,
                request.SharePath ?? "",
                request.Domain,
                request.Username,
                request.Password
            );

            return Results.Json(new
            {
                success = true,
                message = $"Share added: {share.Name}",
                share = new
                {
                    share.Id,
                    share.Name,
                    share.Server,
                    share.SharePath,
                    share.Domain,
                    share.Username,
                    hasPassword = !string.IsNullOrEmpty(share.EncryptedPassword),
                    share.IsDefault,
                    displayUrl = share.GetDisplayUrl()
                }
            });
        });

        // Update a share
        group.MapPut("/shares/{id}", (string id, NetworkShareRequest request) =>
        {
            var success = shareService.UpdateShare(
                id,
                request.Name,
                request.Server,
                request.SharePath,
                request.Domain,
                request.Username,
                request.Password,
                request.IsDefault
            );

            if (!success) return Results.Json(new { success = false, message = "Share not found" });

            var share = shareService.GetShare(id);
            return Results.Json(new
            {
                success = true,
                message = "Share updated",
                share = share != null
                    ? new
                    {
                        share.Id,
                        share.Name,
                        share.Server,
                        share.SharePath,
                        share.Domain,
                        share.Username,
                        hasPassword = !string.IsNullOrEmpty(share.EncryptedPassword),
                        share.IsDefault,
                        displayUrl = share.GetDisplayUrl()
                    }
                    : null
            });
        });

        // Delete a share
        group.MapDelete("/shares/{id}", (string id) =>
        {
            var success = shareService.RemoveShare(id);

            return Results.Json(new
            {
                success,
                message = success ? "Share removed" : "Share not found"
            });
        });

        // Test connection
        group.MapPost("/shares/{id}/test", async (string id) =>
        {
            var (success, message) = await shareService.TestConnectionAsync(id);

            return Results.Json(new
            {
                success,
                message
            });
        });

        // Browse a directory on a share
        group.MapGet("/shares/{id}/browse", async (string id, string? path, bool? refresh) =>
        {
            var share = shareService.GetShare(id);
            if (share == null) return Results.Json(new { success = false, message = "Share not found" });

            var forceRefresh = refresh ?? false;
            var result = await shareService.BrowseDirectoryAsync(id, path ?? "", forceRefresh);

            return Results.Json(new
            {
                success = true,
                shareId = id,
                shareName = share.Name,
                currentPath = result.Path,
                parentPath = GetParentPath(result.Path),
                directories = result.Directories,
                videos = result.Videos,
                audioFiles = result.AudioFiles,
                fromCache = result.FromCache
            });
        });

        // Play a video from a share
        group.MapPost("/shares/{id}/play/{*filePath}", async (string id, string filePath, bool? loop) =>
        {
            var share = shareService.GetShare(id);
            if (share == null) return Results.Json(new { success = false, message = "Share not found" });

            // Decode the path (browser encodes it)
            var decodedPath = Uri.UnescapeDataString(filePath);
            Console.WriteLine($"[NETWORK] Play request: {decodedPath}");

            // Build SMB URL
            var videoUrl = shareService.BuildFileUrl(id, decodedPath);
            if (videoUrl == null) return Results.Json(new { success = false, message = "Failed to build video URL" });

            // Mask password in logs
            var displayUrl = videoUrl;
            if (displayUrl.Contains('@'))
            {
                var atIndex = displayUrl.IndexOf('@');
                var protocolEnd = displayUrl.IndexOf("://") + 3;
                var credsPart = displayUrl.Substring(protocolEnd, atIndex - protocolEnd);
                if (credsPart.Contains(':'))
                {
                    var userEnd = credsPart.IndexOf(':');
                    displayUrl = displayUrl.Substring(0, protocolEnd + userEnd + 1) + "***" +
                                 displayUrl.Substring(atIndex);
                }
            }

            Console.WriteLine($"[NETWORK] Video URL: {displayUrl}");

            // Check if we can stream
            if (!MediaPlayerService.FFmpegSmbSupported)
                return Results.Json(new
                {
                    success = false,
                    message = "SMB streaming not available. Compile FFmpeg with --enable-libsmbclient."
                });

            // Play the video via media service (start playback immediately)
            await mediaService.PlayNetworkVideoAsync(
                videoUrl,
                share.Name,
                decodedPath,
                "smb",
                loop ?? true
            );
            
            // Extract thumbnail in background (don't wait for it)
            string? thumbnail = null;
            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[NETWORK] Extracting thumbnail for: {Path.GetFileName(decodedPath)}");
                    thumbnail = await MediaProbeService.ExtractThumbnailAsync(videoUrl);
                    if (thumbnail != null)
                    {
                        Console.WriteLine($"[NETWORK] Thumbnail extracted: {thumbnail.Length} chars");
                        // Update history entry with thumbnail
                        favoritesService?.UpdateHistoryThumbnail(videoUrl, thumbnail);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NETWORK] Thumbnail extraction failed: {ex.Message}");
                }
            });
            
            // Record in play history (thumbnail may be added later)
            favoritesService?.AddToHistory(new PlayHistoryItem
            {
                Type = MediaItemType.NetworkVideo,
                Name = Path.GetFileName(decodedPath),
                NetworkUrl = videoUrl,
                NetworkShareName = share.Name,
                NetworkFilePath = decodedPath,
                NetworkProtocol = "smb"
            });

            return Results.Json(new
            {
                success = true,
                message = $"Playing: {Path.GetFileName(decodedPath)}",
                videoUrl = displayUrl,
                seekingSupported = true
            });
        });

        // Play an audio file from a share
        group.MapPost("/shares/{id}/play-audio/{*filePath}", async (string id, string filePath, bool? loop) =>
        {
            var share = shareService.GetShare(id);
            if (share == null) return Results.Json(new { success = false, message = "Share not found" });

            // Decode the path (browser encodes it)
            var decodedPath = Uri.UnescapeDataString(filePath);
            Console.WriteLine($"[NETWORK] Play audio request: {decodedPath}");

            // Build SMB URL
            var audioUrl = shareService.BuildFileUrl(id, decodedPath);
            if (audioUrl == null) return Results.Json(new { success = false, message = "Failed to build audio URL" });

            // Mask password in logs
            var displayUrl = audioUrl;
            if (displayUrl.Contains('@'))
            {
                var atIndex = displayUrl.IndexOf('@');
                var protocolEnd = displayUrl.IndexOf("://") + 3;
                var credsPart = displayUrl.Substring(protocolEnd, atIndex - protocolEnd);
                if (credsPart.Contains(':'))
                {
                    var userEnd = credsPart.IndexOf(':');
                    displayUrl = displayUrl.Substring(0, protocolEnd + userEnd + 1) + "***" +
                                 displayUrl.Substring(atIndex);
                }
            }

            Console.WriteLine($"[NETWORK] Audio URL: {displayUrl}");

            // Check if we can stream
            if (!MediaPlayerService.FFmpegSmbSupported)
                return Results.Json(new
                {
                    success = false,
                    message = "SMB streaming not available. Compile FFmpeg with --enable-libsmbclient."
                });

            // Play the audio via media service
            await mediaService.PlayNetworkAudioAsync(
                audioUrl,
                share.Name,
                decodedPath,
                loop ?? false
            );
            
            // Record in play history
            favoritesService?.AddToHistory(new PlayHistoryItem
            {
                Type = MediaItemType.NetworkAudio,
                Name = Path.GetFileName(decodedPath),
                NetworkUrl = audioUrl,
                NetworkShareName = share.Name,
                NetworkFilePath = decodedPath,
                NetworkProtocol = "smb"
            });

            return Results.Json(new
            {
                success = true,
                message = $"Playing: {Path.GetFileName(decodedPath)}",
                audioUrl = displayUrl
            });
        });

        // Clear directory cache
        group.MapPost("/cache/clear", (string? shareId) =>
        {
            shareService.ClearCache(shareId);
            return Results.Json(new
            {
                success = true,
                message = shareId != null ? $"Cache cleared for share {shareId}" : "All cache cleared"
            });
        });

        // Get cache stats
        group.MapGet("/cache/stats", () =>
        {
            var (total, expired) = shareService.GetCacheStats();
            return Results.Json(new
            {
                success = true,
                totalEntries = total,
                expiredEntries = expired
            });
        });
    }

    private static string? GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash <= 0) return "";

        return path.Substring(0, lastSlash);
    }
}

/// <summary>
///     Request model for creating/updating a network share
/// </summary>
public class NetworkShareRequest
{
    public string? Name { get; set; }
    public string? Server { get; set; }
    public string? SharePath { get; set; }
    public string? Domain { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool? IsDefault { get; set; }
}
