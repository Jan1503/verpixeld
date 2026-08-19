using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Search;

namespace verpixeld.Services;

/// <summary>
///     Searches YouTube Music for songs or music videos and returns playable URLs.
///     Uses the YouTubeMusicAPI NuGet package (no API key required).
/// </summary>
public class MusicSearchService
{
    private readonly YouTubeMusicClient _client;

    public MusicSearchService()
    {
        _client = new YouTubeMusicClient();
        Console.WriteLine("[MUSIC] YouTube Music search service initialized");
    }

    /// <summary>
    ///     Search for a song/video and return the top result as a playable YouTube Music URL.
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="preferVideo">If true, search for music videos instead of songs</param>
    public async Task<MusicSearchResult?> SearchAndGetUrlAsync(string query, bool preferVideo = false)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        try
        {
            var category = preferVideo ? SearchCategory.Videos : SearchCategory.Songs;
            Console.WriteLine($"[MUSIC] Searching ({category}): \"{query}\"");

            var searchResults = _client.SearchAsync(query, category);
            var items = await searchResults.FetchItemsAsync(0, 5);

            if (items == null || items.Count == 0)
            {
                Console.WriteLine("[MUSIC] No results found");
                return null;
            }

            if (preferVideo)
            {
                var video = items.Cast<VideoSearchResult>().FirstOrDefault();
                if (video == null) return null;

                var url = $"https://music.youtube.com/watch?v={video.Id}";
                var artists = string.Join(", ", video.Artists.Select(a => a.Name));
                Console.WriteLine($"[MUSIC] Found video: \"{video.Name}\" by {artists} → {url}");

                return new MusicSearchResult
                {
                    Url = url,
                    Title = video.Name,
                    Artist = artists,
                    Id = video.Id,
                    Duration = video.Duration,
                    Type = "video"
                };
            }
            else
            {
                var song = items.Cast<SongSearchResult>().FirstOrDefault();
                if (song == null) return null;

                var url = $"https://music.youtube.com/watch?v={song.Id}";
                var artists = string.Join(", ", song.Artists.Select(a => a.Name));
                Console.WriteLine($"[MUSIC] Found song: \"{song.Name}\" by {artists} → {url}");

                return new MusicSearchResult
                {
                    Url = url,
                    Title = song.Name,
                    Artist = artists,
                    Album = song.Album?.Name,
                    Id = song.Id,
                    Duration = song.Duration,
                    Type = "song"
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MUSIC] Search error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Search for songs or music videos and return multiple results.
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="maxResults">Maximum number of results</param>
    /// <param name="preferVideo">If true, search for music videos instead of songs</param>
    public async Task<List<MusicSearchResult>> SearchAsync(string query, int maxResults = 10, bool preferVideo = false)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<MusicSearchResult>();

        try
        {
            var category = preferVideo ? SearchCategory.Videos : SearchCategory.Songs;
            Console.WriteLine($"[MUSIC] Searching ({category}, max {maxResults}): \"{query}\"");

            var searchResults = _client.SearchAsync(query, category);
            var items = await searchResults.FetchItemsAsync(0, maxResults);

            if (items == null || items.Count == 0)
                return new List<MusicSearchResult>();

            if (preferVideo)
            {
                return items.Cast<VideoSearchResult>().Select(video => new MusicSearchResult
                {
                    Url = $"https://music.youtube.com/watch?v={video.Id}",
                    Title = video.Name,
                    Artist = string.Join(", ", video.Artists.Select(a => a.Name)),
                    Id = video.Id,
                    Duration = video.Duration,
                    Type = "video"
                }).ToList();
            }
            else
            {
                return items.Cast<SongSearchResult>().Select(song => new MusicSearchResult
                {
                    Url = $"https://music.youtube.com/watch?v={song.Id}",
                    Title = song.Name,
                    Artist = string.Join(", ", song.Artists.Select(a => a.Name)),
                    Album = song.Album?.Name,
                    Id = song.Id,
                    Duration = song.Duration,
                    Type = "song"
                }).ToList();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MUSIC] Search error: {ex.Message}");
            return new List<MusicSearchResult>();
        }
    }
}

public class MusicSearchResult
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Album { get; set; }
    public string Id { get; set; } = "";
    public TimeSpan Duration { get; set; }
    /// <summary>"song" or "video"</summary>
    public string Type { get; set; } = "song";
}
