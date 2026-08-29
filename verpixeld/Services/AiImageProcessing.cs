using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace verpixeld.Services;

/// <summary>
///     LED-aware post-processing for AI images: center-crop to the wall aspect,
///     nearest-neighbour downscale, optional posterize for pixel-art styles.
/// </summary>
public static class AiImageProcessing
{
    public static byte[] ScaleToDisplayPng(byte[] imageBytes, int destWidth, int destHeight, string? style = null)
    {
        using var original = SKBitmap.Decode(imageBytes)
            ?? throw new Exception("Failed to decode generated image");
        using var scaled = ScaleToSize(original, destWidth, destHeight, PosterizeLevelsForStyle(style));
        using var image = SKImage.FromBitmap(scaled);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    ///     Center-crop to <paramref name="destWidth"/>:<paramref name="destHeight"/> aspect,
    ///     then nearest-neighbour resize. Caller owns the returned bitmap.
    /// </summary>
    public static SKBitmap ScaleToSize(SKBitmap source, int destWidth, int destHeight, int posterizeLevels = 0)
    {
        destWidth = Math.Max(1, destWidth);
        destHeight = Math.Max(1, destHeight);

        using var cropped = CenterCropToAspect(source, destWidth, destHeight);

        SKBitmap scaled;
        if (cropped.Width == destWidth && cropped.Height == destHeight)
        {
            scaled = cropped.Copy() ?? throw new Exception("Failed to copy cropped image");
        }
        else
        {
            scaled = cropped.Resize(
                new SKImageInfo(destWidth, destHeight),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                ?? throw new Exception("Failed to scale image");
        }

        if (posterizeLevels > 1)
            PosterizeInPlace(scaled, posterizeLevels);

        return scaled;
    }

    public static string ContentHashHex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static int PosterizeLevelsForStyle(string? style) => style switch
    {
        "pixel-art" => 4,
        "retro-8bit" => 3,
        _ => 0
    };

    public static string FriendlyHttpError(int statusCode, string responseBody, string providerLabel)
    {
        var raw = ExtractErrorMessage(responseBody);
        var lower = raw.ToLowerInvariant();
        if (statusCode == 429
            || lower.Contains("rate limit")
            || lower.Contains("too many requests"))
            return "Rate limited. Wait a minute and try again.";
        if (lower.Contains("content_filter")
            || lower.Contains("content management policy")
            || lower.Contains("responsibleai")
            || lower.Contains("safety system")
            || lower.Contains("content filtering"))
            return "Prompt blocked by content filter. Try a different description.";
        return $"{providerLabel} error ({statusCode}): {raw}";
    }

    public static string ExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errorEl)
                && errorEl.TryGetProperty("message", out var msgEl))
                return msgEl.GetString() ?? "Unknown error";
        }
        catch
        {
            // body is not JSON — fall through
        }

        return json.Length > 200 ? json[..200] + "..." : json;
    }

    internal static SKBitmap CenterCropToAspect(SKBitmap source, int destWidth, int destHeight)
    {
        var destAspect = (double)destWidth / destHeight;
        var srcAspect = (double)source.Width / source.Height;

        int cropW, cropH, cropX, cropY;
        if (Math.Abs(srcAspect - destAspect) < 0.001)
        {
            var copy = source.Copy() ?? throw new Exception("Failed to copy image");
            return copy;
        }

        if (srcAspect > destAspect)
        {
            cropH = source.Height;
            cropW = Math.Clamp((int)Math.Round(source.Height * destAspect), 1, source.Width);
            cropX = (source.Width - cropW) / 2;
            cropY = 0;
        }
        else
        {
            cropW = source.Width;
            cropH = Math.Clamp((int)Math.Round(source.Width / destAspect), 1, source.Height);
            cropX = 0;
            cropY = (source.Height - cropH) / 2;
        }

        var subset = new SKBitmap();
        var rect = new SKRectI(cropX, cropY, cropX + cropW, cropY + cropH);
        if (!source.ExtractSubset(subset, rect))
            throw new Exception("Failed to crop image to display aspect");

        var owned = subset.Copy() ?? subset;
        if (!ReferenceEquals(owned, subset))
            subset.Dispose();
        return owned;
    }

    internal static void PosterizeInPlace(SKBitmap bitmap, int levels)
    {
        levels = Math.Max(2, levels);
        var pixels = bitmap.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            pixels[i] = new SKColor(
                QuantizeChannel(c.Red, levels),
                QuantizeChannel(c.Green, levels),
                QuantizeChannel(c.Blue, levels),
                c.Alpha);
        }

        bitmap.Pixels = pixels;
    }

    private static byte QuantizeChannel(byte value, int levels)
    {
        var bucket = (value * (levels - 1) + 127) / 255;
        return (byte)(bucket * 255 / (levels - 1));
    }
}
