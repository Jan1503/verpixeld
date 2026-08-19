using Microsoft.Extensions.DependencyInjection;
using verpixeld.MediaPlayer;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for YouTube Music search and playback.
/// </summary>
public static class MusicSearchEndpoints
{
    public static void MapMusicSearchEndpoints(this WebApplication app)
    {
        var musicService = app.Services.GetRequiredService<MusicSearchService>();
        var mediaService = app.Services.GetRequiredService<MediaPlayerService>();

        var group = app.MapGroup("/api/music");

        // Search YouTube Music
        group.MapPost("/search", async (MusicSearchRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.Json(new { success = false, message = "Query is required" });

            try
            {
                var results = await musicService.SearchAsync(request.Query, request.MaxResults, request.PreferVideo);
                return Results.Json(new
                {
                    success = true,
                    query = request.Query,
                    preferVideo = request.PreferVideo,
                    results = results.Select(r => new
                    {
                        id = r.Id,
                        title = r.Title,
                        artist = r.Artist,
                        album = r.Album,
                        url = r.Url,
                        type = r.Type,
                        duration = r.Duration.TotalSeconds > 0
                            ? $"{(int)r.Duration.TotalMinutes}:{r.Duration.Seconds:D2}"
                            : (string?)null
                    })
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MUSIC/API] Search error: {ex.Message}");
                return Results.Json(new { success = false, message = "Search failed: " + ex.Message });
            }
        });

        // Search and immediately play top result (or play a specific URL)
        group.MapPost("/play", async (MusicPlayRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query) && string.IsNullOrWhiteSpace(request.Url))
                return Results.Json(new { success = false, message = "Query or URL is required" });

            try
            {
                string url;
                string title;
                string artist;

                if (!string.IsNullOrWhiteSpace(request.Url))
                {
                    // Direct URL provided (e.g. from search results)
                    url = request.Url;
                    title = request.Title ?? "Unknown";
                    artist = request.Artist ?? "";
                }
                else
                {
                    // Search and get top result
                    var result = await musicService.SearchAndGetUrlAsync(request.Query!, request.PreferVideo);
                    if (result == null)
                        return Results.Json(new { success = false, message = $"No results found for \"{request.Query}\"" });

                    url = result.Url;
                    title = result.Title;
                    artist = result.Artist;
                }

                // Play via media player (yt-dlp handles YouTube Music URLs)
                // audioOnly = true means no video on display, just audio playback
                var playSuccess = await mediaService.PlayYouTubeVideoAsync(url, false, request.AudioOnly);

                if (!playSuccess)
                {
                    var error = mediaService.LastPlaybackError ?? "Playback failed";
                    return Results.Json(new { success = false, message = error });
                }

                return Results.Json(new
                {
                    success = true,
                    message = $"Playing: {title}" + (string.IsNullOrEmpty(artist) ? "" : $" by {artist}"),
                    audioOnly = request.AudioOnly,
                    url,
                    title,
                    artist
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MUSIC/API] Play error: {ex.Message}");
                return Results.Json(new { success = false, message = "Playback failed: " + ex.Message });
            }
        });
    }
}

public record MusicSearchRequest(string Query, int MaxResults = 10, bool PreferVideo = false);
public record MusicPlayRequest(string? Query = null, string? Url = null, string? Title = null, string? Artist = null,
    bool PreferVideo = false, bool AudioOnly = false);
