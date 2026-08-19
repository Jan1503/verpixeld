using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace verpixeld.MediaPlayer;

/// <summary>
/// YouTube video playback support using yt-dlp for URL extraction
/// Supports format selection based on canvas dimensions to save bandwidth
/// </summary>
public class YouTubeService
{
    private static bool? _ytDlpAvailable;
    private static readonly object _lock = new();

    // Temporary workaround for the YouTube player-JS / SABR breakage
    // (https://github.com/yt-dlp/yt-dlp/issues/17456#issuecomment-5325954656).
    // Force the android client so extraction works until yt-dlp ships a fix.
    private const string YoutubeExtractorArgs = "youtube:player_client=android";

    private static void AddYoutubeWorkaroundArgs(ProcessStartInfo psi)
    {
        psi.ArgumentList.Add("--extractor-args");
        psi.ArgumentList.Add(YoutubeExtractorArgs);
    }
    
    /// <summary>
    /// Video format information from yt-dlp
    /// </summary>
    public class YouTubeFormat
    {
        public string FormatId { get; set; } = "";
        public string Extension { get; set; } = "";
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? Fps { get; set; }
        public string? VCodec { get; set; }
        public string? ACodec { get; set; }
        public long? Filesize { get; set; }
        public int? Tbr { get; set; } // Total bitrate
        public int? Vbr { get; set; } // Video bitrate
        public int? Abr { get; set; } // Audio bitrate
        public bool HasVideo => !string.IsNullOrEmpty(VCodec) && VCodec != "none";
        public bool HasAudio => !string.IsNullOrEmpty(ACodec) && ACodec != "none";
        public bool IsCombined => HasVideo && HasAudio;
    }

    /// <summary>
    /// Extracted YouTube video information
    /// </summary>
    public class YouTubeVideoInfo
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Channel { get; set; }
        public string? Uploader { get; set; }
        public TimeSpan Duration { get; set; }
        public string? Thumbnail { get; set; }
        public List<YouTubeFormat> Formats { get; set; } = new();
        
