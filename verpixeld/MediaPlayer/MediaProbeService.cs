using System.Diagnostics;
using System.Globalization;

namespace verpixeld.MediaPlayer;

/// <summary>
///     Static service for probing media files using ffprobe/ffmpeg.
///     Handles metadata extraction, thumbnail generation, and video info probing.
/// </summary>
public static class MediaProbeService
{
    /// <summary>
    ///     Check if a path is a URL (smb, http, https, ftp, rtsp, rtmp).
    /// </summary>
    public static bool IsUrl(string path)
    {
        return path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Extract metadata (ID3 tags, container metadata) from a media file using ffprobe.
    /// </summary>
    public static async Task<MediaMetadata?> ExtractMetadataAsync(string mediaPath)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("quiet");
            psi.ArgumentList.Add("-print_format");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("-show_format");
            psi.ArgumentList.Add(mediaPath);

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;

            var metadata = new MediaMetadata();

            var tagsStart = output.IndexOf("\"tags\"", StringComparison.OrdinalIgnoreCase);
            if (tagsStart < 0) return metadata;

            var tagsSection = output.Substring(tagsStart);
            var tagsEnd = tagsSection.IndexOf('}');
            if (tagsEnd > 0)
            {
                var braceCount = 0;
                for (var i = tagsSection.IndexOf('{'); i < tagsSection.Length; i++)
                {
                    if (tagsSection[i] == '{') braceCount++;
                    if (tagsSection[i] == '}') braceCount--;
                    if (braceCount == 0)
                    {
                        tagsEnd = i + 1;
                        break;
                    }
                }

                tagsSection = tagsSection.Substring(0, tagsEnd);
            }

            metadata.Title = ExtractJsonValue(tagsSection, "title");
            metadata.Artist = ExtractJsonValue(tagsSection, "artist") ?? ExtractJsonValue(tagsSection, "ARTIST");
            metadata.Album = ExtractJsonValue(tagsSection, "album") ?? ExtractJsonValue(tagsSection, "ALBUM");
            metadata.AlbumArtist = ExtractJsonValue(tagsSection, "album_artist") ??
                                   ExtractJsonValue(tagsSection, "ALBUMARTIST");
            metadata.Genre = ExtractJsonValue(tagsSection, "genre") ?? ExtractJsonValue(tagsSection, "GENRE");
            metadata.Year = ExtractJsonValue(tagsSection, "date") ??
                            ExtractJsonValue(tagsSection, "year") ?? ExtractJsonValue(tagsSection, "DATE");
            metadata.Composer = ExtractJsonValue(tagsSection, "composer") ?? ExtractJsonValue(tagsSection, "COMPOSER");

            var trackStr = ExtractJsonValue(tagsSection, "track") ?? ExtractJsonValue(tagsSection, "TRACK");
            if (!string.IsNullOrEmpty(trackStr))
            {
                var trackParts = trackStr.Split('/');
                if (int.TryParse(trackParts[0], out var trackNum)) metadata.TrackNumber = trackNum;
            }

            if (metadata.HasMetadata) Console.WriteLine($"[MEDIA] Metadata: {metadata.DisplayString(null)}");

            return metadata;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEDIA] Failed to extract metadata: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Extract a string value from JSON-like text.
    /// </summary>
    private static string? ExtractJsonValue(string json, string key)
    {
        var patterns = new[] { $"\"{key}\":", $"\"{key.ToUpperInvariant()}\":", $"\"{key.ToLowerInvariant()}\":" };

        foreach (var pattern in patterns)
        {
            var keyIndex = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0) continue;

            var valueStart = keyIndex + pattern.Length;

            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            if (valueStart >= json.Length) continue;

            if (json[valueStart] == '"')
            {
                valueStart++;
                var valueEnd = json.IndexOf('"', valueStart);
                if (valueEnd > valueStart)
                {
                    var value = json.Substring(valueStart, valueEnd - valueStart);
                    value = value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n");
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Extract a thumbnail from a video file using ffmpeg.
    ///     Returns a base64-encoded JPEG string, or null if extraction fails.
    /// </summary>
    public static async Task<string?> ExtractThumbnailAsync(string videoPath, int width = 120, int height = 80,
        int timeSeconds = 5)
    {
        try
        {
            var isUrl = IsUrl(videoPath);

            if (!isUrl && !File.Exists(videoPath))
            {
                Console.WriteLine($"[THUMBNAIL] File not found: {videoPath}");
                return null;
            }

            var args = isUrl
                ? $"-ss {timeSeconds} -i \"{videoPath}\" -vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" -vframes 1 -f mjpeg -"
                : $"-ss {timeSeconds} -i \"{videoPath}\" -vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" -vframes 1 -f mjpeg -";

            var psi = new ProcessStartInfo("ffmpeg", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.WriteLine("[THUMBNAIL] Failed to start ffmpeg");
                return null;
            }

            using var ms = new MemoryStream();
            await proc.StandardOutput.BaseStream.CopyToAsync(ms);

            _ = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(); }
                catch { }
                Console.WriteLine("[THUMBNAIL] ffmpeg timed out");
                return null;
            }

            if (ms.Length < 100)
            {
                if (timeSeconds > 1)
                    return await ExtractThumbnailAsync(videoPath, width, height, 1);
                Console.WriteLine($"[THUMBNAIL] Extracted image too small: {ms.Length} bytes");
                return null;
            }

            var base64 = Convert.ToBase64String(ms.ToArray());
            Console.WriteLine($"[THUMBNAIL] Extracted {ms.Length} bytes from {Path.GetFileName(videoPath)}");
            return $"data:image/jpeg;base64,{base64}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[THUMBNAIL] Failed to extract: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Get video information using ffprobe.
    ///     Works for both local files and network URLs (smb://).
    /// </summary>
    public static async Task<VideoInfo?> GetVideoInfoAsync(string videoSource)
    {
        var isUrl = IsUrl(videoSource);

        if (!isUrl && !File.Exists(videoSource))
        {
            Console.WriteLine($"[VIDEO] Local file not found: {videoSource}");
            return null;
        }

        try
        {
            // Stream duration is often N/A on MKV/HEVC; format.duration has the real length.
            // Local files used to omit format=duration — GUI then showed 0:00 and seek was disabled.
            var ffprobeArgs = isUrl
                ? $"-v error -timeout 10000000 -select_streams v:0 -show_entries stream=width,height,r_frame_rate,duration -show_entries format=duration -of csv=p=0 \"{videoSource}\""
                : $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate,duration -show_entries format=duration -of csv=p=0 \"{videoSource}\"";

            Console.WriteLine($"[VIDEO] Probing: {(isUrl ? "network stream" : "local file")}");

            var psi = new ProcessStartInfo("ffprobe", ffprobeArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.WriteLine("[VIDEO] Failed to start ffprobe");
                return null;
            }

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            var timeoutTask = Task.Delay(isUrl ? 30000 : 10000);
            var completedTask = await Task.WhenAny(proc.WaitForExitAsync(), timeoutTask);

            if (completedTask == timeoutTask)
            {
                Console.WriteLine("[VIDEO] ffprobe timed out");
                try { proc.Kill(); }
                catch { }
                return null;
            }

            var output = await outputTask;
            var error = await errorTask;

            if (proc.ExitCode != 0)
            {
                Console.WriteLine($"[VIDEO] ffprobe failed (exit {proc.ExitCode}): {error}");
                return null;
            }

            var lines = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                Console.WriteLine("[VIDEO] ffprobe returned no data");
                return null;
            }

            var fileName = isUrl
                ? Uri.TryCreate(videoSource, UriKind.Absolute, out var uri)
                    ? Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath))
                    : Path.GetFileName(videoSource)
                : Path.GetFileName(videoSource);

            var info = ParseProbeCsv(output, videoSource, fileName);
            if (info == null)
                Console.WriteLine($"[VIDEO] Unexpected ffprobe output: {output}");
            else if (info.Width == 0 && info.Height == 0)
                Console.WriteLine($"[AUDIO] Probe result: audio-only, duration: {info.Duration.TotalSeconds:F1}s");
            else
                Console.WriteLine(
                    $"[VIDEO] Probe result: {info.Width}x{info.Height} @ {info.Fps:F2}fps, duration: {info.Duration.TotalSeconds:F1}s");

            return info;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIDEO] Error getting video info: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Parse ffprobe CSV from stream=width,height,r_frame_rate,duration plus optional format=duration on the next line.
    /// </summary>
    internal static VideoInfo? ParseProbeCsv(string output, string videoSource, string fileName)
    {
        var lines = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(',');

        if (parts.Length == 1 && TryParseSeconds(parts[0], out var audioOnlyDuration))
        {
            return new VideoInfo
            {
                Path = videoSource, FileName = fileName,
                Width = 0, Height = 0, Fps = 0,
                Duration = TimeSpan.FromSeconds(audioOnlyDuration)
            };
        }

        if (parts.Length == 1 && lines.Length > 1 && TryParseSeconds(lines[1], out var formatOnlyDuration))
        {
            return new VideoInfo
            {
                Path = videoSource, FileName = fileName,
                Width = 0, Height = 0, Fps = 0,
                Duration = TimeSpan.FromSeconds(formatOnlyDuration)
            };
        }

        if (parts.Length < 3)
        {
            if (TryParseSeconds(output, out var fallbackDuration))
            {
                return new VideoInfo
                {
                    Path = videoSource, FileName = fileName,
                    Width = 0, Height = 0, Fps = 0,
                    Duration = TimeSpan.FromSeconds(fallbackDuration)
                };
            }

            return null;
        }

        int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width);
        int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height);

        double fps = 30;
        if (parts.Length > 2)
        {
            var fpsParts = parts[2].Trim().Split('/');
            if (fpsParts.Length == 2 &&
                double.TryParse(fpsParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
                double.TryParse(fpsParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
                den > 0)
                fps = num / den;
            else
                double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out fps);
        }

        double duration = 0;
        if (parts.Length > 3) TryParseSeconds(parts[3], out duration);
        if (duration <= 0)
        {
            for (var i = 1; i < lines.Length; i++)
            {
                if (TryParseSeconds(lines[i], out duration) && duration > 0)
                    break;
            }
        }

        return new VideoInfo
        {
            Path = videoSource, FileName = fileName,
            Width = width, Height = height,
            Fps = fps > 0 ? fps : 30,
            Duration = TimeSpan.FromSeconds(duration)
        };
    }

    internal static bool TryParseSeconds(string? value, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return false;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds > 0)
            return true;
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts) && ts > TimeSpan.Zero)
        {
            seconds = ts.TotalSeconds;
            return true;
        }

        seconds = 0;
        return false;
    }
}
