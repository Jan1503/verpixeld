using System.Net;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace verpixeld.MediaPlayer;

/// <summary>
///     A square cover for a radio station. Station logos come in every shape: .ico, .svg,
///     addresses without an extension, 16-pixel favicons. This tries the logo from Radio Browser,
///     then the station's website (apple-touch-icon, og:image, large icons), keeps the largest
///     picture Skia can decode, and fits it on a 160×160 tile. With nothing usable it draws a
///     radio on a colour taken from the station name, so the panel always has a cover.
/// </summary>
public static class StationCover
{
    private const int Size = 160;
    private const int MinGood = 64;

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; verpixeld/1.0)");
        http.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/png,image/*;q=0.8,*/*;q=0.5");
        return http;
    }

    /// <summary>PNG data URL of the cover; never null.</summary>
    public static async Task<string> BuildAsync(RadioStationEntry station, CancellationToken ct = default)
    {
        SKBitmap? best = null;
        try
        {
            foreach (var url in await CandidatesAsync(station, ct))
            {
                var bmp = await TryLoadAsync(url, ct);
                if (bmp == null) continue;
                var side = Math.Min(bmp.Width, bmp.Height);
                if (best == null || side > Math.Min(best.Width, best.Height))
                {
                    best?.Dispose();
                    best = bmp;
                }
                else
                {
                    bmp.Dispose();
                }
                if (side >= MinGood) break;
            }
            using var tile = best != null ? Fit(best) : Generated(station.Name);
            using var img = SKImage.FromBitmap(tile);
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
        }
        finally
        {
            best?.Dispose();
        }
    }

    private static async Task<List<string>> CandidatesAsync(RadioStationEntry station, CancellationToken ct)
    {
        var list = new List<string>();
        void Add(string? u, Uri? baseUri = null)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            u = WebUtility.HtmlDecode(u.Trim());
            if (u.StartsWith("//")) u = "https:" + u;
            Uri? abs = null;
            if (Uri.TryCreate(u, UriKind.Absolute, out var a) && (a.Scheme == "http" || a.Scheme == "https")) abs = a;
            else if (baseUri != null && Uri.TryCreate(baseUri, u, out var r)) abs = r;
            if (abs != null && !list.Contains(abs.ToString())) list.Add(abs.ToString());
        }

        Add(station.Favicon);
        if (Uri.TryCreate(station.Homepage, UriKind.Absolute, out var home) &&
            (home.Scheme == "http" || home.Scheme == "https"))
        {
            try
            {
                var html = await GetHtmlAsync(home, ct);
                // Biggest first: touch icons and page images are made to be large.
                foreach (Match m in Regex.Matches(html, @"<link[^>]+>", RegexOptions.IgnoreCase))
                {
                    var tag = m.Value;
                    var rel = Attr(tag, "rel")?.ToLowerInvariant() ?? "";
                    if (rel.Contains("apple-touch-icon")) Add(Attr(tag, "href"), home);
                }
                foreach (Match m in Regex.Matches(html, @"<meta[^>]+>", RegexOptions.IgnoreCase))
                {
                    var tag = m.Value;
                    var prop = (Attr(tag, "property") ?? Attr(tag, "name") ?? "").ToLowerInvariant();
                    if (prop is "og:image" or "og:image:url" or "twitter:image") Add(Attr(tag, "content"), home);
                }
                var icons = new List<(int Size, string Href)>();
                foreach (Match m in Regex.Matches(html, @"<link[^>]+>", RegexOptions.IgnoreCase))
                {
                    var tag = m.Value;
                    var rel = Attr(tag, "rel")?.ToLowerInvariant() ?? "";
                    if (!rel.Contains("icon")) continue;
                    var href = Attr(tag, "href");
                    if (href == null) continue;
                    var sizes = Attr(tag, "sizes") ?? "";
                    var n = Regex.Match(sizes, @"(\d+)x\d+");
                    icons.Add((n.Success ? int.Parse(n.Groups[1].Value) : 0, href));
                }
                foreach (var icon in icons.OrderByDescending(i => i.Size)) Add(icon.Href, home);
            }
            catch
            {
                // The website is only a second chance.
            }
            Add("/apple-touch-icon.png", home);
            Add("/favicon.ico", home);
        }
        return list;
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, name + @"\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
    }

    private static async Task<string> GetHtmlAsync(Uri url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("text/html");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[384 * 1024];
        var n = 0;
        int r;
        while (n < buf.Length && (r = await s.ReadAsync(buf.AsMemory(n), ct)) > 0) n += r;
        return System.Text.Encoding.UTF8.GetString(buf, 0, n);
    }

    private static async Task<SKBitmap?> TryLoadAsync(string url, CancellationToken ct)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct);
            if (bytes.Length < 16 || bytes.Length > 8 * 1024 * 1024) return null;
            var bmp = SKBitmap.Decode(bytes);
            if (bmp == null || bmp.Width < 8 || bmp.Height < 8)
            {
                bmp?.Dispose();
                return null;
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The logo whole (not cropped) on a tile; light logos get a dark ground, dark ones a light ground.</summary>
    private static SKBitmap Fit(SKBitmap logo)
    {
        var tile = new SKBitmap(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(tile);
        var (bg, opaque) = Ground(logo);
        canvas.Clear(bg);
        var pad = opaque && Math.Abs(logo.Width - logo.Height) < Math.Max(logo.Width, logo.Height) / 10 ? 0 : 12;
        var box = Size - 2 * pad;
        var scale = Math.Min((float)box / logo.Width, (float)box / logo.Height);
        var w = logo.Width * scale;
        var h = logo.Height * scale;
        var dst = SKRect.Create((Size - w) / 2, (Size - h) / 2, w, h);
        using var image = SKImage.FromBitmap(logo);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        return tile;
    }

    private static (SKColor Bg, bool Opaque) Ground(SKBitmap logo)
    {
        long r = 0, g = 0, b = 0, n = 0, clear = 0, lum = 0, seen = 0, edges = 0;
        var step = Math.Max(1, Math.Min(logo.Width, logo.Height) / 32);
        for (var y = 0; y < logo.Height; y += step)
        {
            for (var x = 0; x < logo.Width; x += step)
            {
                var c = logo.GetPixel(x, y);
                seen++;
                if (c.Alpha < 40)
                {
                    clear++;
                    continue;
                }
                lum += (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000;
                n++;
                var edge = x < step || y < step || x >= logo.Width - step || y >= logo.Height - step;
                if (!edge) continue;
                edges++;
                r += c.Red;
                g += c.Green;
                b += c.Blue;
            }
        }
        var opaque = clear * 10 < seen;
        if (opaque)
        {
            // Edge colour, so a logo on a coloured square fills the tile seamlessly.
            var e = Math.Max(1, edges);
            return (new SKColor((byte)(r / e), (byte)(g / e), (byte)(b / e)), true);
        }
        var avg = n == 0 ? 128 : lum / n;
        return (avg > 150 ? new SKColor(0x1A, 0x1F, 0x29) : new SKColor(0xF1, 0xF3, 0xF6), false);
    }

    /// <summary>A radio drawn on a colour from the station name.</summary>
    private static SKBitmap Generated(string name)
    {
        var tile = new SKBitmap(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(tile);
        var hash = 0u;
        foreach (var ch in name ?? "") hash = hash * 31 + ch;
        var hue = hash % 360;
        var top = SKColor.FromHsl(hue, 70, 45);
        var bottom = SKColor.FromHsl((hue + 40) % 360, 75, 25);
        using (var bgPaint = new SKPaint())
        {
            bgPaint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(Size, Size),
                new[] { top, bottom }, SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, Size, Size, bgPaint);
        }
        using var white = new SKPaint { Color = new SKColor(255, 255, 255, 235), IsAntialias = true };
        using var stroke = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 235), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 5, StrokeCap = SKStrokeCap.Round
        };
        // antenna, body, speaker, dial
        canvas.DrawLine(56, 58, 112, 34, stroke);
        canvas.DrawRoundRect(SKRect.Create(32, 58, 96, 64), 12, 12, white);
        using var hole = new SKPaint { Color = bottom, IsAntialias = true };
        canvas.DrawCircle(64, 90, 18, hole);
        canvas.DrawCircle(64, 90, 7, white);
        canvas.DrawRoundRect(SKRect.Create(92, 72, 24, 8), 3, 3, hole);
        canvas.DrawRoundRect(SKRect.Create(92, 88, 24, 8), 3, 3, hole);
        canvas.DrawRoundRect(SKRect.Create(92, 104, 24, 8), 3, 3, hole);
        return tile;
    }
}
