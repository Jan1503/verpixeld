using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CanvasManagement;
using CanvasManagement.BdfFontManager;
using SkiaSharp;

namespace verpixeld.Services;

/// <summary>
///     Service that handles application startup, including intro screen and default layout loading
/// </summary>
public class StartupService
{
    private readonly CanvasManager _canvasManager;

    // 3D Logo animation particles
    private readonly List<Particle> _particles = new();
    private readonly Random _random = new();

    // Responsive layout — computed from the actual display size when the intro starts.
    private float _scale = 1f;
    private string _introFont = "10x20";
    private int _charWidth = 22;
    private int _lineHeight = 21;

    public StartupService(CanvasManager canvasManager)
    {
        _canvasManager = canvasManager;
    }

    /// <summary>
    ///     Duration of the intro countdown in seconds
    /// </summary>
    public int CountdownSeconds { get; set; } = 5;

    /// <summary>
    ///     Display width
    /// </summary>
    public int DisplayWidth { get; set; } = 384;

    /// <summary>
    ///     Display height
    /// </summary>
    public int DisplayHeight { get; set; } = 192;

    /// <summary>
    ///     Preferred BDF font for the intro (from config). Empty = auto-pick by display height.
    /// </summary>
    public string? PreferredFont { get; set; }

    /// <summary>
    ///     Shows the intro/splash screen with animated 3D logo and countdown
    /// </summary>
    public async Task ShowIntroAsync()
    {
        Console.WriteLine("[INTRO] Creating intro canvas...");

        // Use a separate canvas for intro with higher z-order so it's on top
        var introCanvas = _canvasManager.GetCanvas(0, 0, DisplayWidth, DisplayHeight, 100, "IntroCanvas");
        introCanvas.Show();
        Console.WriteLine("[INTRO] Intro canvas created and shown");

        // Responsive setup: pick a bitmap font that fits the panel (~8 text rows tall) and derive
        // character/line metrics so the splash + system info scale to any resolution.
        _scale = Math.Min(DisplayWidth / 384f, DisplayHeight / 192f);

        // Use the configured startup font if set & registered; otherwise auto-pick a font sized
        // to the panel height (~8-9 text rows) so the multi-line system info fits short panels.
        if (!string.IsNullOrWhiteSpace(PreferredFont) && BdfFontRegistry.RegisteredFonts.Contains(PreferredFont))
        {
            _introFont = PreferredFont;
        }
        else
        {
            var targetGlyphHeight = Math.Max(5, DisplayHeight / 9);
            _introFont = BdfFontRegistry.GetBestFontForHeight(targetGlyphHeight) ?? "10x20";
        }

        BdfFontRegistry.DefaultFontName = _introFont;

        var glyph = introCanvas.MeasureBdfText("W", _introFont);
        _charWidth = Math.Max(1, (int)Math.Round(glyph.Width));
        _lineHeight = Math.Max(1, (int)Math.Round(glyph.Height) + 1);
        Console.WriteLine($"[INTRO] Responsive font '{_introFont}' charW={_charWidth} lineH={_lineHeight} scale={_scale:F2}");

        try
        {
            // Phase 1: Animated 3D splash screen with "pixeld" logo
            await ShowAnimatedSplashAsync(introCanvas);

            // Phase 2: Transition effect
            await ShowTransitionAsync(introCanvas);

            // Phase 3: System info with countdown
            await ShowSystemInfoWithCountdownAsync(introCanvas);

            Console.WriteLine("[INTRO] Countdown complete, removing intro canvas");
        }
        finally
        {
            // Clean up intro canvas
            introCanvas.Clear();
            introCanvas.Hide();

            // Remove the canvas from CanvasManager to prevent it from lingering
            _canvasManager.RemoveCanvas(introCanvas);

            // Force GC to clean up intro resources immediately
            GC.Collect();
            Console.WriteLine("[INTRO] Intro canvas removed and memory reclaimed");
        }
    }

