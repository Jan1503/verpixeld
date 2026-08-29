using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CanvasManagement;
using SkiaSharp;
using verpixeld.Configuration;
using verpixeld.Layout;

namespace verpixeld.Services;

/// <summary>
///     AI Image Generation service supporting Azure OpenAI (default) and OpenAI providers.
///     Provides text-to-image, image-to-image (stylize), generation history, and scheduled auto-generation.
/// </summary>
public class AiImageService
{
    private readonly DisplayLayoutManager _layoutManager;
    private readonly HttpClient _httpClient;
    private readonly string _configPath;
    private readonly string _historyPath;
    private readonly string _historyDir;
    private readonly string _galleryDir;
    private readonly object _lock = new();

    // Canvas overlay for displaying images above extensions
    private readonly CanvasManager _canvasManager;
    private Canvas? _imageOverlay;

    private Timer? _scheduleTimer;
    private readonly Random _random = new();

    // Configuration
    public string Provider { get; set; } = "azure"; // "azure" or "openai"

    // Azure OpenAI settings
    public string? AzureEndpoint { get; set; }
    public string? AzureApiKey { get; set; }
    public string? AzureDeployment { get; set; } // Image model deployment (e.g. gpt-image-1)
    public string AzureApiVersion { get; set; } = "2025-04-01-preview";

    // OpenAI settings
    public string? OpenAiApiKey { get; set; }
    public string OpenAiModel { get; set; } = "dall-e-3";

    // Schedule settings
    public bool ScheduleEnabled { get; set; }
    public int ScheduleIntervalMinutes { get; set; } = 60;
    public string ScheduleStyle { get; set; } = "pixel-art";
    public string ScheduleCanvasName { get; set; } = "Main";
    public bool ScheduleSaveToDisk { get; set; }
    public List<string> SchedulePrompts { get; set; } = new();
    public DateTime? ScheduleLastRunUtc { get; private set; }
    public string? ScheduleLastPrompt { get; private set; }
    public string? ScheduleLastError { get; private set; }
    public DateTime? ScheduleNextRunUtc { get; private set; }
    public string? ScheduleLastSkip { get; private set; }

    // State
    public bool IsConfigured => Provider == "azure"
        ? !string.IsNullOrEmpty(AzureEndpoint) && !string.IsNullOrEmpty(AzureApiKey) && !string.IsNullOrEmpty(AzureDeployment)
        : !string.IsNullOrEmpty(OpenAiApiKey);

    public bool IsGenerating { get; private set; }
    public bool HasImageOverlay => _imageOverlay != null;

    // History (in-memory + persisted)
    public List<AiGenerationRecord> History { get; private set; } = new();

    // Display dimensions
    private int _displayWidth = 384;
    private int _displayHeight = 192;
    public int DisplayWidth => _displayWidth;
    public int DisplayHeight => _displayHeight;

    // Style presets
    private static readonly Dictionary<string, string> StylePresets = new()
    {
        ["pixel-art"] = "Create a pixel art image with a limited color palette, suitable for a low-resolution LED matrix display ({w}x{h} pixels). Use distinct pixel-level details: ",
        ["retro-8bit"] = "Create a retro 8-bit style image reminiscent of classic video games, for a {w}x{h} pixel LED display. Use bright, distinct colors: ",
        ["neon-synthwave"] = "Create a neon synthwave/retrowave style image with glowing neon colors, purple/pink gradients, and geometric shapes for a {w}x{h} LED matrix: ",
        ["abstract"] = "Create an abstract art piece with bold colors and geometric patterns, optimized for a {w}x{h} pixel LED matrix display: ",
        ["photograph"] = "Create a photorealistic image, scaled down to look great on a {w}x{h} pixel LED matrix display: ",
        ["watercolor"] = "Create a watercolor painting style image with soft flowing colors, for a {w}x{h} pixel display: ",
        ["oil-painting"] = "Create an oil painting style image with rich textures and bold brushstrokes, for a {w}x{h} display: ",
        ["comic"] = "Create a comic book / pop art style image with bold outlines, halftone dots, and vivid colors for a {w}x{h} LED display: ",
        ["minimalist"] = "Create a minimalist image with simple shapes and limited colors, optimized for a {w}x{h} pixel LED matrix: ",
        ["cyberpunk"] = "Create a cyberpunk style image with neon lights, dark atmosphere, and futuristic elements for a {w}x{h} LED display: ",
        ["sketch"] = "Create a pencil sketch style image with fine line work, for display on a {w}x{h} pixel LED matrix: ",
    };

