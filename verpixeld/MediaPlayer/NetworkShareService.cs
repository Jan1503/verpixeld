using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using verpixeld.Configuration;
using verpixeld.Services;

namespace verpixeld.MediaPlayer;

/// <summary>
///     Network share configuration for SMB/CIFS access
/// </summary>
public class NetworkShare
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string SharePath { get; set; } = "";
    public string? Domain { get; set; }
    public string? Username { get; set; }
    public string? EncryptedPassword { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Build SMB URL for FFmpeg (smb://[domain;]user:pass@server/share/path)
    /// </summary>
    public string BuildSmbUrl(string relativePath, string? decryptedPassword = null)
    {
        var sb = new StringBuilder("smb://");

        // Add credentials if present
        if (!string.IsNullOrEmpty(Username))
        {
            if (!string.IsNullOrEmpty(Domain))
            {
                sb.Append(HttpUtility.UrlEncode(Domain));
                sb.Append(';');
            }

            sb.Append(HttpUtility.UrlEncode(Username));

            if (!string.IsNullOrEmpty(decryptedPassword))
            {
                sb.Append(':');
                sb.Append(HttpUtility.UrlEncode(decryptedPassword));
            }

            sb.Append('@');
        }

        // Server and share path
        sb.Append(Server);
        sb.Append('/');
        sb.Append(SharePath.TrimStart('/'));

        // Relative path to file (URL-encode each path component)
        if (!string.IsNullOrEmpty(relativePath))
        {
            if (!SharePath.EndsWith('/') && !relativePath.StartsWith('/')) sb.Append('/');
            // Encode each path component separately to preserve slashes
            var encodedPath = string.Join("/",
                relativePath.TrimStart('/').Split('/').Select(part => Uri.EscapeDataString(part)));
            sb.Append(encodedPath);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Build a display-safe URL (no password)
    /// </summary>
    public string GetDisplayUrl()
    {
        return BuildSmbUrl("");
    }
}

/// <summary>
///     Cached browse result with timestamp
/// </summary>
public class CachedBrowseResult
{
    public BrowseResult Result { get; set; } = new();
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    public bool IsExpired(TimeSpan maxAge)
    {
        return DateTime.UtcNow - CachedAt > maxAge;
    }
}

/// <summary>
///     Service for managing SMB network shares with encrypted credential storage
/// </summary>
public class NetworkShareService
{
    // Directory cache: key = "shareId:path"
    private readonly Dictionary<string, CachedBrowseResult> _browseCache = new();
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);
    private readonly object _cacheLock = new();
    private readonly string _configPath;
    private readonly byte[] _encryptionKey;
    private List<NetworkShare> _shares = new();

    public NetworkShareService()
    {
        _configPath = AppPaths.NetworkSharesConfig;

        // Generate or load encryption key (machine-specific)
        var keyPath = AppPaths.ShareEncryptionKey;
        if (File.Exists(keyPath))
        {
            _encryptionKey = File.ReadAllBytes(keyPath);
        }
        else
        {
            _encryptionKey = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, _encryptionKey);
            // Restrict permissions on Linux
            if (OperatingSystem.IsLinux())
                try
                {
                    File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                }
        }

        LoadShares();
    }

    public IReadOnlyList<NetworkShare> Shares => _shares.AsReadOnly();

    /// <summary>
    ///     Add a new network share
    /// </summary>
    public NetworkShare AddShare(string name, string server, string sharePath,
        string? domain = null, string? username = null, string? password = null)
    {
        var share = new NetworkShare
        {
            Name = name,
            Server = server,
            SharePath = sharePath,
            Domain = domain,
            Username = username,
            EncryptedPassword = password != null ? EncryptPassword(password) : null,
            IsDefault = _shares.Count == 0
        };

        _shares.Add(share);
        SaveShares();

        Console.WriteLine($"[NETWORK] Added share: {share.Name} (smb://{share.Server}/{share.SharePath})");
        return share;
    }

    /// <summary>
    ///     Update an existing share
    /// </summary>
    public bool UpdateShare(string id, string? name = null, string? server = null,
        string? sharePath = null, string? domain = null, string? username = null,
        string? password = null, bool? isDefault = null)
    {
        var share = _shares.FirstOrDefault(s => s.Id == id);
        if (share == null) return false;

        if (name != null) share.Name = name;
        if (server != null) share.Server = server;
        if (sharePath != null) share.SharePath = sharePath;
        if (domain != null) share.Domain = domain == "" ? null : domain;
        if (username != null) share.Username = username == "" ? null : username;
        if (password != null) share.EncryptedPassword = password == "" ? null : EncryptPassword(password);

        if (isDefault == true)
        {
            foreach (var s in _shares) s.IsDefault = false;
            share.IsDefault = true;
        }

        SaveShares();
        Console.WriteLine($"[NETWORK] Updated share: {share.Name}");
        return true;
    }

    /// <summary>
    ///     Remove a share
    /// </summary>
    public bool RemoveShare(string id)
    {
        var share = _shares.FirstOrDefault(s => s.Id == id);
        if (share == null) return false;

        _shares.Remove(share);
        SaveShares();

        Console.WriteLine($"[NETWORK] Removed share: {share.Name}");
        return true;
    }

    /// <summary>
    ///     Get a share by ID
    /// </summary>
    public NetworkShare? GetShare(string id)
    {
        return _shares.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    ///     Get the default share
    /// </summary>
    public NetworkShare? GetDefaultShare()
    {
        return _shares.FirstOrDefault(s => s.IsDefault) ?? _shares.FirstOrDefault();
    }

    /// <summary>
    ///     Build full SMB URL for a file on a share
    /// </summary>
    public string? BuildFileUrl(string shareId, string relativePath)
    {
        var share = GetShare(shareId);
        if (share == null) return null;

        var password = share.EncryptedPassword != null ? DecryptPassword(share.EncryptedPassword) : null;
        return share.BuildSmbUrl(relativePath, password);
    }

    /// <summary>
    ///     Test connection to a share using smbclient
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(string shareId)
    {
        var share = GetShare(shareId);
        if (share == null) return (false, "Share not found");

        try
        {
            var authArgs = BuildSmbClientAuthArgs(share);
            var uncPath = $"//{share.Server}/{share.SharePath}";

            var psi = new ProcessStartInfo("smbclient");
            psi.ArgumentList.Add(uncPath);
            foreach (var arg in authArgs) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("ls");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start smbclient");

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();

            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0) return (true, "Connection successful");

            return (false, $"Connection failed: {error}");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Browse a directory on a share
    /// </summary>
    public async Task<BrowseResult> BrowseDirectoryAsync(string shareId, string subPath = "", bool forceRefresh = false)
    {
        var share = GetShare(shareId);
        if (share == null) return new BrowseResult { Path = subPath };

        var cacheKey = $"{shareId}:{subPath}";

        // Check cache first (unless force refresh)
        if (!forceRefresh)
        {
            lock (_cacheLock)
            {
                if (_browseCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired(_cacheExpiration))
                {
                    Console.WriteLine(
                        $"[NETWORK] Cache hit: {subPath} (cached {(int)(DateTime.UtcNow - cached.CachedAt).TotalSeconds}s ago)");
                    return cached.Result with { FromCache = true };
                }
            }
        }
        else
        {
            Console.WriteLine($"[NETWORK] Force refresh: {subPath}");
        }

        var result = await BrowseSmbDirectoryAsync(share, subPath);

        // Cache the result
        lock (_cacheLock)
        {
            _browseCache[cacheKey] = new CachedBrowseResult { Result = result };
        }

        return result;
    }

    /// <summary>
    ///     Clear directory cache
    /// </summary>
    public void ClearCache(string? shareId = null)
    {
        lock (_cacheLock)
        {
            if (shareId == null)
            {
                var count = _browseCache.Count;
                _browseCache.Clear();
                Console.WriteLine($"[NETWORK] Cleared {count} cached directories");
            }
            else
            {
                var keysToRemove = _browseCache.Keys.Where(k => k.StartsWith($"{shareId}:")).ToList();
                foreach (var key in keysToRemove) _browseCache.Remove(key);
                Console.WriteLine($"[NETWORK] Cleared {keysToRemove.Count} cached directories for share {shareId}");
            }
        }
    }

    /// <summary>
    ///     Get cache statistics
    /// </summary>
    public (int TotalEntries, int ExpiredEntries) GetCacheStats()
    {
        lock (_cacheLock)
        {
            var total = _browseCache.Count;
            var expired = _browseCache.Values.Count(c => c.IsExpired(_cacheExpiration));
            return (total, expired);
        }
    }

    /// <summary>
    ///     Browse SMB directory using smbclient
    /// </summary>
    private async Task<BrowseResult> BrowseSmbDirectoryAsync(NetworkShare share, string subPath)
    {
        var result = new BrowseResult { Path = subPath };
        var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".webm", ".mov", ".flv", ".m4v", ".wmv", ".ts", ".m2ts" };
        var audioExtensions = new[] { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".wma", ".opus" };

        try
        {
            var authArgs = BuildSmbClientAuthArgs(share);
            var uncPath = $"//{share.Server}/{share.SharePath}";

            // Build smbclient command to list directory
            var lsPath = string.IsNullOrEmpty(subPath) ? "" : subPath.Replace("/", "\\");
            var displayPath = $"{uncPath}/{subPath}".Replace("//", "/");
            Console.WriteLine($"[NETWORK] Browsing: {displayPath}");

            var psi = new ProcessStartInfo("smbclient");
            psi.ArgumentList.Add(uncPath);
            foreach (var arg in authArgs) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(string.IsNullOrEmpty(lsPath) ? "ls" : $"cd \"{lsPath}\"; ls");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var proc = Process.Start(psi);
            if (proc == null) return result;

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();

            await proc.WaitForExitAsync();

            // Parse smbclient output (format: "  filename                          D        0  Mon Jan 01 00:00:00 2024")
            // D = directory, A = archive (file), H = hidden, S = system, R = read-only
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Skip known non-file lines
                if (trimmed.StartsWith("Try ") ||
                    trimmed.Contains("blocks of size") ||
                    trimmed.Contains("blocks available") ||
                    trimmed.StartsWith("smb:") ||
                    trimmed.Contains("NT_STATUS"))
                {
                    if (trimmed.StartsWith("Try ") || trimmed.Contains("NT_STATUS"))
                        Console.WriteLine($"[NETWORK] Could not parse line: {trimmed}");
                    continue;
                }

                // Parse the line - format varies but generally:
                // "  filename                          D        0  Mon Jan 01 00:00:00 2024"
                // The attributes (D, A, H, etc.) appear after the filename, before the size

                // Find where attributes start (single letters followed by spaces then a number)
                var match = Regex.Match(trimmed,
                    @"^(.+?)\s+([DAHSR]+)\s+(\d+)\s+\w{3}\s+\w{3}\s+\d+\s+\d{2}:\d{2}:\d{2}\s+\d{4}");

                if (!match.Success)
                    // Try alternate format without attributes visible
                    match = Regex.Match(trimmed,
                        @"^(.+?)\s+(\d+)\s+\w{3}\s+\w{3}\s+\d+\s+\d{2}:\d{2}:\d{2}\s+\d{4}");

                if (!match.Success) continue;

                var name = match.Groups[1].Value.Trim();
                var isDirectory = match.Groups.Count > 3 && match.Groups[2].Value.Contains('D');

                // Skip . and .. entries
                if (name == "." || name == "..") continue;

                var fullPath = string.IsNullOrEmpty(subPath) ? name : $"{subPath}/{name}";

                if (isDirectory)
                {
                    result.Directories.Add(new DirectoryEntry { Name = name, Path = fullPath });
                }
                else
                {
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (videoExtensions.Contains(ext))
                        result.Videos.Add(new VideoEntry { Name = name, Path = fullPath });
                    else if (audioExtensions.Contains(ext))
                        result.AudioFiles.Add(new AudioEntry { Name = name, Path = fullPath });
                }
            }

            result.Directories = result.Directories.OrderBy(d => d.Name).ToList();
            result.Videos = result.Videos.OrderBy(v => v.Name).ToList();
            result.AudioFiles = result.AudioFiles.OrderBy(a => a.Name).ToList();

            Console.WriteLine(
                $"[NETWORK] Found {result.Directories.Count} directories, {result.Videos.Count} videos, {result.AudioFiles.Count} audio files");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NETWORK] Error browsing SMB: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    ///     Build smbclient authentication arguments
    /// </summary>
    private List<string> BuildSmbClientAuthArgs(NetworkShare share)
    {
        var args = new List<string>();

        if (!string.IsNullOrEmpty(share.Username))
        {
            args.Add("-U");
            var password = share.EncryptedPassword != null ? DecryptPassword(share.EncryptedPassword) : "";
            var userArg = string.IsNullOrEmpty(share.Domain)
                ? $"{share.Username}%{password}"
                : $"{share.Domain}\\{share.Username}%{password}";
            args.Add(userArg);
        }
        else
        {
            args.Add("-N"); // No password (guest)
        }

        return args;
    }

    private void LoadShares()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _shares = JsonSerializer.Deserialize<List<NetworkShare>>(json) ?? new List<NetworkShare>();
                Console.WriteLine($"[NETWORK] Loaded {_shares.Count} network shares");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NETWORK] Error loading shares: {ex.Message}");
            _shares = new List<NetworkShare>();
        }
    }

    private void SaveShares()
    {
        try
        {
            var json = JsonSerializer.Serialize(_shares, new JsonSerializerOptions { WriteIndented = true });
            FileHelper.AtomicWriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NETWORK] Error saving shares: {ex.Message}");
        }
    }

    public string EncryptPassword(string password)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(password);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV to encrypted data
        var result = new byte[aes.IV.Length + encryptedBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(encryptedBytes, 0, result, aes.IV.Length, encryptedBytes.Length);

        return Convert.ToBase64String(result);
    }

    public string DecryptPassword(string encrypted)
    {
        var data = Convert.FromBase64String(encrypted);

        using var aes = Aes.Create();
        aes.Key = _encryptionKey;

        // Extract IV from beginning of data
        var iv = new byte[16];
        Buffer.BlockCopy(data, 0, iv, 0, 16);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var decryptedBytes = decryptor.TransformFinalBlock(data, 16, data.Length - 16);

        return Encoding.UTF8.GetString(decryptedBytes);
    }
}

/// <summary>
///     Result of browsing a directory
/// </summary>
public record BrowseResult
{
    public string Path { get; set; } = "";
    public List<DirectoryEntry> Directories { get; set; } = new();
    public List<VideoEntry> Videos { get; set; } = new();
    public List<AudioEntry> AudioFiles { get; set; } = new();
    public bool FromCache { get; set; }
}

/// <summary>
///     Directory entry in a browse result
/// </summary>
public record DirectoryEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>
///     Video entry in a browse result
/// </summary>
public record VideoEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>
///     Audio entry in a browse result
/// </summary>
public record AudioEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}
