using SkiaSharp;
using verpixeld.MediaPlayer;

namespace verpixeld.Tests;

public class FfmpegRawVideoTests
{
    [Fact]
    public void Frame_is_packed_rgb24()
    {
        Assert.Equal("rgb24", FfmpegRawVideo.PixFmt);
        Assert.Equal(3, FfmpegRawVideo.BytesPerPixel);
        Assert.Equal(256 * 128 * 3, FfmpegRawVideo.FrameBytes(256, 128));
    }

    [Fact]
    public void CopyToBitmap_expands_rgb24_to_bgra()
    {
        var rgb24 = new byte[] { 30, 20, 10 }; // R, G, B
        using var bitmap = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Opaque);
        FfmpegRawVideo.CopyToBitmap(bitmap, rgb24);
        var px = bitmap.GetPixel(0, 0);
        Assert.Equal(30, px.Red);
        Assert.Equal(20, px.Green);
        Assert.Equal(10, px.Blue);
        Assert.Equal(255, px.Alpha);
    }
}
