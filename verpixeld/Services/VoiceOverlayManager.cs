using CanvasManagement;
using SkiaSharp;

namespace verpixeld.Services;

/// <summary>
///     Manages visual overlays for the voice assistant:
///     - Feedback canvas (status bar + text, z=350)
///     - AI image overlay (generated image, z=250)
///     - Auto-clear timers for non-blocking feedback
/// </summary>
public class VoiceOverlayManager
{
    private readonly CanvasManager _canvasManager;

    private int _width;
    private int _height;

    // Feedback canvas
    private Canvas? _feedbackCanvas;
    private SKBitmap? _cachedBitmap;
    private CancellationTokenSource? _feedbackClearCts;

    // Image overlay
    private Canvas? _activeImageOverlay;
    private CancellationTokenSource? _imageDisplayCts;

    public VoiceOverlayManager(CanvasManager canvasManager)
    {
        _canvasManager = canvasManager;
    }

    public void Initialize(int width, int height)
    {
        _width = width;
        _height = height;
    }

    // ═══════════════════════════════════════════════════════════════
    // Feedback Canvas (status bar + word-wrapped text)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    ///     Show a feedback overlay with a coloured status bar and word-wrapped text.
    /// </summary>
    public void ShowFeedback(string text, SKColor accentColor, string? statusLabel = null)
    {
        try
        {
            if (_feedbackCanvas == null)
            {
                _feedbackCanvas = _canvasManager.GetCanvas(0, 0, _width, _height, 350, "VoiceFeedback");
                _feedbackCanvas.Show();
            }

            if (_cachedBitmap == null || _cachedBitmap.Width != _width || _cachedBitmap.Height != _height)
            {
                _cachedBitmap?.Dispose();
                _cachedBitmap = new SKBitmap(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
            }

            using var canvas = new SKCanvas(_cachedBitmap);
            canvas.Clear(new SKColor(10, 10, 18));

            const int padding = 4;

            // ── Top status bar with accent colour ──
            var barHeight = Math.Max(16, _height / 10);
            using var barPaint = new SKPaint { Color = accentColor, Style = SKPaintStyle.Fill };
            canvas.DrawRect(0, 0, _width, barHeight, barPaint);

            if (!string.IsNullOrEmpty(statusLabel))
            {
                using var barTextPaint = new SKPaint { Color = SKColors.White, IsAntialias = false };
                var barFontSize = Math.Max(8, barHeight - 4);
                using var barFont = new SKFont(SKTypeface.Default, barFontSize);
                var barBounds = new SKRect();
                barFont.MeasureText(statusLabel, out barBounds);
                var barTextX = (_width - barBounds.Width) / 2f;
                var barTextY = (barHeight + barBounds.Height) / 2f;
                canvas.DrawText(statusLabel, barTextX, barTextY, SKTextAlign.Left, barFont, barTextPaint);
            }

            // ── Thin accent line below the bar ──
            using var linePaint = new SKPaint
            {
                Color = new SKColor(accentColor.Red, accentColor.Green, accentColor.Blue, 100),
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRect(0, barHeight, _width, 1, linePaint);

            // ── Main text area: word-wrap the full text ──
            var textAreaTop = barHeight + padding + 2;
            var textAreaHeight = _height - textAreaTop - padding;
            var fontSize = Math.Max(8, Math.Min(12, _height / 14f));
            using var textFont = new SKFont(SKTypeface.Default, fontSize);
            using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = false };

            var lineHeight = fontSize + 2;
            var maxLines = (int)(textAreaHeight / lineHeight);
            var usableWidth = _width - padding * 2;

            var lines = WrapText(text, textFont, usableWidth);

            var totalTextHeight = lines.Count * lineHeight;
            var startY = lines.Count <= maxLines
                ? textAreaTop + (textAreaHeight - totalTextHeight) / 2f + fontSize
                : textAreaTop + fontSize;

            var renderedLines = Math.Min(lines.Count, maxLines);
            for (var i = 0; i < renderedLines; i++)
            {
                var lineY = startY + i * lineHeight;
                var lb = new SKRect();
                textFont.MeasureText(lines[i], out lb);
                var lineX = (_width - lb.Width) / 2f;
                canvas.DrawText(lines[i], lineX, lineY, SKTextAlign.Left, textFont, textPaint);
            }

            if (lines.Count > maxLines)
            {
                var ellY = startY + (renderedLines - 1) * lineHeight;
                var ellText = "...";
                var eb = new SKRect();
                textFont.MeasureText(ellText, out eb);
                canvas.DrawText(ellText, (_width - eb.Width) / 2f, ellY, SKTextAlign.Left, textFont, textPaint);
            }

            _feedbackCanvas.DrawBitmap(_cachedBitmap, 0, 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Feedback render error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Clear and remove the feedback overlay.
    /// </summary>
    public void ClearFeedback()
    {
        CancelFeedbackAutoClear();

        try
        {
            if (_feedbackCanvas != null)
            {
                _feedbackCanvas.Clear();
                _feedbackCanvas.Hide();
                _canvasManager.RemoveCanvas(_feedbackCanvas);
                _feedbackCanvas = null;
            }

            _cachedBitmap?.Dispose();
            _cachedBitmap = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Feedback cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Schedule the feedback canvas to auto-clear after a delay (non-blocking).
    /// </summary>
    public void ScheduleFeedbackAutoClear(int delayMs)
    {
        CancelFeedbackAutoClear();

        _feedbackClearCts = new CancellationTokenSource();
        var token = _feedbackClearCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                if (!token.IsCancellationRequested)
                    ClearFeedback();
            }
            catch (OperationCanceledException)
            {
                // Cancelled by a new command
            }
        });
    }

    /// <summary>
    ///     Cancel any pending feedback auto-clear timer.
    /// </summary>
    public void CancelFeedbackAutoClear()
    {
        try { _feedbackClearCts?.Cancel(); } catch (Exception ex) { Console.WriteLine($"[VOICE] Feedback auto-clear cancel error: {ex.Message}"); }
        _feedbackClearCts = null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Image Overlay (AI-generated images, z=250)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    ///     Display a bitmap on the image overlay canvas. Auto-dismisses after the specified duration.
    /// </summary>
    public void ShowImageOverlay(SKBitmap bitmap, int displayDurationSeconds)
    {
        DismissImageOverlay();

        var imageOverlay = _canvasManager.GetCanvas(0, 0, _width, _height, 250, "VoiceAiImage");
        imageOverlay.Show();
        imageOverlay.DrawBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height);
        _activeImageOverlay = imageOverlay;

        Console.WriteLine($"[VOICE] Image displayed on overlay, auto-dismiss in {displayDurationSeconds}s (or on next command)");

        _imageDisplayCts = new CancellationTokenSource();
        var displayCt = _imageDisplayCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(displayDurationSeconds * 1000, displayCt);
                DismissImageOverlay();
            }
            catch (OperationCanceledException)
            {
                // Dismissed early by a new command
            }
        });
    }

