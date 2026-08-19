namespace verpixeld.Configuration;

/// <summary>
///     General-purpose file I/O utilities.
/// </summary>
public static class FileHelper
{
    /// <summary>
    ///     Write text to a file atomically (write to temp, then rename).
    ///     Prevents corruption if the process crashes mid-write.
    /// </summary>
    public static void AtomicWriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    ///     Write bytes to a file atomically (write to temp, then rename).
    /// </summary>
    public static void AtomicWriteAllBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }
}