    public AiImageService(DisplayLayoutManager layoutManager, CanvasManager canvasManager, int displayWidth, int displayHeight)
    {
        _layoutManager = layoutManager;
        _canvasManager = canvasManager;
        _displayWidth = displayWidth;
        _displayHeight = displayHeight;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _configPath = AppPaths.AiConfig;
        _historyPath = AppPaths.AiHistory;
        _historyDir = AppPaths.AiHistoryDir;
        _galleryDir = AppPaths.GalleryDir;
        Directory.CreateDirectory(_historyDir);
        LoadConfig();
        LoadHistory();
        Console.WriteLine($"[AI] Initialized with display size {_displayWidth}x{_displayHeight}");
        if (ScheduleEnabled)
            StartScheduleTimer();
    }

    /// <summary>
    ///     Generate an image from a text prompt.
    /// </summary>
    public async Task<AiGenerationResult> GenerateImageAsync(string prompt, string style = "", string quality = "medium")
    {
        if (!IsConfigured)
            return new AiGenerationResult { Success = false, Error = "AI provider not configured. Set up API keys in AI Art settings." };

        if (string.IsNullOrWhiteSpace(prompt))
            return new AiGenerationResult { Success = false, Error = "Prompt is required." };

        if (IsGenerating)
            return new AiGenerationResult { Success = false, Error = "A generation is already in progress." };

        IsGenerating = true;
        try
        {
            var fullPrompt = BuildPrompt(prompt, style);
            Console.WriteLine($"[AI] Generating image: \"{prompt}\" (style={style}, quality={quality})");

            var base64Image = Provider == "azure"
                ? await GenerateWithAzureAsync(fullPrompt, quality)
                : await GenerateWithOpenAiAsync(fullPrompt, quality);

            var pngBytes = AiImageProcessing.ScaleToDisplayPng(
                Convert.FromBase64String(base64Image), _displayWidth, _displayHeight, style);
            var scaledBase64 = Convert.ToBase64String(pngBytes);

            var record = new AiGenerationRecord
            {
                Id = Guid.NewGuid().ToString(),
                Prompt = prompt,
                Style = style,
                Quality = quality,
                Provider = Provider,
                CreatedAt = DateTime.UtcNow.ToString("o")
            };
            AddToHistory(record, pngBytes);

            Console.WriteLine($"[AI] Image generated successfully: {record.Id}");
            return new AiGenerationResult { Success = true, ImageBase64 = scaledBase64, Record = record };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Generation failed: {ex.Message}");
            return new AiGenerationResult { Success = false, Error = ex.Message };
        }
        finally
        {
            IsGenerating = false;
        }
    }