    /// <summary>
    ///     Phase 1: Animated 3D-style splash screen
    /// </summary>
    private async Task ShowAnimatedSplashAsync(Canvas canvas)
    {
        Console.WriteLine("[INTRO] Starting animated splash...");

        // Initialize starfield particles
        InitializeParticles(80);

        // Animation timing
        const int flyInFrames = 45; // ~1.5 seconds for letters to arrive
        const int holdFrames = 75; // ~2.5 seconds holding
        const int totalFrames = flyInFrames + holdFrames;
        const int frameDelay = 33; // ~30fps

        // Calculate centered positions dynamically based on display size
        var charWidth = _charWidth; // Char advance for the responsive font
        const string logoText = "verpixeld";
        var totalTextWidth = logoText.Length * charWidth;
        var startX = (DisplayWidth - totalTextWidth) / 2;
        var centerY = DisplayHeight / 2 - (int)(10 * _scale); // Slightly above center

        // Create letter info with dynamically calculated positions
        var letters = new LetterInfo[logoText.Length];
        for (var i = 0; i < logoText.Length; i++)
            letters[i] = new LetterInfo
            {
                Char = logoText[i].ToString(),
                TargetX = startX + i * charWidth,
                TargetY = centerY
            };

        // Initialize letter starting positions - fly in from edges toward center
        for (var i = 0; i < letters.Length; i++)
        {
            var letter = letters[i];
            var centerIndex = letters.Length / 2.0f;
            var distanceFromCenter = i - centerIndex;

            // Letters fly in from left/right sides, outer letters start further out
            letter.StartX = letter.TargetX + (int)(distanceFromCenter * 80 * _scale);
            // Also slight vertical offset - wave pattern
            letter.StartY = letter.TargetY + (int)(Math.Sin(i * 0.8) * 40 * _scale);
            letter.StartScale = 0.0f; // Start invisible/small
        }

        for (var frame = 0; frame < totalFrames; frame++)
        {
            canvas.Clear();

            // Calculate progress for fly-in phase only (capped at 1.0)
            var flyInProgress = Math.Min(1.0f, (float)frame / flyInFrames);
            var easedProgress = EaseOutCubic(flyInProgress);

            // Draw animated background
            DrawAnimatedBackground(canvas, frame);

            // Update and draw particles (starfield effect)
            UpdateAndDrawParticles(canvas, frame);

            // Draw glowing orb behind text
            DrawGlowingOrb(canvas, frame, easedProgress);

            // Draw animated letters with fly-in effect
            DrawAnimatedLetters(canvas, letters, easedProgress, frame);

            // Draw scanning lines effect
            DrawScanLines(canvas, frame);

            await Task.Delay(frameDelay);
        }

        // Hold final state briefly
        await Task.Delay(300);
    }

    /// <summary>
    ///     Phase 2: Transition effect between splash and info
    /// </summary>
    private async Task ShowTransitionAsync(Canvas canvas)
    {
        Console.WriteLine("[INTRO] Playing transition...");

        const int transitionFrames = 20;
        const int frameDelay = 25;

        for (var frame = 0; frame < transitionFrames; frame++)
        {
            var progress = (float)frame / transitionFrames;

            // Wipe effect from center outward
            canvas.Clear();

            var wipeHeight = (int)(DisplayHeight * EaseInOutQuad(progress));
            var centerY = DisplayHeight / 2;

            // Draw contracting logo area
            if (progress < 0.5f)
            {
                var alpha = (byte)(255 * (1 - progress * 2));
                DrawStaticLogo(canvas, alpha);
            }

            // Draw expanding info area
            if (progress > 0.3f)
            {
                var infoProgress = (progress - 0.3f) / 0.7f;
                DrawExpandingInfoPreview(canvas, infoProgress);
            }

            await Task.Delay(frameDelay);
        }
    }

