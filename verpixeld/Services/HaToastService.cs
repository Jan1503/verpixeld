using CanvasManagement;
using CanvasManagement.Interfaces;
using SkiaSharp;

namespace verpixeld.Services;

/// <summary>
///     Bottom-banner overlay for Home Assistant persistent notifications (z=340, under voice feedback).
///     Auto-dismisses after a few seconds; queued if several arrive at once.
/// </summary>
public sealed class HaToastService : IDisposable
{
    private const int ZOrder = 340;
    private const int ShowMs = 8000;

    private readonly CanvasManager _canvasManager;
    private readonly int _width;
    private readonly int _height;
    private readonly object _lock = new();
    private readonly Queue<(string Title, string Message)> _queue = new();

    private Canvas? _canvas;
    private SKBitmap? _bitmap;
    private System.Threading.Timer? _timer;
    private bool _showing;
    private bool _disposed;

    public HaToastService(CanvasManager canvasManager, int width, int height)
    {
        _canvasManager = canvasManager;
        _width = width;
        _height = height;
        HomeAssistantBridge.Notification += OnNotification;
        Console.WriteLine($"[HA Toast] Overlay ready ({width}x{height}, z={ZOrder})");
    }

    private void OnNotification(string title, string message)
    {
        if (_disposed) return;
        lock (_lock)
        {
            _queue.Enqueue((title, message));
            if (!_showing) ShowNext();
        }
    }

    private void ShowNext()
    {
        if (_queue.Count == 0)
        {
            Hide();
            return;
        }

        var (title, message) = _queue.Dequeue();
        _showing = true;

        if (_canvas == null)
        {
            _canvas = _canvasManager.GetCanvas(0, 0, _width, _height, ZOrder, "HaToast");
            _canvas.TransparentBackground = true;
            _canvas.Show();
        }

        if (_bitmap == null || _bitmap.Width != _width || _bitmap.Height != _height)
        {
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
        }

        using (var c = new SKCanvas(_bitmap))
        {
            c.Clear(SKColors.Transparent);
            var barH = Math.Max(22, _height / 5);
            var y = _height - barH;
            using var bg = new SKPaint { Color = new SKColor(18, 22, 32, 230), IsAntialias = true };
            using var accent = new SKPaint { Color = new SKColor(3, 169, 244), IsAntialias = true };
            c.DrawRect(0, y, _width, barH, bg);
            c.DrawRect(0, y, Math.Max(3, _width / 64), barH, accent);

            using var titlePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var msgPaint = new SKPaint { Color = new SKColor(200, 210, 220), IsAntialias = true };
            using var titleFont = new SKFont(SKTypeface.Default, Math.Max(8, barH * 0.38f));
            using var msgFont = new SKFont(SKTypeface.Default, Math.Max(7, barH * 0.32f));
            var pad = Math.Max(6, _width / 40);
            c.DrawText(title, pad + 4, y + barH * 0.42f, titleFont, titlePaint);
            c.DrawText(message, pad + 4, y + barH * 0.82f, msgFont, msgPaint);
        }

        _canvas.DrawBitmap(_bitmap, 0, 0, _bitmap.Width, _bitmap.Height);
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ =>
        {
            lock (_lock) ShowNext();
        }, null, ShowMs, Timeout.Infinite);
        Console.WriteLine($"[HA Toast] {title}: {message}");
    }

    private void Hide()
    {
        _showing = false;
        try
        {
            _canvas?.Clear(SKColors.Transparent);
            _canvas?.Hide();
        }
        catch { }
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