    /// <summary>
    ///     Image-to-image: stylize an uploaded image using AI.
    /// </summary>
    public async Task<AiGenerationResult> EditImageAsync(string imageBase64, string prompt, string style = "")
    {
        if (!IsConfigured)
            return new AiGenerationResult { Success = false, Error = "AI provider not configured." };

        if (IsGenerating)
            return new AiGenerationResult { Success = false, Error = "A generation is already in progress." };

        IsGenerating = true;
        try
        {
            var stylePrompt = !string.IsNullOrEmpty(style) && StylePresets.TryGetValue(style, out var prefix)
                ? prefix.Replace("{w}", _displayWidth.ToString()).Replace("{h}", _displayHeight.ToString())
                : "";

            var fullPrompt = string.IsNullOrWhiteSpace(prompt)
                ? $"{stylePrompt}Transform this image in the described style."
                : $"{stylePrompt}{prompt}";

            Console.WriteLine($"[AI] Editing image: \"{prompt}\" (style={style})");

            string resultBase64;

            if (Provider == "azure")
            {
                // Azure OpenAI with gpt-image-1 supports edits endpoint
                resultBase64 = await EditWithAzureAsync(imageBase64, fullPrompt);
            }
            else
            {
                // OpenAI edits endpoint
                resultBase64 = await EditWithOpenAiAsync(imageBase64, fullPrompt);
            }

            var pngBytes = AiImageProcessing.ScaleToDisplayPng(
                Convert.FromBase64String(resultBase64), _displayWidth, _displayHeight, style);
            var scaledBase64 = Convert.ToBase64String(pngBytes);

            var record = new AiGenerationRecord
            {
                Id = Guid.NewGuid().ToString(),
                Prompt = prompt,
                Style = style,
                IsEdit = true,
                Provider = Provider,
                CreatedAt = DateTime.UtcNow.ToString("o")
            };
            AddToHistory(record, pngBytes);

            Console.WriteLine($"[AI] Image edit completed: {record.Id}");
            return new AiGenerationResult { Success = true, ImageBase64 = scaledBase64, Record = record };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Edit failed: {ex.Message}");
            return new AiGenerationResult { Success = false, Error = ex.Message };
        }
        finally
        {
            IsGenerating = false;
        }
    }

    /// <summary>
    ///     Apply a base64 image to an overlay canvas above the current extension.
    ///     Uses a dedicated overlay canvas at z-order 250 so images remain visible
    ///     even when an extension is actively drawing on the main canvas.
    /// </summary>
    public bool ApplyToCanvas(string base64Image, string canvasName = "Main")
    {
        try
        {
            var imageBytes = Convert.FromBase64String(base64Image);
            using var bitmap = SKBitmap.Decode(imageBytes);
            if (bitmap == null) return false;

            var x = 0;
            var y = 0;
            var w = _displayWidth;
            var h = _displayHeight;
            if (_canvasManager != null && !string.IsNullOrWhiteSpace(canvasName))
            {
                var target = _canvasManager.GetCanvasByName(canvasName);
                if (target != null)
                {
                    x = target.XPos;
                    y = target.YPos;
                    w = Math.Max(1, target.Width);
                    h = Math.Max(1, target.Height);
                }
                else
                    Console.WriteLine($"[AI] Canvas '{canvasName}' not found — using full display overlay");
            }

            using var fitted = AiImageProcessing.ScaleToSize(bitmap, w, h);

            if (_canvasManager != null)
            {
                DismissImageOverlay();
                _imageOverlay = _canvasManager.GetCanvas(x, y, w, h, 250, "AiImageOverlay");
                _imageOverlay.Show();
                _imageOverlay.DrawBitmap(fitted, 0, 0, fitted.Width, fitted.Height);
                Console.WriteLine($"[AI] Image applied to overlay on '{canvasName}' at {x},{y} {w}x{h} z=250");
                return true;
            }

            var canvas = _layoutManager.GetCanvas(canvasName);
            if (canvas == null)
            {
                Console.WriteLine($"[AI] Canvas '{canvasName}' not found");
                return false;
            }

            canvas.DrawBitmap(fitted, 0, 0, fitted.Width, fitted.Height);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Failed to apply to canvas: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Dismiss the image overlay canvas, allowing the extension underneath to be visible again.
    /// </summary>
    public void DismissImageOverlay()
    {
        if (_imageOverlay != null && _canvasManager != null)
        {
            _imageOverlay.Clear();
            _imageOverlay.Hide();
            _canvasManager.RemoveCanvas(_imageOverlay);
            _imageOverlay = null;
            Console.WriteLine("[AI] Image overlay dismissed");
        }
    }

    #region Azure OpenAI

    private async Task<string> GenerateWithAzureAsync(string prompt, string quality)
    {
        var url = $"{AzureEndpoint!.TrimEnd('/')}/openai/deployments/{AzureDeployment}/images/generations?api-version={AzureApiVersion}";

        var body = new
        {
            prompt,
            n = 1,
            size = "1024x1024",
            quality = MapQuality(quality),
            output_format = "png"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", AzureApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[AI/Azure] Error {response.StatusCode}: {responseBody}");
            throw new Exception(AiImageProcessing.FriendlyHttpError((int)response.StatusCode, responseBody, "Azure OpenAI"));
        }

        return ExtractImageFromResponse(responseBody);
    }

    private async Task<string> EditWithAzureAsync(string imageBase64, string prompt)
    {
        var url = $"{AzureEndpoint!.TrimEnd('/')}/openai/deployments/{AzureDeployment}/images/edits?api-version={AzureApiVersion}";

        // Azure edit endpoint uses multipart form data
        var imageBytes = Convert.FromBase64String(imageBase64);
        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "image", "input.png");
        content.Add(new StringContent(prompt), "prompt");
        content.Add(new StringContent("1024x1024"), "size");
        content.Add(new StringContent("1"), "n");
        content.Add(new StringContent("medium"), "quality");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", AzureApiKey);
        request.Content = content;

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[AI/Azure] Edit error {response.StatusCode}: {responseBody}");
            throw new Exception(AiImageProcessing.FriendlyHttpError((int)response.StatusCode, responseBody, "Azure OpenAI"));
        }