    /// <summary>
    ///     Phase 3: System info display with countdown
    /// </summary>
    private async Task ShowSystemInfoWithCountdownAsync(Canvas canvas)
    {
        Console.WriteLine("[INTRO] Showing system info...");

        // Animate system info appearing
        await AnimateSystemInfoAppearanceAsync(canvas);

        // Funny loading messages
        var funnyMessages = new[]
        {
            "Reticulating splines...",
            "Charging flux capacitor...",
            "Herding pixels...",
            "Convincing electrons...",
            "Warming up the LEDs...",
            "Bribing the CPU...",
            "Downloading more RAM...",
            "Polishing the bits...",
            "Untangling the cables...",
            "Feeding the hamsters...",
            "Calibrating photons...",
            "Negotiating with firewall...",
            "Compiling excuses...",
            "Generating witty text...",
            "Loading loading screen...",
            "Preparing awesomeness...",
            "Consulting the oracle...",
            "Waking up the server...",
            "Spinning up the disco ball...",
            "Teaching pixels to dance...",
            "Inflating the cloud...",
            "Summoning web demons...",
            "Aligning the stars...",
            "Brewing digital coffee...",
            "Greeting Chuck Norris - RIP...",
            "Extracting the unextracted...",
            "Throwing some sticky bombs...",
            "Warming up the DOOM engine..."
        };

        // Shuffle and pick messages for countdown
        var shuffled = funnyMessages.OrderBy(_ => _random.Next()).ToArray();

        // Countdown with funny messages
        var barHeight = _lineHeight + 2;
        var barY = DisplayHeight - barHeight;
        for (var i = CountdownSeconds; i > 0; i--)
        {
            // Clear the countdown line area before drawing new text
            canvas.DrawRect(0, barY, DisplayWidth, barHeight, SKColors.Black, SKPaintStyle.Fill);

            var messageIndex = (CountdownSeconds - i) % shuffled.Length;
            var statusText = shuffled[messageIndex];

            // Cycle through colors for fun
            var hue = (CountdownSeconds - i) * 40 % 360;
            var color = HsvToRgb(hue, 0.7f, 0.8f);

            canvas.DrawBdfText($"{statusText} {i}s", 1, barY, color);
            await Task.Delay(1000);
        }
    }

