using SkiaSharp;
using verpixeld.Services;

namespace verpixeld.Tests;

public class AiImageProcessingTests
{
    [Fact]
    public void ScaleToSize_center_crops_square_to_wide_display()
    {
        using var source = new SKBitmap(8, 8);
        source.Erase(SKColors.Red);
        for (var x = 0; x < 8; x++)
        {
            source.SetPixel(x, 0, SKColors.Lime);
            source.SetPixel(x, 7, SKColors.Lime);
        }

        using var scaled = AiImageProcessing.ScaleToSize(source, 8, 4);

        Assert.Equal(8, scaled.Width);
        Assert.Equal(4, scaled.Height);
        Assert.Equal(SKColors.Red, scaled.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, scaled.GetPixel(7, 3));
    }

    [Fact]
    public void ScaleToSize_uses_nearest_neighbour_not_blend()
    {
        using var source = new SKBitmap(4, 2);
        for (var y = 0; y < 2; y++)
        {
            source.SetPixel(0, y, SKColors.Red);
            source.SetPixel(1, y, SKColors.Red);
            source.SetPixel(2, y, SKColors.Blue);
            source.SetPixel(3, y, SKColors.Blue);
        }

        using var scaled = AiImageProcessing.ScaleToSize(source, 2, 1);

        Assert.Equal(SKColors.Red, scaled.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, scaled.GetPixel(1, 0));
    }

    [Fact]
    public void Posterize_collapses_a_gradient_to_the_level_count()
    {
        using var bitmap = new SKBitmap(32, 1);
        for (var x = 0; x < 32; x++)
        {
            var v = (byte)(x * 8);
            bitmap.SetPixel(x, 0, new SKColor(v, v, v));
        }

        AiImageProcessing.PosterizeInPlace(bitmap, 4);

        var unique = new HashSet<byte>();
        for (var x = 0; x < 32; x++)
            unique.Add(bitmap.GetPixel(x, 0).Red);

        Assert.InRange(unique.Count, 1, 4);
    }

    [Fact]
    public void PosterizeLevelsForStyle_only_pixel_styles_quantize()
    {
        Assert.Equal(4, AiImageProcessing.PosterizeLevelsForStyle("pixel-art"));
        Assert.Equal(3, AiImageProcessing.PosterizeLevelsForStyle("retro-8bit"));
        Assert.Equal(0, AiImageProcessing.PosterizeLevelsForStyle("photograph"));
        Assert.Equal(0, AiImageProcessing.PosterizeLevelsForStyle(null));
    }

    [Fact]
    public void ScaleToDisplayPng_returns_display_sized_png()
    {
        using var source = new SKBitmap(64, 64);
        source.Erase(SKColors.Orange);
        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        var png = AiImageProcessing.ScaleToDisplayPng(data.ToArray(), 16, 8, "pixel-art");
        using var decoded = SKBitmap.Decode(png);

        Assert.NotNull(decoded);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(8, decoded.Height);
    }

    [Fact]
    public void ContentHashHex_is_stable_and_changes_with_bytes()
    {
        var a = new byte[] { 1, 2, 3, 4 };
        var b = new byte[] { 1, 2, 3, 5 };
        Assert.Equal(AiImageProcessing.ContentHashHex(a), AiImageProcessing.ContentHashHex(a));
        Assert.NotEqual(AiImageProcessing.ContentHashHex(a), AiImageProcessing.ContentHashHex(b));
        Assert.Equal(64, AiImageProcessing.ContentHashHex(a).Length);
    }

    [Theory]
    [InlineData(429, "{\"error\":{\"message\":\"Rate limit exceeded\"}}", "Rate limited. Wait a minute and try again.")]
    [InlineData(400, "{\"error\":{\"message\":\"Your prompt was blocked by the content_filter.\"}}", "Prompt blocked by content filter. Try a different description.")]
    [InlineData(500, "{\"error\":{\"message\":\"backend exploded\"}}", "Azure OpenAI error (500): backend exploded")]
    public void FriendlyHttpError_maps_status_and_filter(int status, string body, string expected) =>
        Assert.Equal(expected, AiImageProcessing.FriendlyHttpError(status, body, "Azure OpenAI"));
}