        return ExtractImageFromResponse(responseBody);
    }

    #endregion

    #region OpenAI

    private async Task<string> GenerateWithOpenAiAsync(string prompt, string quality)
    {
        var url = "https://api.openai.com/v1/images/generations";

        var bodyObj = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["model"] = OpenAiModel,
            ["n"] = 1,
            ["size"] = "1024x1024",
            ["quality"] = MapQuality(quality)
        };

        // DALL-E 3 and older return URLs by default; GPT image models return b64
        if (OpenAiModel.StartsWith("dall-e"))
            bodyObj["response_format"] = "b64_json";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OpenAiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(bodyObj), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[AI/OpenAI] Error {response.StatusCode}: {responseBody}");
            throw new Exception(AiImageProcessing.FriendlyHttpError((int)response.StatusCode, responseBody, "OpenAI"));
        }

        return ExtractImageFromResponse(responseBody);
    }

    private async Task<string> EditWithOpenAiAsync(string imageBase64, string prompt)
    {
        var url = "https://api.openai.com/v1/images/edits";

        var imageBytes = Convert.FromBase64String(imageBase64);
        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "image[]", "input.png");
        content.Add(new StringContent(prompt), "prompt");
        content.Add(new StringContent(OpenAiModel.StartsWith("dall-e") ? "dall-e-2" : OpenAiModel), "model");
        content.Add(new StringContent("1024x1024"), "size");
        content.Add(new StringContent("1"), "n");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OpenAiApiKey);
        request.Content = content;

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[AI/OpenAI] Edit error {response.StatusCode}: {responseBody}");
            throw new Exception(AiImageProcessing.FriendlyHttpError((int)response.StatusCode, responseBody, "OpenAI"));
        }

        return ExtractImageFromResponse(responseBody);
    }

    #endregion

    #region Response Parsing

    private static string ExtractImageFromResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var first = data[0];

        // Try b64_json first (GPT image models, DALL-E with b64 format)
        if (first.TryGetProperty("b64_json", out var b64))
            return b64.GetString()!;

        // Try URL (DALL-E 3 default)
        if (first.TryGetProperty("url", out var urlEl))
        {
            var imageUrl = urlEl.GetString()!;
            // Download the image
            using var client = new HttpClient();
            var bytes = client.GetByteArrayAsync(imageUrl).GetAwaiter().GetResult();
            return Convert.ToBase64String(bytes);
        }

        throw new Exception("No image data found in API response");
    }

    private static string MapQuality(string quality) => quality switch
    {
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        _ => "medium"
    };

    #endregion

    #region Prompt Building

    private string BuildPrompt(string userPrompt, string style)
    {
        if (string.IsNullOrEmpty(style) || !StylePresets.TryGetValue(style, out var prefix))
            return userPrompt;

        var styledPrefix = prefix
            .Replace("{w}", _displayWidth.ToString())
            .Replace("{h}", _displayHeight.ToString());

        return styledPrefix + userPrompt;
    }

    #endregion

    #region History

    private void AddToHistory(AiGenerationRecord record, byte[] pngBytes)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_historyDir);
            var filename = record.Id + ".png";
            FileHelper.AtomicWriteAllBytes(Path.Combine(_historyDir, filename), pngBytes);
            record.Filename = filename;
            record.ThumbnailBase64 = null;
            History.Insert(0, record);
            if (History.Count > 50)
            {
                var removed = History.Skip(50).ToList();
                History.RemoveRange(50, History.Count - 50);
                foreach (var old in removed)
                    TryDeleteHistoryFile(old);
            }
            SaveHistory();
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            foreach (var record in History)
                TryDeleteHistoryFile(record);
            History.Clear();
            SaveHistory();
        }
    }

    public byte[]? GetHistoryImageBytes(string id)
    {
        var safeId = Path.GetFileNameWithoutExtension(Path.GetFileName(id));
        AiGenerationRecord? record;
        lock (_lock)
            record = History.FirstOrDefault(h => h.Id == safeId);
        if (record == null) return null;

        var filename = Path.GetFileName(record.Filename ?? safeId + ".png");
        var filePath = Path.Combine(_historyDir, filename);
        if (!File.Exists(filePath)) return null;
        return File.ReadAllBytes(filePath);
    }

    private void TryDeleteHistoryFile(AiGenerationRecord record)
    {
        try
        {
            var filename = Path.GetFileName(record.Filename ?? record.Id + ".png");
            var filePath = Path.Combine(_historyDir, filename);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch { }
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                var json = File.ReadAllText(_historyPath);
                History = JsonSerializer.Deserialize<List<AiGenerationRecord>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                var migrated = MigrateHistoryThumbnails();
                Console.WriteLine($"[AI] Loaded {History.Count} history entries");
                if (migrated)
                    SaveHistory();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Failed to load history: {ex.Message}");
            History = new();
        }
    }

    /// <summary>
    ///     Older builds stored full PNG base64 in ai_history.json. Write those
    ///     out as files and drop the payload so the JSON stays small.
    /// </summary>
    private bool MigrateHistoryThumbnails()
    {
        Directory.CreateDirectory(_historyDir);
        var dirty = false;
        foreach (var record in History)
        {
            if (string.IsNullOrEmpty(record.Id))
            {
                record.Id = Guid.NewGuid().ToString();
                dirty = true;
            }

            if (!string.IsNullOrEmpty(record.ThumbnailBase64) && string.IsNullOrEmpty(record.Filename))
            {
                try
                {
                    var filename = record.Id + ".png";
                    FileHelper.AtomicWriteAllBytes(Path.Combine(_historyDir, filename),
                        Convert.FromBase64String(record.ThumbnailBase64));
                    record.Filename = filename;
                    dirty = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AI] Failed to migrate history {record.Id}: {ex.Message}");
                }
            }

            if (record.ThumbnailBase64 != null)
            {
                record.ThumbnailBase64 = null;
                dirty = true;
            }
        }

        return dirty;
    }

    private void SaveHistory()
    {
        try
        {
            var json = JsonSerializer.Serialize(History, new JsonSerializerOptions { WriteIndented = true });
            FileHelper.AtomicWriteAllText(_historyPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Failed to save history: {ex.Message}");
        }
    }

    #endregion

    #region Schedule

    public void ConfigureSchedule(bool enabled, int intervalMinutes, string style, string canvasName, bool saveToDisk, List<string> prompts)
    {
        ScheduleEnabled = enabled;
        ScheduleIntervalMinutes = Math.Clamp(intervalMinutes, 5, 1440);
        ScheduleStyle = style;
        ScheduleCanvasName = canvasName;
        ScheduleSaveToDisk = saveToDisk;
        SchedulePrompts = prompts;

        SaveConfig();

        if (enabled && prompts.Count > 0)
            StartScheduleTimer();
        else
            StopScheduleTimer();

        Console.WriteLine($"[AI] Schedule {(enabled ? "enabled" : "disabled")}: every {ScheduleIntervalMinutes}min, {prompts.Count} prompts");
    }

    private void StartScheduleTimer()
    {
        StopScheduleTimer();
        var interval = TimeSpan.FromMinutes(ScheduleIntervalMinutes);
        ScheduleNextRunUtc = DateTime.UtcNow.Add(interval);
        ScheduleLastSkip = null;
        _scheduleTimer = new Timer(OnScheduleTick, null, interval, interval);
        Console.WriteLine($"[AI] Schedule timer started: every {ScheduleIntervalMinutes} minutes");
    }

    private void StopScheduleTimer()
    {
        _scheduleTimer?.Dispose();
        _scheduleTimer = null;
        ScheduleNextRunUtc = null;
    }

    private async void OnScheduleTick(object? state)
    {
        if (!ScheduleEnabled || SchedulePrompts.Count == 0)
            return;

        if (IsGenerating)
        {
            ScheduleLastSkip = "Skipped: a generation is already in progress.";
            ScheduleNextRunUtc = DateTime.UtcNow.AddMinutes(ScheduleIntervalMinutes);
            Console.WriteLine($"[AI/Schedule] {ScheduleLastSkip}");
            return;
        }

        await RunScheduledGenerationAsync();
    }

    /// <summary>
    ///     Fire one scheduled generation immediately (does not require the timer to be enabled).
    /// </summary>
    public Task<AiGenerationResult> RunScheduleNowAsync()
    {
        if (SchedulePrompts.Count == 0)
            return Task.FromResult(new AiGenerationResult { Success = false, Error = "Add at least one prompt for auto-generate." });
        if (IsGenerating)
            return Task.FromResult(new AiGenerationResult { Success = false, Error = "A generation is already in progress." });
        return RunScheduledGenerationAsync();
    }

    private async Task<AiGenerationResult> RunScheduledGenerationAsync()
    {
        ScheduleLastSkip = null;
        try
        {
            var prompt = SchedulePrompts[_random.Next(SchedulePrompts.Count)];
            var style = ScheduleStyle == "random"
                ? StylePresets.Keys.ElementAt(_random.Next(StylePresets.Count))
                : ScheduleStyle;

            Console.WriteLine($"[AI/Schedule] Auto-generating: \"{prompt}\" (style={style})");

            var result = await GenerateImageAsync(prompt, style);
            ScheduleLastRunUtc = DateTime.UtcNow;
            ScheduleLastPrompt = prompt;
            ScheduleNextRunUtc = ScheduleEnabled
                ? DateTime.UtcNow.AddMinutes(ScheduleIntervalMinutes)
                : null;

            if (result.Success && result.ImageBase64 != null)
            {
                ApplyToCanvas(result.ImageBase64, ScheduleCanvasName);
                ScheduleLastError = null;
                Console.WriteLine("[AI/Schedule] Image applied to display");

                if (ScheduleSaveToDisk)
                    SaveImageToDisk(result.ImageBase64, prompt, style);
            }
            else
            {
                ScheduleLastError = result.Error ?? "Generation failed";
                Console.WriteLine($"[AI/Schedule] Failed: {result.Error}");
            }

            SaveConfig();
            return result;
        }
        catch (Exception ex)
        {
            ScheduleLastRunUtc = DateTime.UtcNow;
            ScheduleLastError = ex.Message;
            ScheduleNextRunUtc = ScheduleEnabled
                ? DateTime.UtcNow.AddMinutes(ScheduleIntervalMinutes)
                : null;
            SaveConfig();
            Console.WriteLine($"[AI/Schedule] Error: {ex.Message}");
            return new AiGenerationResult { Success = false, Error = ex.Message };
        }
    }

    public string? SaveImageToDisk(string base64Image, string prompt, string style) =>
        SaveToGallery(base64Image, prompt, style).Filename;

    public GallerySaveResult SaveToGallery(string base64Image, string prompt, string style, bool force = false)
    {
        try
        {
            Directory.CreateDirectory(_galleryDir);
            var imageBytes = Convert.FromBase64String(base64Image);
            var hash = AiImageProcessing.ContentHashHex(imageBytes);
            EnsureGalleryHashes();

            if (!force && _galleryHashes!.TryGetValue(hash, out var existing))
            {
                Console.WriteLine($"[AI] Gallery already has this image: {existing}");
                return new GallerySaveResult { Filename = existing, AlreadyExists = true };
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safePrompt = new string(prompt.Take(40).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            if (string.IsNullOrEmpty(safePrompt)) safePrompt = "generated";
            var filename = $"{timestamp}_{style}_{safePrompt}.png";
            var filePath = Path.Combine(_galleryDir, filename);

            File.WriteAllBytes(filePath, imageBytes);
            _galleryHashes![hash] = filename;
            Console.WriteLine($"[AI] Image saved to: {filePath}");
            return new GallerySaveResult { Filename = filename };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Failed to save image: {ex.Message}");
            return new GallerySaveResult();
        }
    }

    private Dictionary<string, string>? _galleryHashes; // sha256 -> filename

    private void EnsureGalleryHashes()
    {
        if (_galleryHashes != null) return;
        _galleryHashes = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_galleryDir)) return;
        foreach (var file in Directory.GetFiles(_galleryDir, "*.png"))
        {
            try
            {
                var hash = AiImageProcessing.ContentHashHex(File.ReadAllBytes(file));
                _galleryHashes[hash] = Path.GetFileName(file);
            }
            catch { /* skip unreadable files */ }
        }
    }

    private void ForgetGalleryHash(string filename)
    {
        if (_galleryHashes == null) return;
        var keys = _galleryHashes.Where(kv => kv.Value == filename).Select(kv => kv.Key).ToList();
        foreach (var key in keys)
            _galleryHashes.Remove(key);
    }

    /// <summary>
    ///     Get all saved images from the gallery folder.
    /// </summary>
    public List<object> GetGalleryFiles()
    {
        try
        {
            if (!Directory.Exists(_galleryDir)) return new();
            return Directory.GetFiles(_galleryDir, "*.png")
                .OrderByDescending(f => File.GetCreationTime(f))
                .Select(f => (object)new
                {
                    filename = Path.GetFileName(f),
                    createdAt = File.GetCreationTime(f).ToString("o"),
                    sizeKb = new FileInfo(f).Length / 1024
                })
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>
    ///     Get a saved gallery image as base64.
    /// </summary>
    public string? GetGalleryImage(string filename)
    {
        try
        {
            var safeName = Path.GetFileName(filename); // prevent path traversal
            var filePath = Path.Combine(_galleryDir, safeName);
            if (!File.Exists(filePath)) return null;
            return Convert.ToBase64String(File.ReadAllBytes(filePath));
        }
        catch { return null; }
    }

    /// <summary>
    ///     Delete a gallery image.
    /// </summary>
    public bool DeleteGalleryImage(string filename)
    {
        try
        {
            var safeName = Path.GetFileName(filename);
            var filePath = Path.Combine(_galleryDir, safeName);
            if (!File.Exists(filePath)) return false;
            File.Delete(filePath);
            ForgetGalleryHash(safeName);
            Console.WriteLine($"[AI] Gallery image deleted: {safeName}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Failed to delete gallery image: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Get gallery image count.
    /// </summary>
    public int GalleryCount
    {
        get
        {
            try { return Directory.Exists(_galleryDir) ? Directory.GetFiles(_galleryDir, "*.png").Length : 0; }
            catch { return 0; }
        }
    }

    #endregion

    #region Configuration Persistence

    public void Configure(string provider, string? azureEndpoint, string? azureApiKey,
        string? azureDeployment, string? azureApiVersion,
        string? openAiApiKey, string? openAiModel)
    {
        Provider = provider;

        if (azureEndpoint != null) AzureEndpoint = azureEndpoint;
        if (azureApiKey != null) AzureApiKey = azureApiKey;
        if (azureDeployment != null) AzureDeployment = azureDeployment;
        if (azureApiVersion != null) AzureApiVersion = azureApiVersion;

        if (openAiApiKey != null) OpenAiApiKey = openAiApiKey;
        if (openAiModel != null) OpenAiModel = openAiModel;

        SaveConfig();
        Console.WriteLine($"[AI] Configured: provider={Provider}, configured={IsConfigured}");
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<AiConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config != null)
                {
                    Provider = config.Provider ?? "azure";
                    AzureEndpoint = config.AzureEndpoint;
                    AzureApiKey = config.AzureApiKey;
                    AzureDeployment = config.AzureDeployment;
                    AzureApiVersion = config.AzureApiVersion ?? "2025-04-01-preview";
                    OpenAiApiKey = config.OpenAiApiKey;
                    OpenAiModel = config.OpenAiModel ?? "dall-e-3";
                    ScheduleEnabled = config.ScheduleEnabled;
                    ScheduleIntervalMinutes = config.ScheduleIntervalMinutes > 0 ? config.ScheduleIntervalMinutes : 60;
                    ScheduleStyle = config.ScheduleStyle ?? "pixel-art";
                    ScheduleCanvasName = config.ScheduleCanvasName ?? "Main";
                    ScheduleSaveToDisk = config.ScheduleSaveToDisk;
                    SchedulePrompts = config.SchedulePrompts ?? new();
                    ScheduleLastRunUtc = config.ScheduleLastRunUtc;
                    ScheduleLastPrompt = config.ScheduleLastPrompt;
                    ScheduleLastError = config.ScheduleLastError;
                    Console.WriteLine($"[AI] Config loaded: provider={Provider}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Failed to load config: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var config = new AiConfig
            {
                Provider = Provider,
                AzureEndpoint = AzureEndpoint,
                AzureApiKey = AzureApiKey,
                AzureDeployment = AzureDeployment,
                AzureApiVersion = AzureApiVersion,
                OpenAiApiKey = OpenAiApiKey,
                OpenAiModel = OpenAiModel,
                ScheduleEnabled = ScheduleEnabled,
                ScheduleIntervalMinutes = ScheduleIntervalMinutes,
                ScheduleStyle = ScheduleStyle,
                ScheduleCanvasName = ScheduleCanvasName,
                ScheduleSaveToDisk = ScheduleSaveToDisk,
                SchedulePrompts = SchedulePrompts,
                ScheduleLastRunUtc = ScheduleLastRunUtc,
                ScheduleLastPrompt = ScheduleLastPrompt,
                ScheduleLastError = ScheduleLastError
            };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            FileHelper.AtomicWriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Failed to save config: {ex.Message}");
        }
    }

    #endregion

    #region Models

    private class AiConfig
    {
        public string? Provider { get; set; }
        public string? AzureEndpoint { get; set; }
        public string? AzureApiKey { get; set; }
        public string? AzureDeployment { get; set; }
        public string? AzureApiVersion { get; set; }
        public string? OpenAiApiKey { get; set; }
        public string? OpenAiModel { get; set; }
        public bool ScheduleEnabled { get; set; }
        public int ScheduleIntervalMinutes { get; set; }
        public string? ScheduleStyle { get; set; }
        public string? ScheduleCanvasName { get; set; }
        public bool ScheduleSaveToDisk { get; set; }
        public List<string>? SchedulePrompts { get; set; }
        public DateTime? ScheduleLastRunUtc { get; set; }
        public string? ScheduleLastPrompt { get; set; }
        public string? ScheduleLastError { get; set; }
    }

    #endregion
}

public class AiGenerationRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("style")]
    public string Style { get; set; } = "";

    [JsonPropertyName("quality")]
    public string Quality { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("isEdit")]
    public bool IsEdit { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("thumbnailBase64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThumbnailBase64 { get; set; }
}

public class AiGenerationResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ImageBase64 { get; set; }
    public AiGenerationRecord? Record { get; set; }
}

public class GallerySaveResult
{
    public string? Filename { get; set; }
    public bool AlreadyExists { get; set; }
}
