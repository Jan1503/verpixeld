namespace verpixeld.MediaPlayer;

/// <summary>
///     Safe upload destinations and credential-free log / persist strings.
/// </summary>
public static class MediaPath
{
    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            throw new ArgumentException("A file name is required.", nameof(fileName));
        return name;
    }

    /// <summary>
    ///     Combine <paramref name="root"/> with a client-supplied name. The result always stays
    ///     under <paramref name="root"/>; the directory is created.
    /// </summary>
    public static string SafeUploadPath(string root, string? fileName)
    {
        var name = SanitizeFileName(fileName);
        Directory.CreateDirectory(root);
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, name));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Upload path escaped the media root.");
        return candidate;
    }

    /// <summary>
    ///     Strip userinfo from smb/http/ftp URLs so logs and favorites never keep a password.
    /// </summary>
    public static string? Redact(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var at = url.IndexOf('@');
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (at <= 0 || scheme < 0 || at < scheme) return url;
        return url[..(scheme + 3)] + url[(at + 1)..];
    }
}
