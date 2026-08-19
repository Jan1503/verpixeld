using System.Text.Json;
using System.Text.Json.Serialization;

namespace verpixeld.Services;

/// <summary>
///     Searches the free Radio Browser API (radio-browser.info) for internet radio stations
///     by genre/tag. Returns direct HTTP stream URLs that can be played instantly by ffmpeg.
///     No API key required.
/// </summary>
public class RadioBrowserService
{
    private static readonly string[] ApiServers =
    {
        "https://de2.api.radio-browser.info",
        "https://de1.api.radio-browser.info",
        "https://at1.api.radio-browser.info"
    };

    private readonly HttpClient _http;

    /// <summary>
    ///     Maps common genre names (as spoken by the user / extracted by LLM) to Radio Browser tags.
    ///     Keys are lowercase. Values can be comma-separated for OR-style matching.
    /// </summary>
    private static readonly Dictionary<string, string> GenreTagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Electronic
        { "trance", "trance" },
        { "techno", "techno" },
        { "house", "house" },
        { "deep house", "deep house" },
        { "edm", "edm" },
        { "electronic", "electronic" },
        { "electro", "electro" },
        { "drum and bass", "drum and bass" },
        { "dnb", "drum and bass" },
        { "dubstep", "dubstep" },
        { "ambient", "ambient" },
        { "chillout", "chillout" },
        { "chill", "chillout" },
        { "lounge", "lounge" },
        { "downtempo", "downtempo" },

        // Rock & Metal
        { "rock", "rock" },
        { "classic rock", "classic rock" },
        { "metal", "metal" },
        { "hard rock", "hard rock" },
        { "punk", "punk" },
        { "alternative", "alternative" },
        { "indie", "indie" },
        { "grunge", "grunge" },

        // Pop & Charts
        { "pop", "pop" },
        { "charts", "top 40" },
        { "top 40", "top 40" },
        { "hits", "hits" },
        { "80s", "80s" },
        { "90s", "90s" },
        { "80er", "80s" },
        { "90er", "90s" },
        { "oldies", "oldies" },
        { "retro", "retro" },
        { "disco", "disco" },

        // Jazz, Blues, Soul
        { "jazz", "jazz" },
        { "blues", "blues" },
        { "soul", "soul" },
        { "funk", "funk" },
        { "smooth jazz", "smooth jazz" },

        // Classical
        { "classical", "classical" },
        { "klassik", "classical" },
        { "opera", "opera" },

        // Hip-Hop & R&B
        { "hip hop", "hip-hop" },
        { "hiphop", "hip-hop" },
        { "rap", "rap" },
        { "r&b", "rnb" },
        { "rnb", "rnb" },
        { "urban", "urban" },

        // Country & Folk
        { "country", "country" },
        { "folk", "folk" },
        { "americana", "americana" },
        { "bluegrass", "bluegrass" },

        // Latin & World
        { "latin", "latin" },
        { "reggae", "reggae" },
        { "reggaeton", "reggaeton" },
        { "salsa", "salsa" },
        { "bossa nova", "bossa nova" },
        { "world", "world" },
        { "african", "african" },

        // Relaxation
        { "meditation", "meditation" },
        { "sleep", "sleep" },
        { "nature", "nature" },
        { "relaxation", "relax" },
        { "spa", "spa" },

        // German
        { "schlager", "schlager" },
        { "deutsch", "german" },
        { "deutschrock", "german rock" },
        { "volksmusik", "volksmusik" },

        // Charts / Trending
        { "trending", "hits" },
        { "trend", "hits" },
        { "aktuell", "top 40" },
        { "current", "top 40" },

