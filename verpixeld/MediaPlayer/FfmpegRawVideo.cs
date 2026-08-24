using SkiaSharp;

namespace verpixeld.MediaPlayer;

/// <summary>
///     FFmpeg rawvideo pipe is packed RGB24 (3 bytes/pixel). That is 25% less stdout than BGRA and
///     is what the Pi can keep at realtime (<c>speed=1.0x</c>). We expand to BGRA only when drawing.
/// </summary>
internal static class FfmpegRawVideo
{
    public const string PixFmt = "rgb24";
    public const int BytesPerPixel = 3;

    public static int FrameBytes(int width, int height) => width * height * BytesPerPixel;

    public static unsafe void CopyToBitmap(SKBitmap bitmap, byte[] rgb24)
    {
        var dstBase = (byte*)bitmap.GetPixels();
        if (dstBase == null) return;

        var width = bitmap.Width;
        var height = bitmap.Height;
        var dstStride = bitmap.RowBytes;
        var srcStride = width * BytesPerPixel;

        fixed (byte* srcBase = rgb24)
        {
            for (var y = 0; y < height; y++)
            {
                var s = srcBase + y * srcStride;
                var d = dstBase + y * dstStride;
                for (var x = 0; x < width; x++)
                {
                    d[0] = s[2]; // B
                    d[1] = s[1]; // G
                    d[2] = s[0]; // R
                    d[3] = 255;
                    s += 3;
                    d += 4;
                }
            }
        }
    }
}
