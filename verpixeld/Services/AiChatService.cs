using System.Text;
using System.Text.Json;
using verpixeld.Configuration;

namespace verpixeld.Services;

/// <summary>
///     Azure OpenAI Chat Completion service for intent classification, Q&amp;A, and conversational AI.
///     Separated from AiImageService to maintain clean separation of concerns.
///     Shares the same ai_config.json for Azure credentials but manages chat-specific settings independently.
/// </summary>
public class AiChatService
{
    private readonly HttpClient _httpClient;
    private readonly string _configPath;

    // Azure connection (shared credentials, loaded from same config file)
    public string? AzureEndpoint { get; set; }
    public string? AzureApiKey { get; set; }
    public string? AzureChatDeployment { get; set; }
    public string AzureApiVersion { get; set; } = "2025-04-01-preview";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(AzureEndpoint) &&
        !string.IsNullOrEmpty(AzureApiKey) &&
        !string.IsNullOrEmpty(AzureChatDeployment);

    public AiChatService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        _configPath = AppPaths.AiConfig;

        LoadConfig();
    }

    /// <summary>
    ///     Send a chat completion request to Azure OpenAI.
    ///     Used for intent classification and conversational Q&amp;A.
    /// </summary>
    public async Task<string?> GetChatCompletionAsync(string systemPrompt, string userMessage, int maxTokens = 1000)
    {
        if (string.IsNullOrEmpty(AzureEndpoint) || string.IsNullOrEmpty(AzureApiKey))
        {
            Console.WriteLine("[AI/Chat] Azure not configured");
            return null;
        }

        if (string.IsNullOrEmpty(AzureChatDeployment))
        {
            Console.WriteLine("[AI/Chat] No chat deployment configured — set Chat Deployment in AI settings (e.g. gpt-4o)");
            return null;
        }

        try
        {
            var url = $"{AzureEndpoint.TrimEnd('/')}/openai/deployments/{AzureChatDeployment}/chat/completions?api-version={AzureApiVersion}";

            var body = new
            {
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage }
                },
                max_completion_tokens = maxTokens
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("api-key", AzureApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[AI/Chat] Error {response.StatusCode}: {responseBody}");
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
            {
                Console.WriteLine("[AI/Chat] Response contained no choices");
                return null;
            }

            var choice = choices[0];

            // Check for content filtering
            if (choice.TryGetProperty("finish_reason", out var finishReason))
            {
                var reason = finishReason.GetString();
                if (reason == "content_filter")
                {
                    Console.WriteLine("[AI/Chat] Response blocked by Azure content filter");
                    return "CONTENT_FILTERED";
                }
            }

            var content = choice
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrEmpty(content))
                Console.WriteLine("[AI/Chat] Response content was null/empty");

            return content;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI/Chat] Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Update chat configuration. Also updates shared Azure credentials in the config file.
    /// </summary>
    public void Configure(string? azureEndpoint, string? azureApiKey, string? azureChatDeployment, string? azureApiVersion)
    {
        if (azureEndpoint != null) AzureEndpoint = azureEndpoint;
        if (azureApiKey != null) AzureApiKey = azureApiKey;
        if (azureChatDeployment != null) AzureChatDeployment = azureChatDeployment;
        if (azureApiVersion != null) AzureApiVersion = azureApiVersion;

        SaveConfig();
        Console.WriteLine($"[AI/Chat] Configured: deployment={AzureChatDeployment}, configured={IsConfigured}");
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<ChatConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null) return;

            // Load shared Azure credentials
            AzureEndpoint = config.AzureEndpoint;
            AzureApiKey = config.AzureApiKey;
            AzureApiVersion = config.AzureApiVersion ?? "2025-04-01-preview";

            // Load chat-specific setting
            AzureChatDeployment = config.AzureChatDeployment;

            Console.WriteLine($"[AI/Chat] Config loaded: deployment={AzureChatDeployment ?? "not set"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI/Chat] Failed to load config: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            // Read existing config to preserve image-service fields
            Dictionary<string, JsonElement>? existing = null;
            if (File.Exists(_configPath))
            {
                var existingJson = File.ReadAllText(_configPath);
                existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson);
            }

            existing ??= new Dictionary<string, JsonElement>();

            // Update only our fields
            existing["AzureEndpoint"] = JsonSerializer.SerializeToElement(AzureEndpoint);
            existing["AzureApiKey"] = JsonSerializer.SerializeToElement(AzureApiKey);
            existing["AzureChatDeployment"] = JsonSerializer.SerializeToElement(AzureChatDeployment);
            existing["AzureApiVersion"] = JsonSerializer.SerializeToElement(AzureApiVersion);

            var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
            FileHelper.AtomicWriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI/Chat] Failed to save config: {ex.Message}");
        }
    }

    /// <summary>
    ///     Minimal config model for reading shared fields from ai_config.json.
    /// </summary>
    private class ChatConfig
    {
        public string? AzureEndpoint { get; set; }
        public string? AzureApiKey { get; set; }
        public string? AzureChatDeployment { get; set; }
        public string? AzureApiVersion { get; set; }
    }
}
