using System.Text.Json;

namespace verpixeld.Services;

/// <summary>
///     Routes voice commands through an LLM to classify intent and generate a spoken response.
///     Uses Azure OpenAI chat completions via AiChatService.
/// </summary>
public class VoiceCommandRouter
{
    private readonly AiChatService _chatService;
    private readonly Func<VoiceContext> _contextProvider;

    public VoiceCommandRouter(AiChatService chatService, Func<VoiceContext> contextProvider)
    {
        _chatService = chatService;
        _contextProvider = contextProvider;
    }

    /// <summary>
    ///     Classify user speech into an intent and generate a spoken response.
    /// </summary>
    public async Task<VoiceCommandResult> ClassifyAsync(string transcription)
    {
        if (!_chatService.IsConfigured)
        {
            Console.WriteLine("[ROUTER] Chat not configured, falling back to image generation");
            return new VoiceCommandResult
            {
                Intent = VoiceIntents.GenerateImage,
                Response = "",
                Action = new Dictionary<string, JsonElement>
                {
                    ["prompt"] = JsonSerializer.SerializeToElement(transcription)
                }
            };
        }

        var context = _contextProvider();
        var systemPrompt = BuildSystemPrompt(context);

        Console.WriteLine($"[ROUTER] Classifying: \"{transcription}\"");
        var llmResponse = await _chatService.GetChatCompletionAsync(systemPrompt, transcription, 200);

        // Handle content filtering — Azure may block the classification request
        if (llmResponse == "CONTENT_FILTERED")
        {
            Console.WriteLine("[ROUTER] Content filter triggered — attempting local fallback");
            return ContentFilterFallback(transcription);
        }

        if (string.IsNullOrEmpty(llmResponse))
        {
            // Retry once on empty response (transient Azure error)
            Console.WriteLine("[ROUTER] LLM returned empty — retrying once...");
            await Task.Delay(500);
            llmResponse = await _chatService.GetChatCompletionAsync(systemPrompt, transcription, 200);

            if (llmResponse == "CONTENT_FILTERED")
            {
                Console.WriteLine("[ROUTER] Content filter triggered on retry — attempting local fallback");
                return ContentFilterFallback(transcription);
            }

            if (string.IsNullOrEmpty(llmResponse))
            {
                // Empty response after retry — often caused by Azure content filtering
                // returning null instead of a proper content_filter finish reason.
                // Try local fallback before giving up.
                Console.WriteLine("[ROUTER] LLM returned empty after retry — attempting local fallback");
                return ContentFilterFallback(transcription);
            }
        }

        try
        {
            // Strip markdown code fences if present
            var cleaned = llmResponse.Trim();
            if (cleaned.StartsWith("```"))
            {
                var firstNewline = cleaned.IndexOf('\n');
                if (firstNewline > 0) cleaned = cleaned[(firstNewline + 1)..];
                if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
                cleaned = cleaned.Trim();
            }

            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var intent = root.TryGetProperty("intent", out var i) ? i.GetString() ?? VoiceIntents.Unknown : VoiceIntents.Unknown;
            var response = SanitizeText(root.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "");
            var action = new Dictionary<string, JsonElement>();

            if (root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in a.EnumerateObject())
                    action[prop.Name] = prop.Value.Clone();
            }

            Console.WriteLine($"[ROUTER] Intent: {intent}, Response: \"{response}\"");
            return new VoiceCommandResult { Intent = intent, Response = response, Action = action };
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[ROUTER] Failed to parse LLM JSON: {ex.Message}");
            Console.WriteLine($"[ROUTER] Raw LLM response: {llmResponse}");

            // If parsing fails, treat as a question — the LLM response might be plain text
            return new VoiceCommandResult
            {
                Intent = VoiceIntents.Question,
                Response = llmResponse.Trim(),
                Action = new Dictionary<string, JsonElement>()
            };
        }
    }

    /// <summary>
    ///     When Azure's content filter blocks the classification request, try to infer the intent locally.
    ///     This typically happens with "male"/"zeichne"/"erstelle" (draw) commands where the filter
    ///     misinterprets the German text.
    /// </summary>
    private static VoiceCommandResult ContentFilterFallback(string transcription)
    {
        var lower = transcription.ToLowerInvariant();

        // Check for image generation keywords (German: male, zeichne, erstelle ein Bild, etc.)
        var drawKeywords = new[] { "male ", "mal ", "zeichne ", "erstelle ein bild", "generiere ein bild", "paint ", "draw " };
        if (drawKeywords.Any(k => lower.StartsWith(k) || lower.Contains(k)))
        {
            Console.WriteLine($"[ROUTER] Content filter fallback → generate-image: \"{transcription}\"");
            return new VoiceCommandResult
            {
                Intent = VoiceIntents.GenerateImage,
                Response = "Okay, ich male das für dich!",
                Action = new Dictionary<string, JsonElement>
                {
                    ["prompt"] = JsonDocument.Parse($"\"{transcription}\"").RootElement.Clone()
                }
            };
        }

        // Default: tell the user to try again
        Console.WriteLine($"[ROUTER] Content filter fallback → unknown (no local match)");
        return new VoiceCommandResult
        {
            Intent = VoiceIntents.Unknown,
            Response = "Die Anfrage wurde leider blockiert. Bitte versuche es mit anderen Worten.",
            Action = new Dictionary<string, JsonElement>()
        };
    }

    private static string BuildSystemPrompt(VoiceContext ctx)
    {
        // Keep extensions list short to reduce token count
        var extensions = ctx.AvailableExtensions ?? "unknown";
        if (extensions.Length > 150)
            extensions = extensions[..150] + "...";

        return $@"You are Pixel, a voice assistant for an LED matrix display. Respond in the user's language. Return ONLY JSON:
{{""intent"":""<intent>"",""response"":""<short spoken reply>"",""action"":{{}}}}

Intents:
- generate-image: draw/paint/create image. action:{{""prompt"":""full description""}}
- question: any question, joke, info. action:{{}}
- media-play/media-pause/media-stop/media-next/media-previous: media control. action:{{}}
- media-volume: action:{{""level"":<0-100>}} or {{""direction"":""up/down""}}
- switch-extension: action:{{""name"":""display name""}}
- set-brightness: action:{{""level"":<0-100>}}
- music-search: search/play a specific song or music. action:{{""query"":""search terms""}}
- music-radio: play continuous/endless music by genre, mood, or style (e.g. ""play trance music"", ""play jazz"", ""play chill music""). action:{{""genre"":""genre/mood/style description""}}
- show-camera: show a camera feed on the display. action:{{""camera"":""alert|local""}}. Default to ""alert"" (security/door/IP camera) unless user explicitly says USB or webcam.
- hide-camera: stop/hide/close the camera feed. action:{{}}
- unknown: action:{{}}

Context: time={DateTime.Now:HH:mm}, date={DateTime.Now:d.M.yyyy}, brightness={ctx.BrightnessPercent}%, extension={ctx.ActiveExtension ?? "none"}, media={ctx.MediaState ?? "idle"}, volume={ctx.VolumePercent}%, extensions={extensions}, alertCamera={ctx.AlertCameraState ?? "off"}, localCamera={ctx.LocalCameraState ?? "off"}

If unclear image vs question, prefer question. For generate-image, keep the FULL original prompt. Keep responses short.";
    }

    /// <summary>
    ///     Normalize exotic Unicode characters that LLMs sometimes produce.
    ///     Replaces non-breaking hyphens, en/em dashes, fancy quotes, etc. with ASCII equivalents.
    /// </summary>
    private static string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace('\u2011', '-') // non-breaking hyphen → hyphen
            .Replace('\u2010', '-') // hyphen (Unicode) → hyphen
            .Replace('\u2013', '-') // en dash → hyphen
            .Replace('\u2014', '-') // em dash → hyphen
            .Replace('\u2018', '\'') // left single quote → apostrophe
            .Replace('\u2019', '\'') // right single quote → apostrophe
            .Replace('\u201C', '"') // left double quote → quote
            .Replace('\u201D', '"'); // right double quote → quote
    }
}

/// <summary>
///     Current system context provided to the LLM for better intent classification.
/// </summary>
public class VoiceContext
{
    public int BrightnessPercent { get; set; }
    public string? ActiveExtension { get; set; }
    public string? AvailableExtensions { get; set; }
    public string? MediaState { get; set; }
    public int VolumePercent { get; set; }
    public string? AlertCameraState { get; set; }
    public string? LocalCameraState { get; set; }
}

/// <summary>
///     Result of intent classification from the LLM.
/// </summary>
public class VoiceCommandResult
{
    public string Intent { get; set; } = VoiceIntents.Unknown;
    public string Response { get; set; } = "";
    public Dictionary<string, JsonElement> Action { get; set; } = new();

    public string GetActionString(string key, string defaultValue = "")
    {
        if (Action.TryGetValue(key, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? defaultValue;
        return defaultValue;
    }

    public int GetActionInt(string key, int defaultValue = 0)
    {
        if (!Action.TryGetValue(key, out var val)) return defaultValue;
        if (val.ValueKind == JsonValueKind.Number) return val.GetInt32();
        if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var parsed)) return parsed;
        return defaultValue;
    }
}
