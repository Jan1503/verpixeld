using System.Globalization;
using CanvasManagement;
using CanvasManagement.BdfFontManager;
using CanvasManagement.Interfaces;
using SkiaSharp;
using verpixeld.Configuration;

namespace verpixeld.Services;

/// <summary>
///     Bottom-banner overlay for Home Assistant persistent notifications (z=340, under voice feedback).
///     Auto-dismisses after a few seconds; queued if several arrive at once.
///     Opaque strip so the compositor's opaque-layer path shows it.
/// </summary>
public sealed class HaToastService : IDisposable
{
    private const int ZOrder = 340;

    private readonly CanvasManager _canvasManager;
    private readonly HomeAssistantService _homeAssistant;
    private readonly int _width;
    private readonly int _height;
    private readonly object _lock = new();
    private readonly Queue<HaNotification> _queue = new();

    private Canvas? _canvas;
    private SKBitmap? _bitmap;
    private System.Threading.Timer? _timer;
    private bool _showing;
    private bool _disposed;
    private int _barH;

    public HaToastService(CanvasManager canvasManager, int width, int height, HomeAssistantService homeAssistant)
    {
        _canvasManager = canvasManager;
        _homeAssistant = homeAssistant;
        _width = width;
        _height = height;
        _barH = Math.Max(22, height / 5);
        HomeAssistantBridge.Notification += OnNotification;
        Console.WriteLine($"[HA Toast] Overlay ready ({width}x{height}, bar={_barH}px, z={ZOrder})");
    }

    private void OnNotification(HaNotification n)
    {
        if (_disposed) return;
        var opts = SnapshotToast();
        if (!opts.Enabled)
        {
            Console.WriteLine("[HA Toast] skipped (toasts disabled)");
            return;
        }

        Console.WriteLine($"[HA Toast] event \"{n.Title}\": {n.Message}");
        lock (_lock)
        {
            _queue.Enqueue(n);
            if (!_showing) ShowNext();
        }
    }