    /// <summary>
    ///     Animate system info lines appearing one by one
    /// </summary>
    private async Task AnimateSystemInfoAppearanceAsync(Canvas canvas)
    {
        canvas.Clear();

        // Calculate line positions dynamically based on display height
        var lineHeight = _lineHeight;
        var startY = 1;
        var gap = (int)(10 * _scale); // section gap, scaled to the panel

        var infoLines = new[]
        {
            (Text: "verpixeld", X: 1, Y: startY, Color: GetGradientColor(0)),
            (Text: "LED Matrix Control System", X: 1, Y: startY + lineHeight, Color: SKColors.Cyan),
            (Text: "(c) 2022-2026, Jan R. Wrage", X: 1, Y: startY + lineHeight * 2, Color: new SKColor(100, 100, 130)),
            (Text: "* Configuration *", X: 1, Y: startY + lineHeight * 3 + gap, Color: SKColors.DarkViolet),
            (Text: $"Resolution: {DisplayWidth}x{DisplayHeight}", X: 1, Y: startY + lineHeight * 4 + gap,
                Color: SKColors.DarkViolet),
            (Text: $"IP-Address: {GetLocalIPAddress()?.ToString() ?? "Unknown"}", X: 1, Y: startY + lineHeight * 5 + gap,
                Color: SKColors.DarkViolet),
            (Text: "Ready to rock!", X: 1, Y: startY + lineHeight * 6 + gap /** 2*/, Color: SKColors.LimeGreen)
        };

        // Animate each line appearing with a typewriter-like effect
        foreach (var line in infoLines)
        {
            // Draw all previous lines
            canvas.Clear();
            var currentIndex = Array.IndexOf(infoLines, line);

            for (var i = 0; i <= currentIndex; i++)
            {
                var prevLine = infoLines[i];

                // Special rendering for "verpixeld" title with gradient effect
                if (i == 0)
                    DrawVerpixeldTitle(canvas, prevLine.X, prevLine.Y, 1.0f);
                else
                    canvas.DrawBdfText(prevLine.Text, prevLine.X, prevLine.Y, prevLine.Color);
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    ///     Draw the verpixeld title with gradient colors
    /// </summary>
    private void DrawVerpixeldTitle(Canvas canvas, int x, int y, float alpha)
    {
        var colors = new[]
        {
            new SKColor(0, 200, 255), // Cyan (ver)
            new SKColor(0, 255, 200), // Teal
            new SKColor(100, 255, 100), // Green
            new SKColor(255, 0, 100), // Pink (pix)
            new SKColor(255, 100, 0), // Orange  
            new SKColor(255, 200, 0), // Yellow
            new SKColor(150, 50, 255), // Purple (eld)
            new SKColor(200, 0, 255), // Violet
            new SKColor(255, 0, 150) // Magenta
        };

        var chars = "verpixeld".ToCharArray();
        var xOffset = x;
        var charWidth = _charWidth; // Responsive BDF font char advance

        for (var i = 0; i < chars.Length; i++)
        {
            var color = colors[i % colors.Length];
            if (alpha < 1.0f)
                color = new SKColor(
                    (byte)(color.Red * alpha),
                    (byte)(color.Green * alpha),
                    (byte)(color.Blue * alpha)
                );
            canvas.DrawBdfText(chars[i].ToString(), xOffset, y, color);
            xOffset += charWidth;
        }
    }

    /// <summary>
    ///     Draw static logo for transition (dynamically centered)
    /// </summary>
    private void DrawStaticLogo(Canvas canvas, byte alpha)
    {
        const string logoText = "verpixeld";
        var charWidth = _charWidth;
        var totalWidth = logoText.Length * charWidth;
        var startX = (DisplayWidth - totalWidth) / 2;
        var y = DisplayHeight / 2 - (int)(10 * _scale);

        for (var i = 0; i < logoText.Length; i++)
        {
            var letterColor = GetGradientColor(i, alpha);
            canvas.DrawBdfText(logoText[i].ToString(), startX + i * charWidth, y, letterColor);
        }
    }

    /// <summary>
    ///     Draw expanding info preview during transition
    /// </summary>
    private void DrawExpandingInfoPreview(Canvas canvas, float progress)
    {
        var alpha = (byte)(255 * Math.Min(1, progress * 1.5f));
        /*BdfFontRegistry.DefaultFontName = "10x20"*/
        ;

        if (progress > 0.2f) DrawVerpixeldTitle(canvas, 1, 1, Math.Min(1, (progress - 0.2f) * 2));

        if (progress > 0.4f)
        {
            var textAlpha = Math.Min(1, (progress - 0.4f) * 2);
            var color = new SKColor(0, 255, 255, (byte)(textAlpha * 255));
            canvas.DrawBdfText("LED Matrix Control System", 1, _lineHeight, color);
        }
    }

    /// <summary>
    ///     Initialize particle system for starfield effect
    /// </summary>
    private void InitializeParticles(int count)
    {
        _particles.Clear();
        for (var i = 0; i < count; i++)
            _particles.Add(new Particle
            {
                X = _random.Next(DisplayWidth),
                Y = _random.Next(DisplayHeight),
                Z = _random.NextSingle() * 100,
                Speed = 0.5f + _random.NextSingle() * 2,
                Size = 1 + _random.Next(2),
                Color = GetStarColor()
            });
    }

    /// <summary>
    ///     Update and draw particles
    /// </summary>
    private void UpdateAndDrawParticles(Canvas canvas, int frame)
    {
        foreach (var p in _particles)
        {
            // Move particles toward viewer (z decreases)
            p.Z -= p.Speed;

            if (p.Z <= 0)
            {
                // Reset particle to far distance
                p.Z = 100;
                p.X = _random.Next(DisplayWidth);
                p.Y = _random.Next(DisplayHeight);
            }

            // Project 3D to 2D with perspective
            var scale = 100f / (p.Z + 1);
            var screenX = (int)(DisplayWidth / 2 + (p.X - DisplayWidth / 2) * scale * 0.5f);
            var screenY = (int)(DisplayHeight / 2 + (p.Y - DisplayHeight / 2) * scale * 0.5f);

            // Only draw if on screen
            if (screenX >= 0 && screenX < DisplayWidth && screenY >= 0 && screenY < DisplayHeight)
            {
                var brightness = (byte)(255 * (1 - p.Z / 100f));
                var color = new SKColor(
                    (byte)(p.Color.Red * brightness / 255),
                    (byte)(p.Color.Green * brightness / 255),
                    (byte)(p.Color.Blue * brightness / 255)
                );

                var size = Math.Max(1, (int)(p.Size * scale * 0.3f));
                canvas.DrawRect(screenX, screenY, size, size, color, SKPaintStyle.Fill);
            }
        }
    }

    /// <summary>
    ///     Draw animated background with subtle gradient
    /// </summary>
    private void DrawAnimatedBackground(Canvas canvas, int frame)
    {
        // Subtle animated gradient bars at edges
        var intensity = (byte)(20 + (int)(15 * Math.Sin(frame * 0.1)));

        // Top edge glow
        for (var y = 0; y < 10; y++)
        {
            var alpha = (byte)(intensity * (10 - y) / 10);
            var color = new SKColor(0, alpha, (byte)(alpha * 2));
            canvas.DrawRect(0, y, DisplayWidth, 1, color, SKPaintStyle.Fill);
        }

        // Bottom edge glow
        for (var y = 0; y < 10; y++)
        {
            var alpha = (byte)(intensity * (10 - y) / 10);
            var color = new SKColor((byte)(alpha * 2), 0, alpha);
            canvas.DrawRect(0, DisplayHeight - y - 1, DisplayWidth, 1, color, SKPaintStyle.Fill);
        }
    }

    /// <summary>
    ///     Draw glowing orb effect behind text (dynamically centered)
    /// </summary>
    private void DrawGlowingOrb(Canvas canvas, int frame, float progress)
    {
        var centerX = DisplayWidth / 2;
        var centerY = DisplayHeight / 2 - (int)(10 * _scale);

        // Pulsing glow
        var pulse = 0.7f + 0.3f * (float)Math.Sin(frame * 0.15);
        var maxRadius = (int)(80 * _scale * progress * pulse);

        // Draw multiple concentric circles for glow effect
        for (var r = maxRadius; r > 0; r -= 4)
        {
            var intensity = (byte)(60 * (1 - (float)r / maxRadius) * progress);
            var hue = (frame * 2 + r) % 360;
            var color = HsvToRgb(hue, 0.8f, intensity / 255f);

            // Draw glow ring
            DrawCircleOutline(canvas, centerX, centerY, r, color);
        }
    }

    /// <summary>
    ///     Draw animated letters with fly-in effect
    /// </summary>
    private void DrawAnimatedLetters(Canvas canvas, LetterInfo[] letters, float progress, int frame)
    {
        for (var i = 0; i < letters.Length; i++)
        {
            var letter = letters[i];

            // Stagger letters from center outward (middle letters appear first)
            var centerIndex = letters.Length / 2.0f;
            var distanceFromCenter = Math.Abs(i - centerIndex) / centerIndex;
            var letterDelay = distanceFromCenter * 0.25f; // Outer letters delayed slightly

            // Calculate individual letter progress
            var letterProgress = Math.Max(0, Math.Min(1, (progress - letterDelay) / (1.0f - letterDelay * 0.5f)));

            // Skip if letter hasn't started yet
            if (letterProgress <= 0) continue;

            // Use elastic easing for bouncy fly-in effect
            var easedProgress = EaseOutElastic(letterProgress);

            // Interpolate position - letters fly from their start positions to center
            var currentX = (int)(letter.StartX + (letter.TargetX - letter.StartX) * easedProgress);
            var currentY = (int)(letter.StartY + (letter.TargetY - letter.StartY) * easedProgress);

            // Get gradient color for this letter
            var baseColor = GetGradientColor(i);

            // Alpha fades in quickly at the start
            var alpha = Math.Min(1.0f, letterProgress * 2.5f);

            // Draw motion trail when letter is still moving
            if (letterProgress < 0.7f && letterProgress > 0.05f)
            {
                var trailAlpha = (byte)(alpha * 60 * (1 - letterProgress));
                var trailColor = new SKColor(baseColor.Red, baseColor.Green, baseColor.Blue, trailAlpha);

                // Draw trail copies behind the letter
                for (var t = 1; t <= 2; t++)
                {
                    var trailFactor = 0.12f * t;
                    var tx = currentX + (int)((letter.StartX - letter.TargetX) * trailFactor);
                    var ty = currentY + (int)((letter.StartY - letter.TargetY) * trailFactor);
                    canvas.DrawBdfText(letter.Char, tx, ty, trailColor);
                }
            }

            // Draw shadow when letter is mostly in place
            if (letterProgress > 0.5f)
            {
                var shadowAlpha = (byte)((letterProgress - 0.5f) * 2 * 120);
                var shadowColor = new SKColor(
                    (byte)(baseColor.Red / 6),
                    (byte)(baseColor.Green / 6),
                    (byte)(baseColor.Blue / 6),
                    shadowAlpha
                );
                canvas.DrawBdfText(letter.Char, currentX + 2, currentY + 2, shadowColor);
            }

            // Draw main letter with pulsing glow
            var glowPulse = 0.85f + 0.15f * (float)Math.Sin(frame * 0.12 + i * 0.5);
            var finalColor = new SKColor(
                (byte)(baseColor.Red * glowPulse),
                (byte)(baseColor.Green * glowPulse),
                (byte)(baseColor.Blue * glowPulse),
                (byte)(alpha * 255)
            );

            canvas.DrawBdfText(letter.Char, currentX, currentY, finalColor);
        }
    }

    /// <summary>
    ///     Elastic easing for bouncy fly-in effect
    /// </summary>
    private static float EaseOutElastic(float t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        const float c4 = 2 * (float)Math.PI / 3;
        return (float)Math.Pow(2, -10 * t) * (float)Math.Sin((t * 10 - 0.75) * c4) + 1;
    }

    /// <summary>
    ///     Draw scan lines effect
    /// </summary>
    private void DrawScanLines(Canvas canvas, int frame)
    {
        var scanY = frame * 4 % (DisplayHeight + 20) - 10;

        for (var i = -2; i <= 2; i++)
        {
            var y = scanY + i * 2;
            if (y >= 0 && y < DisplayHeight)
            {
                var intensity = (byte)(40 * (1 - Math.Abs(i) / 3f));
                var color = new SKColor(0, intensity, (byte)(intensity * 2));
                canvas.DrawRect(0, y, DisplayWidth, 1, color, SKPaintStyle.Fill);
            }
        }
    }

    /// <summary>
    ///     Draw circle outline using lines
    /// </summary>
    private void DrawCircleOutline(Canvas canvas, int cx, int cy, int radius, SKColor color)
    {
        if (radius < 2) return;

        const int segments = 32;
        for (var i = 0; i < segments; i++)
        {
            var angle1 = i * 2 * Math.PI / segments;
            var angle2 = (i + 1) * 2 * Math.PI / segments;

            var x1 = (int)(cx + radius * Math.Cos(angle1));
            var y1 = (int)(cy + radius * Math.Sin(angle1));
            var x2 = (int)(cx + radius * Math.Cos(angle2));
            var y2 = (int)(cy + radius * Math.Sin(angle2));

            canvas.DrawLine(x1, y1, x2, y2, color, 1);
        }
    }

    /// <summary>
    ///     Get gradient color for letter index (9 colors for "verpixeld")
    /// </summary>
    private static SKColor GetGradientColor(int index, byte alpha = 255)
    {
        var colors = new[]
        {
            new SKColor(0, 200, 255, alpha), // v - Cyan
            new SKColor(0, 255, 200, alpha), // e - Teal
            new SKColor(100, 255, 100, alpha), // r - Green
            new SKColor(255, 0, 127, alpha), // p - Hot pink
            new SKColor(255, 127, 0, alpha), // i - Orange
            new SKColor(255, 200, 0, alpha), // x - Yellow
            new SKColor(148, 0, 211, alpha), // e - Violet
            new SKColor(200, 0, 255, alpha), // l - Purple
            new SKColor(255, 20, 147, alpha) // d - Deep pink
        };
        return colors[index % colors.Length];
    }

    /// <summary>
    ///     Get random star color
    /// </summary>
    private SKColor GetStarColor()
    {
        var colors = new[]
        {
            SKColors.White,
            new SKColor(200, 220, 255), // Blue-white
            new SKColor(255, 200, 150), // Warm white
            new SKColor(100, 200, 255), // Cyan
            new SKColor(255, 150, 200) // Pink
        };
        return colors[_random.Next(colors.Length)];
    }

    /// <summary>
    ///     Convert HSV to RGB
    /// </summary>
    private static SKColor HsvToRgb(float h, float s, float v)
    {
        var hi = (int)(h / 60) % 6;
        var f = h / 60 - (int)(h / 60);
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);

        return hi switch
        {
            0 => new SKColor((byte)(v * 255), (byte)(t * 255), (byte)(p * 255)),
            1 => new SKColor((byte)(q * 255), (byte)(v * 255), (byte)(p * 255)),
            2 => new SKColor((byte)(p * 255), (byte)(v * 255), (byte)(t * 255)),
            3 => new SKColor((byte)(p * 255), (byte)(q * 255), (byte)(v * 255)),
            4 => new SKColor((byte)(t * 255), (byte)(p * 255), (byte)(v * 255)),
            _ => new SKColor((byte)(v * 255), (byte)(p * 255), (byte)(q * 255))
        };
    }

    // Easing functions for smooth animations
    private static float EaseOutCubic(float t)
    {
        return 1 - (float)Math.Pow(1 - t, 3);
    }

    private static float EaseInOutQuad(float t)
    {
        return t < 0.5f ? 2 * t * t : 1 - (float)Math.Pow(-2 * t + 2, 2) / 2;
    }

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1;
        return 1 + c3 * (float)Math.Pow(t - 1, 3) + c1 * (float)Math.Pow(t - 1, 2);
    }

    /// <summary>
    ///     Gets a local IPv4 address for splash / HA URLs.
    ///     Must not call <see cref="Dns.GetHostEntry"/> — that blocks for minutes (or forever)
    ///     in Docker/TrueNAS when the container hostname is not in DNS.
    /// </summary>
    public static IPAddress? GetLocalIPAddress()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
                return null;

            IPAddress? fallback = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip))
                        continue;
                    if (ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        continue;

                    fallback ??= ip;
                    // Prefer a LAN address over the Docker bridge (172.16/12 still used on some LANs).
                    if (!ip.ToString().StartsWith("172.", StringComparison.Ordinal))
                        return ip;
                }
            }

            return fallback;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NET] local IP scan failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Helper class for letter animation
    /// </summary>
    private class LetterInfo
    {
        public string Char { get; set; } = "";
        public int StartX { get; set; }
        public int StartY { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public float StartScale { get; set; }
    }

    /// <summary>
    ///     Helper class for particle system
    /// </summary>
    private class Particle
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Speed { get; set; }
        public int Size { get; set; }
        public SKColor Color { get; set; }
    }
}
