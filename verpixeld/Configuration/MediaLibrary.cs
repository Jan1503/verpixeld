namespace verpixeld.Configuration;

/// <summary>
///     Local media on disk. On the Pi this is Media/Videos (and Music/Audio). In Docker the
///     whole /app/Media mount is the library so a NAS share can be bound there without a Videos/ subfolder.
/// </summary>
public static class MediaLibrary
{
    public const int MaxFiles = 4000;

    public static readonly string[] VideoExtensions =
        [".mp4", ".webm", ".mkv", ".avi", ".mov", ".flv", ".m4v", ".ts", ".m2ts"];

    public static readonly string[] AudioExtensions =
        [".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".wma", ".opus"];

    public static readonly string[] ModExtensions =
        [".mod", ".xm", ".s3m", ".it", ".stm", ".mtm"];

    public static string VideoRoot => AppPaths.RunningInContainer() ? AppPaths.MediaDir : AppPaths.VideosDir;
    public static string AudioRoot => AppPaths.RunningInContainer() ? AppPaths.MediaDir : AppPaths.AudioDir;
    public static string ModRoot => AppPaths.RunningInContainer() ? AppPaths.MediaDir : AppPaths.MusicDir;

    /// <summary>Folder browser root. Always the Media directory (Docker mount or Pi Media/).</summary>
    public static string BrowseRoot => AppPaths.MediaDir;

    public const int MaxBrowseEntries = 2000;

    public static string? ResolveVideo(string? relative) =>
        ResolveMediaFile(relative, VideoRoot, AppPaths.VideosDir, AppPaths.MediaDir);

    public static string? ResolveAudio(string? relative) =>
        ResolveMediaFile(relative, AudioRoot, AppPaths.AudioDir, AppPaths.MediaDir);

    /// <summary>
    ///     Resolves a picker-relative path against the library roots. In Docker the NAS is mounted
    ///     at Media/ (movies live in Media/Movies/…, not Media/Videos/Movies/…).
    /// </summary>
    public static string? ResolveMediaFile(string? relative, params string[] roots)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var rel = Uri.UnescapeDataString(relative).Replace('\\', '/').Trim();
        if (Path.IsPathRooted(relative) && File.Exists(relative))
            return Path.GetFullPath(relative);

        foreach (var root in roots)
        {
            var hit = Resolve(root, rel);
            if (hit != null) return hit;
        }

        return null;
    }