        // Selected format URLs for playback
        public string? VideoUrl { get; set; }
        public string? AudioUrl { get; set; }
        public bool IsAdaptive => VideoUrl != null && AudioUrl != null && VideoUrl != AudioUrl;
        public YouTubeFormat? SelectedFormat { get; set; }
    }

    /// <summary>
    /// Check if yt-dlp is installed and available
    /// </summary>
    public static bool IsYtDlpAvailable()
    {
        if (_ytDlpAvailable.HasValue)
            return _ytDlpAvailable.Value;

        lock (_lock)
        {
            if (_ytDlpAvailable.HasValue)
                return _ytDlpAvailable.Value;

            try
            {
                var psi = new ProcessStartInfo("yt-dlp", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    _ytDlpAvailable = false;
                    return false;
                }

                proc.WaitForExit(5000);
                _ytDlpAvailable = proc.ExitCode == 0;
                
                if (_ytDlpAvailable.Value)
                {
                    var version = proc.StandardOutput.ReadToEnd().Trim();
                    Console.WriteLine($"[YOUTUBE] yt-dlp available: {version}");
                }
                
                return _ytDlpAvailable.Value;
            }
            catch
            {
                _ytDlpAvailable = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Check if URL is a supported YouTube URL
    /// </summary>
    public static bool IsYouTubeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        // Match youtube.com, youtu.be, youtube-nocookie.com, etc.
        var patterns = new[]
        {
            @"(youtube\.com|youtu\.be|youtube-nocookie\.com)",
            @"^https?://(www\.)?(youtube\.com|youtu\.be)/",
        };

        return patterns.Any(p => Regex.IsMatch(url, p, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Extract video ID from YouTube URL
    /// </summary>
    public static string? ExtractVideoId(string url)
    {
        // Patterns for YouTube video IDs
        var patterns = new[]
        {
            @"(?:v=|/v/|youtu\.be/|/embed/|/shorts/)([a-zA-Z0-9_-]{11})",
            @"^([a-zA-Z0-9_-]{11})$" // Just the ID
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(url, pattern);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Get video information and available formats
    /// </summary>
    public async Task<YouTubeVideoInfo?> GetVideoInfoAsync(string url)
    {
        if (!IsYtDlpAvailable())
        {
            Console.WriteLine("[YOUTUBE] yt-dlp not available");
            return null;
        }

        try
        {
            // Use yt-dlp to get JSON info
            var psi = new ProcessStartInfo("yt-dlp")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            // Arguments: dump JSON info without downloading
            psi.ArgumentList.Add("-j");           // Output JSON
            psi.ArgumentList.Add("--no-playlist"); // Single video only
            psi.ArgumentList.Add("--no-warnings");
            AddYoutubeWorkaroundArgs(psi);
            psi.ArgumentList.Add(url);

            Console.WriteLine($"[YOUTUBE] Getting video info: {url}");

            using var proc = Process.Start(psi);
            if (proc == null)
                return null;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            // Timeout after 30 seconds
            var timeoutTask = Task.Delay(30000);
            var completedTask = await Task.WhenAny(proc.WaitForExitAsync(), timeoutTask);

            if (completedTask == timeoutTask)
            {
                Console.WriteLine("[YOUTUBE] yt-dlp timed out");
                try { proc.Kill(); } catch { }
                return null;
            }

            var output = await outputTask;
            var error = await errorTask;

            if (proc.ExitCode != 0)
            {
                Console.WriteLine($"[YOUTUBE] yt-dlp failed: {error}");
                return null;
            }

            // Parse JSON output
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            var info = new YouTubeVideoInfo
            {
                Id = root.GetProperty("id").GetString() ?? "",
                Title = root.GetProperty("title").GetString() ?? "Unknown",
                Channel = root.TryGetProperty("channel", out var ch) ? ch.GetString() : null,
                Uploader = root.TryGetProperty("uploader", out var up) ? up.GetString() : null,
                Duration = TimeSpan.FromSeconds(root.TryGetProperty("duration", out var dur) ? dur.GetDouble() : 0),
                Thumbnail = root.TryGetProperty("thumbnail", out var thumb) ? thumb.GetString() : null
            };

            // Parse formats
            if (root.TryGetProperty("formats", out var formats))
            {
                foreach (var fmt in formats.EnumerateArray())
                {
                    var format = new YouTubeFormat
                    {
                        FormatId = fmt.TryGetProperty("format_id", out var fid) ? fid.GetString() ?? "" : "",
                        Extension = fmt.TryGetProperty("ext", out var ext) ? ext.GetString() ?? "" : "",
                        Width = fmt.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : null,
                        Height = fmt.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : null,
                        Fps = fmt.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Number ? fps.GetDouble() : null,
                        VCodec = fmt.TryGetProperty("vcodec", out var vc) ? vc.GetString() : null,
                        ACodec = fmt.TryGetProperty("acodec", out var ac) ? ac.GetString() : null,
                        Filesize = fmt.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt64() : null,
                        Tbr = fmt.TryGetProperty("tbr", out var tbr) && tbr.ValueKind == JsonValueKind.Number ? (int)tbr.GetDouble() : null,
                        Vbr = fmt.TryGetProperty("vbr", out var vbr) && vbr.ValueKind == JsonValueKind.Number ? (int)vbr.GetDouble() : null,
                        Abr = fmt.TryGetProperty("abr", out var abr) && abr.ValueKind == JsonValueKind.Number ? (int)abr.GetDouble() : null
                    };
                    info.Formats.Add(format);
                }
            }

            Console.WriteLine($"[YOUTUBE] Found {info.Formats.Count} formats for: {info.Title} ({info.Duration:mm\\:ss})");
            return info;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YOUTUBE] Error getting video info: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Select best format for the given canvas dimensions
    /// Prefers combined streams (non-adaptive) when available, falls back to adaptive
    /// </summary>
    public YouTubeFormat? SelectBestFormat(YouTubeVideoInfo info, int canvasWidth, int canvasHeight)
    {
        if (info.Formats.Count == 0)
            return null;

        // Target: slightly larger than canvas to allow for quality
        // But not too large to waste bandwidth
        int targetHeight = Math.Max(canvasHeight, 144); // Minimum 144p
        int maxHeight = Math.Min(canvasHeight * 3, 720); // Cap at 720p or 3x canvas
        
        Console.WriteLine($"[YOUTUBE] Selecting format for {canvasWidth}x{canvasHeight} canvas (target height: {targetHeight}-{maxHeight}p)");

        // First, try to find a combined stream (non-adaptive) that fits
        var combinedFormats = info.Formats
            .Where(f => f.IsCombined && f.Height.HasValue)
            .Where(f => f.Height >= targetHeight && f.Height <= maxHeight)
            .OrderBy(f => f.Height) // Prefer smaller to save bandwidth
            .ThenByDescending(f => f.Tbr ?? 0) // Then prefer higher bitrate
            .ToList();

        if (combinedFormats.Count > 0)
        {
            var best = combinedFormats.First();
            Console.WriteLine($"[YOUTUBE] Selected combined format: {best.FormatId} ({best.Width}x{best.Height} @ {best.Tbr}kbps)");
            return best;
        }

        // No suitable combined format, look for video-only streams
        var videoFormats = info.Formats
            .Where(f => f.HasVideo && !f.HasAudio && f.Height.HasValue)
            .Where(f => f.Height >= targetHeight && f.Height <= maxHeight)
            .OrderBy(f => f.Height)
            .ThenByDescending(f => f.Vbr ?? f.Tbr ?? 0)
            .ToList();

        if (videoFormats.Count > 0)
        {
            var best = videoFormats.First();
            Console.WriteLine($"[YOUTUBE] Selected adaptive video format: {best.FormatId} ({best.Width}x{best.Height} @ {best.Vbr ?? best.Tbr}kbps)");
            return best;
        }

        // Fall back to any video format
        var anyVideo = info.Formats
            .Where(f => f.HasVideo && f.Height.HasValue)
            .OrderBy(f => Math.Abs((f.Height ?? 0) - targetHeight))
            .FirstOrDefault();

        if (anyVideo != null)
        {
            Console.WriteLine($"[YOUTUBE] Selected fallback format: {anyVideo.FormatId} ({anyVideo.Width}x{anyVideo.Height})");
        }

        return anyVideo;
    }

    /// <summary>
    /// Select best audio format for adaptive streams
    /// </summary>
    public YouTubeFormat? SelectBestAudioFormat(YouTubeVideoInfo info)
    {
        // Prefer audio-only streams with good quality but reasonable bitrate
        var audioFormats = info.Formats
            .Where(f => f.HasAudio && !f.HasVideo)
            .Where(f => f.ACodec != null && !f.ACodec.Contains("opus")) // Prefer AAC/M4A over Opus for FFmpeg compatibility
            .OrderByDescending(f => f.Abr ?? f.Tbr ?? 0)
            .ToList();

        // If no AAC/M4A, try Opus
        if (audioFormats.Count == 0)
        {
            audioFormats = info.Formats
                .Where(f => f.HasAudio && !f.HasVideo)
                .OrderByDescending(f => f.Abr ?? f.Tbr ?? 0)
                .ToList();
        }

        var best = audioFormats.FirstOrDefault();
        if (best != null)
        {
            Console.WriteLine($"[YOUTUBE] Selected audio format: {best.FormatId} ({best.ACodec} @ {best.Abr ?? best.Tbr}kbps)");
        }

        return best;
    }

    /// <summary>
    /// Get direct playback URLs for video and audio
    /// </summary>
    public async Task<(string? videoUrl, string? audioUrl)?> GetPlaybackUrlsAsync(string youtubeUrl, int canvasWidth, int canvasHeight)
    {
        var info = await GetVideoInfoAsync(youtubeUrl);
        if (info == null)
            return null;

        var videoFormat = SelectBestFormat(info, canvasWidth, canvasHeight);
        if (videoFormat == null)
        {
            Console.WriteLine("[YOUTUBE] No suitable video format found");
            return null;
        }

        // Build format selector string for yt-dlp
        string formatSelector;
        YouTubeFormat? audioFormat = null;

        if (videoFormat.IsCombined)
        {
            // Combined stream - "/best" fallback in case the exact itag isn't directly downloadable.
            formatSelector = $"{videoFormat.FormatId}/best";
        }
        else
        {
            // Adaptive stream - need video + audio, with fallbacks to avoid "format not available".
            audioFormat = SelectBestAudioFormat(info);
            formatSelector = audioFormat == null
                ? $"{videoFormat.FormatId}+bestaudio/best"
                : $"{videoFormat.FormatId}+{audioFormat.FormatId}/{videoFormat.FormatId}+bestaudio/best";
        }

        // Get the actual URLs using yt-dlp -g
        try
        {
            var psi = new ProcessStartInfo("yt-dlp")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-g");                // Get URLs only
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--no-warnings");
            AddYoutubeWorkaroundArgs(psi);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(formatSelector);
            psi.ArgumentList.Add(youtubeUrl);

            Console.WriteLine($"[YOUTUBE] Getting playback URL for format: {formatSelector}");

            using var proc = Process.Start(psi);
            if (proc == null)
                return null;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            var timeoutTask = Task.Delay(15000);
            var completedTask = await Task.WhenAny(proc.WaitForExitAsync(), timeoutTask);

            if (completedTask == timeoutTask)
            {
                Console.WriteLine("[YOUTUBE] URL extraction timed out");
                try { proc.Kill(); } catch { }
                return null;
            }

            var output = await outputTask;
            var error = await errorTask;

            if (proc.ExitCode != 0)
            {
                Console.WriteLine($"[YOUTUBE] URL extraction failed: {error}");
                return null;
            }

            var urls = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (videoFormat.IsCombined)
            {
                // Single URL for combined stream
                if (urls.Length >= 1)
                {
                    Console.WriteLine("[YOUTUBE] Got combined stream URL");
                    return (urls[0], urls[0]); // Same URL for both
                }
            }
            else
            {
                // Two URLs for adaptive stream (video, then audio)
                if (urls.Length >= 2)
                {
                    Console.WriteLine("[YOUTUBE] Got adaptive stream URLs (video + audio)");
                    return (urls[0], urls[1]);
                }

                // The "/best" fallback may have resolved to a single combined stream.
                if (urls.Length == 1)
                {
                    Console.WriteLine("[YOUTUBE] Fell back to a single combined stream URL");
                    return (urls[0], urls[0]);
                }
            }

            Console.WriteLine($"[YOUTUBE] Unexpected URL count: {urls.Length}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YOUTUBE] Error getting URLs: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get a single best URL for FFmpeg (handles muxing of adaptive streams)
    /// Returns format string and URL(s) for FFmpeg command
    /// </summary>
    public async Task<YouTubePlaybackInfo?> GetPlaybackInfoAsync(string youtubeUrl, int canvasWidth, int canvasHeight)
    {
        var info = await GetVideoInfoAsync(youtubeUrl);
        if (info == null)
            return null;

        var videoFormat = SelectBestFormat(info, canvasWidth, canvasHeight);
        if (videoFormat == null)
            return null;

        var result = new YouTubePlaybackInfo
        {
            Title = info.Title,
            Channel = info.Channel ?? info.Uploader,
            Duration = info.Duration,
            Thumbnail = info.Thumbnail,
            VideoId = info.Id
        };

        // Build format selector with robust fallbacks. Requesting a single exact itag often fails with
        // "Requested format is not available" (the format may be SABR/DRM-gated or not directly downloadable);
        // the trailing "/best" guarantees yt-dlp picks something playable.
        string formatSelector;
        if (videoFormat.IsCombined)
        {
            formatSelector = $"{videoFormat.FormatId}/best";
            result.IsAdaptive = false;
        }
        else
        {
            var audioFormat = SelectBestAudioFormat(info);
            formatSelector = audioFormat == null
                ? $"{videoFormat.FormatId}+bestaudio/best"
                : $"{videoFormat.FormatId}+{audioFormat.FormatId}/{videoFormat.FormatId}+bestaudio/best";
            result.IsAdaptive = true;
        }

        result.Format = formatSelector;
        result.Width = videoFormat.Width ?? 0;
        result.Height = videoFormat.Height ?? 0;
        result.Fps = videoFormat.Fps ?? 30;

        // Get URLs
        try
        {
            var psi = new ProcessStartInfo("yt-dlp")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--no-warnings");
            AddYoutubeWorkaroundArgs(psi);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(formatSelector);
            psi.ArgumentList.Add(youtubeUrl);

            using var proc = Process.Start(psi);
            if (proc == null)
                return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
                return null;

            var urls = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (result.IsAdaptive && urls.Length >= 2)
            {
                result.VideoUrl = urls[0];
                result.AudioUrl = urls[1];
            }
            else if (urls.Length >= 1)
            {
                result.VideoUrl = urls[0];
                result.AudioUrl = urls[0]; // Same for combined
            }
            else
            {
                return null;
            }

            Console.WriteLine($"[YOUTUBE] Ready to play: {result.Title} ({result.Width}x{result.Height}, {(result.IsAdaptive ? "adaptive" : "combined")})");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YOUTUBE] Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get audio-only playback URL using yt-dlp with bestaudio format.
    /// Used for music songs where the user doesn't want album art on the display.
    /// </summary>
    public async Task<YouTubePlaybackInfo?> GetAudioOnlyPlaybackInfoAsync(string youtubeUrl)
    {
        var info = await GetVideoInfoAsync(youtubeUrl);
        if (info == null)
            return null;

        var result = new YouTubePlaybackInfo
        {
            Title = info.Title,
            Channel = info.Channel ?? info.Uploader,
            Duration = info.Duration,
            Thumbnail = info.Thumbnail,
            VideoId = info.Id,
            Format = "bestaudio",
            IsAdaptive = false
        };

        try
        {
            var psi = new ProcessStartInfo("yt-dlp")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--no-warnings");
            AddYoutubeWorkaroundArgs(psi);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("bestaudio");
            psi.ArgumentList.Add(youtubeUrl);

            Console.WriteLine($"[YOUTUBE] Getting audio-only URL for: {info.Title}");

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0) return null;

            var url = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(url)) return null;

            result.AudioUrl = url;
            result.VideoUrl = url; // Same URL for audio-only

            Console.WriteLine($"[YOUTUBE] Audio-only ready: {result.Title}");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YOUTUBE] Audio-only error: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Complete playback information for YouTube video
/// </summary>
public class YouTubePlaybackInfo
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Channel { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Thumbnail { get; set; }
    public string Format { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public bool IsAdaptive { get; set; }
    public string? VideoUrl { get; set; }
    public string? AudioUrl { get; set; }
}