    private void ShowNext()
    {
        try
        {
            if (_queue.Count == 0)
            {
                Hide();
                return;
            }

            var n = _queue.Dequeue();
            var opts = SnapshotToast();
            if (!opts.Enabled)
            {
                _queue.Clear();
                Hide();
                return;
            }

            _showing = true;
            var severity = ResolveSeverity(n.Severity, n.NotificationId, n.Title, opts.DefaultSeverity);
            Console.WriteLine($"[HA Toast] showing [{severity}] \"{n.Title}\": {n.Message}");

            var fontName = ResolveFont(opts.Font);
            var lineH = MeasureLineHeight(fontName);
            var barH = Math.Clamp(Math.Max(Math.Max(22, _height / 5), lineH * 2 + 8), 16, _height);

            EnsureCanvas(barH);
            _canvas!.Show();

            var bg = ParseColor(opts.Background, new SKColor(18, 22, 32));
            var accent = ParseColor(opts.AccentFor(severity), new SKColor(3, 169, 244));
            var titleColor = ParseColor(opts.TitleColor, SKColors.White);
            var msgColor = ParseColor(opts.MessageColor, new SKColor(200, 210, 220));
            var accentW = Math.Max(3, _width / 64);
            var pad = Math.Max(6, _width / 40);

            using (var c = new SKCanvas(_bitmap!))
            {
                c.Clear(bg);
                using var accentPaint = new SKPaint { Color = accent, IsAntialias = false };
                c.DrawRect(0, 0, accentW, _barH, accentPaint);
            }

            _canvas.SubmitCompletedFrame(_bitmap!);

            var textX = pad + accentW;
            var maxW = Math.Max(8, _width - textX - pad);
            var title = Fit(fontName, n.Title, maxW);
            var message = Fit(fontName, n.Message, maxW);
            var titleY = Math.Max(1, (_barH / 2 - lineH) / 2);
            var msgY = _barH / 2 + Math.Max(1, (_barH / 2 - lineH) / 2);

            if (fontName != null)
            {
                _canvas.DrawBdfText(title, textX, titleY, titleColor, fontName);
                _canvas.DrawBdfText(message, textX, msgY, msgColor, fontName);
            }
            else
            {
                using var titlePaint = new SKPaint { Color = titleColor, IsAntialias = true };
                using var msgPaint = new SKPaint { Color = msgColor, IsAntialias = true };
                using var titleFont = new SKFont(SKTypeface.Default, Math.Max(8, _barH * 0.38f));
                using var msgFont = new SKFont(SKTypeface.Default, Math.Max(7, _barH * 0.32f));
                using var sk = new SKCanvas(_bitmap!);
                sk.DrawText(title, textX, _barH * 0.42f, SKTextAlign.Left, titleFont, titlePaint);
                sk.DrawText(message, textX, _barH * 0.82f, SKTextAlign.Left, msgFont, msgPaint);
                _canvas.SubmitCompletedFrame(_bitmap!);
            }

            var duration = Math.Clamp(opts.DurationMs, 1000, 60_000);
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ =>
            {
                lock (_lock)
                {
                    try { ShowNext(); }
                    catch (Exception ex) { Console.WriteLine($"[HA Toast] timer error: {ex.Message}"); }
                }
            }, null, duration, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HA Toast] render error: {ex.Message}");
            _showing = false;
        }
    }

    private HomeAssistantToastOptions SnapshotToast()
    {
        try { return _homeAssistant.Snapshot().Toast?.Clone() ?? new HomeAssistantToastOptions(); }
        catch { return new HomeAssistantToastOptions(); }
    }

    private void EnsureCanvas(int barH)
    {
        if (_canvas != null && _bitmap != null && _barH == barH) return;

        if (_canvas != null)
        {
            try { _canvasManager.RemoveCanvas(_canvas); }
            catch { }
            _canvas = null;
        }

        _barH = barH;
        _canvas = _canvasManager.GetCanvas(0, _height - _barH, _width, _barH, ZOrder, "HaToast");
        _canvas.TransparentBackground = false;
        _canvas.PanelColorBits = 8;

        _bitmap?.Dispose();
        _bitmap = new SKBitmap(_width, _barH);
    }

    private void Hide()
    {
        _showing = false;
        try { _canvas?.Hide(); }
        catch { }
    }

    internal static string ResolveSeverity(string? explicitSeverity, string? notificationId, string? title,
        string fallback)
    {
        if (TryParseSeverity(explicitSeverity, out var sev)) return sev;

        var id = notificationId ?? "";
        var colon = id.IndexOf(':');
        if (colon > 0 && TryParseSeverity(id[..colon], out sev)) return sev;

        foreach (var token in new[] { "critical", "error", "warning", "warn", "success", "info" })
        {
            if (!StartsWithToken(id, token)) continue;
            if (TryParseSeverity(token, out sev)) return sev;
        }

        var t = (title ?? "").Trim();
        var bracket = t.IndexOf(']');
        if (t.StartsWith('[') && bracket > 1 && TryParseSeverity(t[1..bracket], out sev))
            return sev;

        return TryParseSeverity(fallback, out sev) ? sev : "info";
    }

    internal static bool TryParseSeverity(string? raw, out string severity)
    {
        severity = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        severity = raw.Trim().ToLowerInvariant() switch
        {
            "error" or "err" or "critical" or "danger" or "alarm" => "error",
            "warning" or "warn" => "warning",
            "success" or "ok" or "done" => "success",
            "info" or "information" or "debug" => "info",
            _ => ""
        };
        return severity.Length > 0;
    }

    private static bool StartsWithToken(string id, string token)
    {
        if (!id.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Length == token.Length) return true;
        var next = id[token.Length];
        return next is ':' or '_' or '-' or '.';
    }

    private string? ResolveFont(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var name in BdfFontRegistry.RegisteredFonts)
                if (string.Equals(name, configured, StringComparison.OrdinalIgnoreCase))
                    return name;
        }

        var target = Math.Max(6, Math.Max(22, _height / 5) / 2 - 2);
        return BdfFontRegistry.GetBestFontForHeight(target)
               ?? BdfFontRegistry.DefaultFontName;
    }

    private int MeasureLineHeight(string? fontName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fontName)) return Math.Max(8, _height / 12);
            return Math.Max(6, (int)BdfFontRegistry.GetFont(fontName).MeasureText("Ag").Height);
        }
        catch
        {
            return Math.Max(8, _height / 12);
        }
    }

    private static string Fit(string? fontName, string? text, int maxWidth)
    {
        var s = text ?? "";
        if (s.Length == 0 || string.IsNullOrWhiteSpace(fontName)) return s;
        try
        {
            var font = BdfFontRegistry.GetFont(fontName);
            if (font.MeasureText(s).Width <= maxWidth) return s;
            while (s.Length > 1 && font.MeasureText(s + "...").Width > maxWidth)
                s = s[..^1];
            return s + "...";
        }
        catch
        {
            return s.Length > 40 ? s[..40] + "..." : s;
        }
    }

    internal static SKColor ParseColor(string? hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return new SKColor((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
        return fallback;
    }

    internal static string NormalizeHex(string? hex, string fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 ||
            !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return fallback;
        return "#" + s.ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HomeAssistantBridge.Notification -= OnNotification;
        _timer?.Dispose();
        _timer = null;
        lock (_lock)
        {
            _queue.Clear();
            Hide();
            if (_canvas != null)
            {
                try { _canvasManager.RemoveCanvas(_canvas); }
                catch { }
                _canvas = null;
            }
        }

        _bitmap?.Dispose();
        _bitmap = null;
        GC.SuppressFinalize(this);
    }
}