    /// <summary>
    ///     Dismiss the currently displayed image overlay immediately.
    /// </summary>
    public void DismissImageOverlay()
    {
        try { _imageDisplayCts?.Cancel(); } catch (Exception ex) { Console.WriteLine($"[VOICE] Image display CTS cancel error: {ex.Message}"); }
        _imageDisplayCts = null;

        var overlay = _activeImageOverlay;
        _activeImageOverlay = null;
        if (overlay != null)
        {
            try
            {
                overlay.Clear();
                overlay.Hide();
                _canvasManager.RemoveCanvas(overlay);
                Console.WriteLine("[VOICE] Image overlay dismissed by new command");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VOICE] Overlay dismiss error: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Dismiss ALL managed overlays. Called when a new voice command arrives
    ///     or when shutting down.
    /// </summary>
    public void DismissAll()
    {
        DismissImageOverlay();
        CancelFeedbackAutoClear();
        ClearFeedback();
    }

    // ═══════════════════════════════════════════════════════════════
    // Text word-wrapping
    // ═══════════════════════════════════════════════════════════════

    private static List<string> WrapText(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = "";

        foreach (var word in words)
        {
            var testLine = string.IsNullOrEmpty(currentLine) ? word : $"{currentLine} {word}";
            var bounds = new SKRect();
            font.MeasureText(testLine, out bounds);

            if (bounds.Width > maxWidth && !string.IsNullOrEmpty(currentLine))
            {
                lines.Add(currentLine);
                currentLine = word;
            }
            else
            {
                currentLine = testLine;
            }
        }

        if (!string.IsNullOrEmpty(currentLine))
            lines.Add(currentLine);

        return lines;
    }
}