    /// <summary>
    ///     Library-relative path of a file ("Movies/Film.mkv"), so favorites and history find it again
    ///     in Docker (Media/…) and on the Pi (Media/Videos/…). Null when the file is outside the library.
    /// </summary>
    public static string? ToRelative(string? fullPath, bool video)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathRooted(fullPath)) return null;
        var full = Path.GetFullPath(fullPath);
        var roots = video
            ? new[] { VideoRoot, AppPaths.VideosDir, AppPaths.MediaDir }
            : new[] { AudioRoot, AppPaths.AudioDir, AppPaths.MediaDir };
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(r, full).Replace('\\', '/');
        }
        return null;
    }

    /// <summary>
    ///     A stored local path: library-relative, or (older entries) only the file name, which is
    ///     then looked for in the library.
    /// </summary>
    public static string? FindLocal(string? stored, bool video)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var hit = video ? ResolveVideo(stored) : ResolveAudio(stored);
        if (hit != null) return hit;
        var name = Path.GetFileName(stored.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) return null;
        var root = video ? VideoRoot : AudioRoot;
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, name, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 8,
                MatchCasing = MatchCasing.CaseInsensitive
            }).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static string? ParentPath(string? current)
    {
        if (string.IsNullOrEmpty(current)) return null;
        var path = current.Replace('\\', '/').Trim('/');
        var last = path.LastIndexOf('/');
        if (last < 0) return "";
        return path[..last];
    }

    public static string? ResolveDirectory(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        var rel = string.IsNullOrWhiteSpace(relative)
            ? ""
            : Uri.UnescapeDataString(relative).Replace('\\', '/').Trim('/');
        if (rel.Contains("..", StringComparison.Ordinal)) return null;

        var rootFull = Path.GetFullPath(root);
        var combined = string.IsNullOrEmpty(rel)
            ? rootFull
            : Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!combined.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return Directory.Exists(combined) ? combined : null;
    }

    /// <summary>
    ///     One-folder listing for the Local Media browser (same shape as the SMB network browser).
    /// </summary>
    public static MediaBrowseResult Browse(string root, string? relativePath)
    {
        var rel = string.IsNullOrWhiteSpace(relativePath)
            ? ""
            : Uri.UnescapeDataString(relativePath).Replace('\\', '/').Trim('/');
        var result = new MediaBrowseResult { CurrentPath = rel, ParentPath = ParentPath(rel) };

        var dir = ResolveDirectory(root, rel);
        if (dir == null)
        {
            result.Error = Directory.Exists(root)
                ? "Folder not found"
                : $"Media folder missing: {root}";
            return result;
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
                result.Directories.Add(new MediaBrowseEntry
                {
                    Name = name,
                    Path = string.IsNullOrEmpty(rel) ? name : $"{rel}/{name}"
                });
                if (result.Directories.Count >= MaxBrowseEntries) break;
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        try
        {
            var videos = new HashSet<string>(VideoExtensions, StringComparer.OrdinalIgnoreCase);
            var audio = new HashSet<string>(AudioExtensions, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
                var ext = Path.GetExtension(name);
                var path = string.IsNullOrEmpty(rel) ? name : $"{rel}/{name}";
                if (videos.Contains(ext))
                    result.Videos.Add(new MediaBrowseEntry { Name = name, Path = path });
                else if (audio.Contains(ext))
                    result.AudioFiles.Add(new MediaBrowseEntry { Name = name, Path = path });
                if (result.Videos.Count + result.AudioFiles.Count >= MaxBrowseEntries) break;
            }
        }
        catch (Exception ex)
        {
            result.Error ??= ex.Message;
        }

        result.Directories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        result.Videos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        result.AudioFiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // Walking a NAS library takes seconds; the media status (asked every second) lists it three
    // times. Listings are kept for a minute and refreshed in the background.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, IReadOnlyList<string> List)> ListCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ListRefreshing = new();

    /// <summary>Forget cached listings (after an upload).</summary>
    public static void InvalidateListings() => ListCache.Clear();

    public static IReadOnlyList<string> ListRelative(string root, string[] extensions)
    {
        var key = root + "|" + string.Join(",", extensions);
        if (ListCache.TryGetValue(key, out var hit))
        {
            if (DateTime.UtcNow - hit.At > TimeSpan.FromSeconds(60) && ListRefreshing.TryAdd(key, 0))
                _ = Task.Run(() =>
                {
                    try { ListCache[key] = (DateTime.UtcNow, ListRelativeNow(root, extensions)); }
                    catch { /* keep the old list */ }
                    finally { ListRefreshing.TryRemove(key, out _); }
                });
            return hit.List;
        }
        var list = ListRelativeNow(root, extensions);
        ListCache[key] = (DateTime.UtcNow, list);
        return list;
    }

    private static IReadOnlyList<string> ListRelativeNow(string root, string[] extensions)
    {
        if (!Directory.Exists(root)) return [];
        var rootFull = Path.GetFullPath(root);
        var ext = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var file in WalkFiles(rootFull))
        {
            if (!ext.Contains(Path.GetExtension(file))) continue;
            result.Add(Path.GetRelativePath(rootFull, file).Replace('\\', '/'));
            if (result.Count >= MaxFiles) break;
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public static string? Resolve(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(root))
            return null;
        if (!Directory.Exists(root)) return null;

        var rel = Uri.UnescapeDataString(relative).Replace('\\', '/').TrimStart('/');
        if (rel.Contains("..", StringComparison.Ordinal)) return null;

        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!combined.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(combined) ? combined : null;
    }

    private static IEnumerable<string> WalkFiles(string dir)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir); }
        catch { yield break; }

        foreach (var file in files)
            yield return file;

        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir); }
        catch { yield break; }

        foreach (var sub in subs)
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.')) continue;
            foreach (var file in WalkFiles(sub))
                yield return file;
        }
    }
}

public sealed class MediaBrowseResult
{
    public string CurrentPath { get; set; } = "";
    public string? ParentPath { get; set; }
    public string? Error { get; set; }
    public List<MediaBrowseEntry> Directories { get; } = [];
    public List<MediaBrowseEntry> Videos { get; } = [];
    public List<MediaBrowseEntry> AudioFiles { get; } = [];
}

public sealed class MediaBrowseEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}