        // Other
        { "news", "news" },
        { "talk", "talk" },
        { "comedy", "comedy" },
        { "dance", "dance" },
        { "party", "party" },
        { "musik", "music" },
        { "music", "music" },
    };

    public RadioBrowserService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "verpixeld/1.0");
        _http.Timeout = TimeSpan.FromSeconds(8);
        Console.WriteLine("[RADIO-BROWSER] Internet radio service initialized");
    }

    /// <summary>
    ///     Search for radio stations matching a genre/tag.
    ///     Returns stations sorted by popularity (click count), verified online.
    ///     If the genre contains commas, tries each part individually until results are found.
    /// </summary>
    public async Task<List<RadioStation>> SearchStationsAsync(string genre, int limit = 10)
    {
        var tag = MapGenreToTag(genre);

        // Try the full tag first
        var results = await SearchByTagAsync(tag, limit);
        if (results.Count > 0)
            return results;

        // If the tag contains commas (LLM sometimes returns "trending, charts"),
        // try each part individually
        if (tag.Contains(','))
        {
            var parts = tag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var mapped = MapGenreToTag(part);
                results = await SearchByTagAsync(mapped, limit);
                if (results.Count > 0)
                    return results;
            }
        }

        // Last resort: try the raw genre words individually
        var words = genre.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var word in words)
        {
            if (word.Length < 3) continue; // skip tiny words
            var mapped = MapGenreToTag(word);
            if (mapped == tag) continue; // already tried this
            results = await SearchByTagAsync(mapped, limit);
            if (results.Count > 0)
                return results;
        }

        return new List<RadioStation>();
    }

    /// <summary>
    ///     Search for stations by a single tag against all API servers.
    /// </summary>
    private async Task<List<RadioStation>> SearchByTagAsync(string tag, int limit)
    {
        Console.WriteLine($"[RADIO-BROWSER] Searching tag=\"{tag}\", limit={limit}");

        foreach (var server in ApiServers)
        {
            try
            {
                var url = $"{server}/json/stations/search?tag={Uri.EscapeDataString(tag)}" +
                          $"&lastcheckok=1&order=clickcount&reverse=true&limit={limit}";

                var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var stations = JsonSerializer.Deserialize<List<RadioBrowserStation>>(json);

                if (stations == null || stations.Count == 0)
                {
                    Console.WriteLine($"[RADIO-BROWSER] No stations found for tag \"{tag}\"");
                    continue;
                }

                var results = stations
                    .Where(s => !string.IsNullOrEmpty(s.UrlResolved) && s.LastCheckOk == 1)
                    .Select(s => new RadioStation
                    {
                        Name = s.Name?.Trim() ?? "Unknown Station",
                        StreamUrl = s.UrlResolved!,
                        Tags = s.Tags ?? "",
                        Country = s.Country ?? "",
                        CountryCode = s.CountryCode ?? "",
                        Codec = s.Codec ?? "",
                        Bitrate = s.Bitrate,
                        Favicon = s.Favicon ?? "",
                        Votes = s.Votes,
                        ClickCount = s.ClickCount
                    })
                    .ToList();

                if (results.Count > 0)
                {
                    Console.WriteLine($"[RADIO-BROWSER] Found {results.Count} stations for \"{tag}\"");
                    Console.WriteLine($"[RADIO-BROWSER] Top: \"{results[0].Name}\" ({results[0].Codec} {results[0].Bitrate}kbps) — {results[0].StreamUrl}");
                    return results;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RADIO-BROWSER] Server {server} failed: {ex.Message}");
            }
        }

        return new List<RadioStation>();
    }

    /// <summary>
    ///     Map a user-spoken genre to an appropriate Radio Browser tag.
    ///     Falls back to using the genre directly as the tag.
    /// </summary>
    private static string MapGenreToTag(string genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return "music";

        var trimmed = genre.Trim();

        // Direct lookup
        if (GenreTagMap.TryGetValue(trimmed, out var tag))
            return tag;

        // Try without common suffixes ("musik", "music", "radio")
        var cleaned = trimmed
            .Replace(" musik", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" music", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" radio", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (GenreTagMap.TryGetValue(cleaned, out var cleanedTag))
            return cleanedTag;

        // Fall back to using the genre directly as the tag
        return cleaned.ToLowerInvariant();
    }
}

/// <summary>
///     A radio station returned by the Radio Browser API search.
/// </summary>
public class RadioStation
{
    public string Name { get; set; } = "";
    public string StreamUrl { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Country { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string Codec { get; set; } = "";
    public int Bitrate { get; set; }
    public string Favicon { get; set; } = "";
    public int Votes { get; set; }
    public int ClickCount { get; set; }
}

/// <summary>
///     Raw JSON model from Radio Browser API response.
/// </summary>
internal class RadioBrowserStation
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("url_resolved")]
    public string? UrlResolved { get; set; }

    [JsonPropertyName("favicon")]
    public string? Favicon { get; set; }

    [JsonPropertyName("tags")]
    public string? Tags { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("countrycode")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; }

    [JsonPropertyName("votes")]
    public int Votes { get; set; }

    [JsonPropertyName("clickcount")]
    public int ClickCount { get; set; }

    [JsonPropertyName("lastcheckok")]
    public int LastCheckOk { get; set; }
}
