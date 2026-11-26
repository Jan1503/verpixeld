using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace verpixeld.WebApi;

/// <summary>
///     Cache busting for the web UI without hand-maintained <c>?v=</c> numbers: every local script and
///     stylesheet referenced from index.html gets <c>?v=</c> plus the first characters of its content hash.
///     A changed file gets a new URL, so browsers fetch it at once; an unchanged one may be cached for good
///     (see <see cref="IsVersioned"/>). Hashes are cached per file and recomputed when the file changes.
/// </summary>
public static partial class AssetVersioning
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, string Hash)> Hashes = new();

    [GeneratedRegex("""(?<attr>\b(?:src|href)=")(?<path>/[^"?#]+\.(?:js|css))(?:\?[^"#]*)?(?<end>")""")]
    private static partial Regex AssetReference();

    /// <summary>Rewrites the asset references in <paramref name="html"/> against files under <paramref name="webRoot"/>.</summary>
    public static string Apply(string html, string webRoot) =>
        AssetReference().Replace(html, m =>
        {
            var path = m.Groups["path"].Value;
            var hash = HashOf(Path.Combine(webRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            return hash == null
                ? m.Value // unknown file: leave the reference as written
                : $"{m.Groups["attr"].Value}{path}?v={hash}{m.Groups["end"].Value}";
        });

    /// <summary>True for a request whose URL carries a content version (<c>?v=</c>): safe to cache for good.</summary>
    public static bool IsVersioned(HttpContext context) => context.Request.Query.ContainsKey("v");

    private static string? HashOf(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists) return null;
        if (Hashes.TryGetValue(file, out var cached) && cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Hash;
        using var stream = info.OpenRead();
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream))[..10];
        Hashes[file] = (info.LastWriteTimeUtc, info.Length, hash);
        return hash;
    }
}
