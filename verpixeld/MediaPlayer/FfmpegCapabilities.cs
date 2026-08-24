using System.Diagnostics;

namespace verpixeld.MediaPlayer;

/// <summary>
///     Static utility for checking FFmpeg/curl availability and hardware capabilities.
/// </summary>
public static class FfmpegCapabilities
{
    private static bool? _hwAccelAvailable;

    /// <summary>Diagnostic output control for FFmpeg commands.</summary>
    public static bool DiagnosticsEnabled { get; set; }

    /// <summary>
    ///     Available FFmpeg scaling filters with descriptions.
    /// </summary>
    public static readonly Dictionary<string, string> AvailableScaleFilters = new()
    {
        ["auto"] = "Automatic (fast_bilinear for streams, lanczos for local)",
        ["fast_bilinear"] = "Fast bilinear - fastest, can look sharp/aliased",
        ["bilinear"] = "Bilinear - smooth, slight blur",
        ["bicubic"] = "Bicubic - balanced quality",
        ["lanczos"] = "Lanczos - highest quality, sharpest, slowest",
        ["area"] = "Area averaging - great for downscaling, smooth result",
        ["gauss"] = "Gaussian - soft/blurred, good for LED matrices",
        ["sinc"] = "Sinc - very high quality, slow"
    };

    /// <summary>Check if FFmpeg is available.</summary>
    public static bool IsFFmpegAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Check if FFmpeg has SMB protocol support.</summary>
    public static bool IsFFmpegSmbSupported()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-protocols")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(2000);

            var text = output + "\n" + error;
            foreach (var line in text.Split('\n', '\r'))
            {
                if (line.Trim().Equals("smb", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Check if curl is available for HTTP/FTP streaming.</summary>
    public static bool IsCurlAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("curl", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Check if V4L2 M2M hardware decoding is available (Raspberry Pi).</summary>
    public static bool IsHwAccelAvailable()
    {
        if (_hwAccelAvailable.HasValue) return _hwAccelAvailable.Value;

        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-decoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            _hwAccelAvailable = output.Contains("h264_v4l2m2m") || output.Contains("hevc_v4l2m2m");

            if (_hwAccelAvailable.Value) Console.WriteLine("[VIDEO] Hardware decoding available (V4L2 M2M)");

            return _hwAccelAvailable.Value;
        }
        catch
        {
            _hwAccelAvailable = false;
            return false;
        }
    }
}
