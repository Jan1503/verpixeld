using System.Text.Json;
using CanvasManagement;
using CanvasManagement.Interfaces;
using Microsoft.CognitiveServices.Speech;
using SkiaSharp;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.MediaPlayer;

namespace verpixeld.Services;

/// <summary>
///     Voice command service: listens for a wake word via Azure Speech SDK,
///     then uses Azure STT to transcribe the user's command, classifies the intent
///     via LLM, executes the action, and responds with TTS.
///     
///     Delegates to:
///     - <see cref="TextToSpeechService"/> for TTS + audio ducking
///     - <see cref="VoiceOverlayManager"/> for feedback canvas + image overlay
///     - <see cref="ParecAudioSession"/> for microphone capture
///     - <see cref="VoiceConfig"/> for configuration persistence
///     - <see cref="VoiceIntents"/> for intent string constants
/// </summary>
public class VoiceCommandService : IDisposable
{
    private readonly CanvasManager _canvasManager;
    private readonly AiImageService _aiImageService;
    private readonly AiChatService _aiChatService;
    private readonly string _configPath;

    // Extracted services
    private readonly TextToSpeechService _tts;
    private readonly VoiceOverlayManager _overlay;

    // Injected services
    private readonly VoiceCommandRouter _router;
    private readonly MediaPlayerService _mediaPlayer;
    private readonly ICanvasContentManager _contentManager;
    private readonly IExtensionDiscovery _extensionDiscovery;
    private readonly MusicSearchService _musicSearchService;
    private readonly RadioBrowserService _radioBrowserService;
    private readonly AlertService _alertService;
    private readonly LocalCameraService _localCameraService;

    // Configuration (loaded from VoiceConfig)
    public string? SpeechKey { get; set; }
    public string? SpeechRegion { get; set; }
    public string? KeywordModelPath { get; set; }
    public string? AudioDevice { get; set; }
    public string? VideoDevice { get; set; }
    public string SpeechLanguage { get; set; } = "de-DE";
    public string DefaultStyle { get; set; } = "";
    public int DisplayDurationSeconds { get; set; } = 60;
    public int SilenceTimeoutMs { get; set; } = 3500;
    public string ProfanityFilter { get; set; } = "raw";
    public string SegmentationStrategy { get; set; } = "Semantic";
    public bool TtsEnabled { get => _tts.TtsEnabled; set => _tts.TtsEnabled = value; }
    public string TtsVoiceName { get => _tts.TtsVoiceName; set => _tts.TtsVoiceName = value; }
    public bool MusicAudioOnly { get; set; } = true;
    public bool SaveGeneratedImages { get; set; } = true;
    public bool Enabled { get; set; }
    public int TtsDuckVolumePercent { get => _tts.TtsDuckVolumePercent; set => _tts.TtsDuckVolumePercent = value; }
    public bool TtsDuckingEnabled { get => _tts.TtsDuckingEnabled; set => _tts.TtsDuckingEnabled = value; }

    // State
    public VoiceState CurrentState { get; private set; } = VoiceState.Disabled;
    public string? LastTranscription { get; private set; }
    public string? LastIntent { get; private set; }
    public string? LastResponse { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastCommandTime { get; private set; }
    public int CommandCount { get; private set; }

    // Display
    private int _width;
    private int _height;

    // Internal
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    // Failure backoff: prevents a tight respawn loop (and console flooding) when the
    // microphone / PulseAudio capture keeps dying (e.g. running as root, or no mic).
    private int _consecutiveFailures;
    private const int MaxConsecutiveFailures = 6;

    // Persistent unified keyword + STT session
    private ParecAudioSession? _persistentAudioSession;
    private KeywordRecognitionModel? _keywordModel;
    private SpeechRecognizer? _keywordSpeechRecognizer;

    public VoiceCommandService(
        CanvasManager canvasManager, AiImageService aiImageService, AiChatService aiChatService,
        MediaPlayerService mediaPlayer, ICanvasContentManager contentManager,
        IExtensionDiscovery extensionDiscovery, MusicSearchService musicSearchService,
        RadioBrowserService radioBrowserService, AlertService alertService,
        LocalCameraService localCameraService, int width, int height)
    {
        _canvasManager = canvasManager;
        _aiImageService = aiImageService;
        _aiChatService = aiChatService;
        _mediaPlayer = mediaPlayer;
        _contentManager = contentManager;
        _extensionDiscovery = extensionDiscovery;
        _musicSearchService = musicSearchService;
        _radioBrowserService = radioBrowserService;
        _alertService = alertService;
        _localCameraService = localCameraService;
        _width = width;
        _height = height;

        _tts = new TextToSpeechService();
        _overlay = new VoiceOverlayManager(canvasManager);
        _overlay.Initialize(width, height);

        _configPath = AppPaths.VoiceConfig;
        LoadConfig();

        _router = new VoiceCommandRouter(_aiChatService, BuildVoiceContext);
        Console.WriteLine("[VOICE] Services wired: media, content manager, extensions, music search, alert, local camera, router");

        SdkAvailable = CheckSpeechSdkAvailable();
        Console.WriteLine($"[VOICE] Initialized for {width}x{height} display (SDK available: {SdkAvailable})");

        if (!Enabled && IsConfigured && SdkAvailable && HasKeywordModel)
        {
            Console.WriteLine("[VOICE] Keyword model present — auto-enabling voice listening");
            Enabled = true;
            SaveConfig();
        }

        if (Enabled && IsConfigured && SdkAvailable)
        {
            if (MicrophoneAvailable())
            {
                Start();
            }
            else
            {
                CurrentState = VoiceState.Error;
                LastError = "No microphone detected (no PulseAudio capture source).";
                Console.WriteLine(
                    "[VOICE] No microphone/capture source detected — voice listening not started. " +
                    "Connect a microphone (and ensure PulseAudio is accessible) then re-enable voice; " +
                    "it will also start automatically on next launch when a device is present.");
            }
        }
    }

    private VoiceContext BuildVoiceContext()
    {
        var ctx = new VoiceContext
        {
            BrightnessPercent = (int)(_canvasManager.Brightness * 100),
            VolumePercent = (int)(_mediaPlayer.Volume * 100)
        };

        try
        {
            var content = _contentManager.GetContent("Main");
            ctx.ActiveExtension = content?.ExtensionDisplayName ?? "none";
        }
        catch (Exception ex) { Console.WriteLine($"[VOICE] Failed to get active extension: {ex.Message}"); ctx.ActiveExtension = "unknown"; }

        try
        {
            var extensions = _extensionDiscovery.GetAvailableInfo();
            if (extensions != null)
                ctx.AvailableExtensions = string.Join(", ", extensions.Select(e => e.DisplayName).Take(20));
        }
        catch (Exception ex) { Console.WriteLine($"[VOICE] Failed to get available extensions: {ex.Message}"); ctx.AvailableExtensions = "unknown"; }

        ctx.AlertCameraState = _alertService.IsActive ? "streaming" : "off";
        ctx.LocalCameraState = _localCameraService.IsStreaming ? "streaming" : "off";

        if (_mediaPlayer.IsRunning)
        {
            var title = _mediaPlayer.CurrentYouTubeInfo?.Title
                    ?? _mediaPlayer.CurrentAudio
                    ?? _mediaPlayer.CurrentVideo
                    ?? "unknown";
            ctx.MediaState = _mediaPlayer.IsPaused ? $"paused: {title}" : $"playing: {title}";
        }
        else
        {
            ctx.MediaState = "stopped";
        }

        return ctx;
    }

    public bool SdkAvailable { get; private set; }

    private static bool CheckSpeechSdkAvailable()
    {
        try
        {
            var assembly = System.Reflection.Assembly.Load("Microsoft.CognitiveServices.Speech.csharp");
            Console.WriteLine($"[VOICE] Speech SDK loaded: {assembly.GetName().Version}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Speech SDK NOT available: {ex.Message}");
            Console.WriteLine("[VOICE] To fix: publish with 'dotnet publish -r linux-arm64' to include native Speech SDK libraries");
            return false;
        }
    }

    /// <summary>
    ///     Foundry uses one key for OpenAI and Speech. A dedicated Speech key
    ///     still wins when set; otherwise we reuse the Azure OpenAI key.
    /// </summary>
    public string? EffectiveSpeechKey =>
        !string.IsNullOrEmpty(SpeechKey) ? SpeechKey : _aiImageService.AzureApiKey;

    public bool UsesSharedAzureKey =>
        string.IsNullOrEmpty(SpeechKey) && !string.IsNullOrEmpty(_aiImageService.AzureApiKey);

    public bool IsConfigured =>
        !string.IsNullOrEmpty(EffectiveSpeechKey) &&
        !string.IsNullOrEmpty(SpeechRegion);

    public bool HasKeywordModel =>
        !string.IsNullOrEmpty(KeywordModelPath) &&
        File.Exists(KeywordModelPath);

    /// <summary>
    ///     True when a usable microphone capture source is currently available. Used to avoid
    ///     starting (and endlessly retrying) the listen loop when no mic is connected or
    ///     PulseAudio is not accessible.
    /// </summary>
    public bool MicrophoneAvailable()
    {
        var resolved = PulseAudioHelper.ResolvePaSource(AudioDevice);
        return PulseAudioHelper.HasInputSource(resolved);
    }

    // ═══════════════════════════════════════════════════════════════
    // Start / Stop
    // ═══════════════════════════════════════════════════════════════

    public void Start()
    {
        lock (_lock)
        {
            if (_listenTask != null) return;

            if (!SdkAvailable)
            {
                Console.WriteLine("[VOICE] Cannot start — Azure Speech SDK not available");
                CurrentState = VoiceState.Error;
                LastError = "Azure Speech SDK not available. Publish with: dotnet publish -r linux-arm64";
                return;
            }

            if (!IsConfigured)
            {
                Console.WriteLine("[VOICE] Cannot start — speech key/region not configured");
                CurrentState = VoiceState.Error;
                LastError = "Speech key/region not configured";
                return;
            }

            SyncTtsConfig();

            if (!MicrophoneAvailable())
            {
                Console.WriteLine(
                    "[VOICE] Cannot start — no microphone/capture source available. " +
                    "Connect a mic (and ensure PulseAudio is reachable) then try again.");
                CurrentState = VoiceState.Error;
                LastError = "No microphone detected (no PulseAudio capture source).";
                return;
            }

            _cts = new CancellationTokenSource();
            CurrentState = VoiceState.Idle;
            Enabled = true;
            _consecutiveFailures = 0;
            SaveConfig();

            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            Console.WriteLine("[VOICE] Started listening");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_listenTask == null) return;

            Console.WriteLine("[VOICE] Stopping...");
            _cts?.Cancel();

            try { _listenTask?.Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Console.WriteLine($"[VOICE] Listen task wait error: {ex.Message}"); }

            _listenTask = null;
            _cts?.Dispose();
            _cts = null;
            DisposeUnifiedSession();
            CurrentState = VoiceState.Disabled;
            Enabled = false;
            SaveConfig();

            _overlay.ClearFeedback();
            Console.WriteLine("[VOICE] Stopped");
        }
    }

    public async Task<string?> ManualTriggerAsync()
    {
        if (!SdkAvailable)
        {
            LastError = "Azure Speech SDK not available. Publish with: dotnet publish -r linux-arm64";
            return null;
        }

        if (!IsConfigured)
        {
            LastError = "Not configured";
            return null;
        }

        if (!MicrophoneAvailable())
        {
            LastError = "No microphone detected (no PulseAudio capture source).";
            Console.WriteLine("[VOICE] Manual trigger ignored — no microphone available");
            return null;
        }

        return await RecognizeSpeechAndGenerateAsync(CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // Main Listen Loop
    // ═══════════════════════════════════════════════════════════════

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (HasKeywordModel)
                {
                    var failed = await RunKeywordActivatedRecognitionAsync(ct);
                    if (failed)
                    {
                        if (!await HandleSessionFailureAsync(ct)) break;
                    }
                    else
                    {
                        // Healthy iteration (wake word handled or clean idle) — reset backoff.
                        _consecutiveFailures = 0;
                    }
                }
                else
                {
                    CurrentState = VoiceState.WaitingForKeyword;
                    Console.WriteLine("[VOICE] No keyword model — waiting for manual trigger via API");
                    await Task.Delay(5000, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[VOICE] Listen loop error: {ex.Message}");
                CurrentState = VoiceState.Error;
                LastError = ex.Message;
                if (!await HandleSessionFailureAsync(ct)) break;
            }
        }

        DisposeUnifiedSession();
        if (CurrentState != VoiceState.Error)
            CurrentState = VoiceState.Disabled;
    }

    /// <summary>
    ///     Applies exponential backoff after a microphone/recognition failure and decides
    ///     whether to keep retrying. Returns <see langword="false" /> when the loop should give up
    ///     to avoid flooding the console (e.g. no usable microphone, or PulseAudio is inaccessible
    ///     because the app runs as root). Returns <see langword="true" /> to retry after a delay.
    /// </summary>
    private async Task<bool> HandleSessionFailureAsync(CancellationToken ct)
    {
        _consecutiveFailures++;

        // Tear down the dead session so the next attempt rebuilds it cleanly.
        DisposeUnifiedSession();

        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            CurrentState = VoiceState.Error;
            LastError = "Microphone capture keeps failing (parec/PulseAudio). Voice listening paused.";
            Console.WriteLine(
                $"[VOICE] Microphone capture failed {_consecutiveFailures} times in a row — pausing voice listening to stop the retry loop.");
            Console.WriteLine(
                "[VOICE] Likely cause: the app runs as root (PulseAudio session not accessible: '/root/.config/pulse: Permission denied'), " +
                "or no microphone is available. Fix: run verpixeld as the 'pi' user, or set XDG_RUNTIME_DIR/PULSE_SERVER for the service. " +
                "Re-enable voice from the settings/API after fixing.");
            return false;
        }

        // Exponential backoff capped at 30s: 2s, 4s, 8s, 16s, 30s...
        var backoffMs = Math.Min(30000, 1000 * (int)Math.Pow(2, _consecutiveFailures));
        Console.WriteLine($"[VOICE] Audio session failure #{_consecutiveFailures} — retrying in {backoffMs / 1000}s");

        try { await Task.Delay(backoffMs, ct); }
        catch (OperationCanceledException) { return false; }

        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Unified Keyword-Activated Speech Recognition
    // ═══════════════════════════════════════════════════════════════

    private async Task<bool> RunKeywordActivatedRecognitionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureUnifiedSessionAlive();

        _persistentAudioSession?.ResumePush();

        var speechTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keywordDetected = false;

        // Set when the recognizer aborts because the audio stream died (parec/PulseAudio failure)
        // rather than from normal speech flow. Signals the listen loop to back off.
        var recognitionFailed = false;

        _keywordSpeechRecognizer!.Recognizing += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizingSpeech && !string.IsNullOrEmpty(e.Result.Text))
            {
                if (!keywordDetected)
                {
                    keywordDetected = true;
                    CurrentState = VoiceState.Listening;
                    Console.WriteLine("[VOICE] Wake word detected — listening...");
                }
                var partial = StripKeywordPrefix(e.Result.Text);
                _overlay.ShowFeedback(partial + " _", new SKColor(0, 180, 255), "LISTENING");
            }
        };

        _keywordSpeechRecognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedKeyword)
            {
                keywordDetected = true;
                CurrentState = VoiceState.Listening;
                _overlay.ShowFeedback("Speak now...", new SKColor(0, 180, 255), "LISTENING");
                Console.WriteLine($"[VOICE] Wake word detected: \"{e.Result.Text}\"");
            }
            else if (e.Result.Reason == ResultReason.RecognizedSpeech)
            {
                var text = StripKeywordPrefix(e.Result.Text);
                Console.WriteLine($"[VOICE] STT result: \"{text}\" (raw: \"{e.Result.Text}\")");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    speechTcs.TrySetResult(text);
                }
                else
                {
                    Console.WriteLine("[VOICE] Keyword-only segment — waiting for follow-up speech...");
                }
            }
            else if (e.Result.Reason == ResultReason.NoMatch)
            {
                if (keywordDetected)
                    Console.WriteLine("[VOICE] No match after keyword — waiting for follow-up...");
            }
        };

        _keywordSpeechRecognizer.Canceled += (_, e) =>
        {
            Console.WriteLine($"[VOICE] Recognition canceled: {e.Reason} — {e.ErrorDetails}");
            if (e.Reason == CancellationReason.Error)
                LastError = e.ErrorDetails;
            // EndOfStream / Error here means the microphone stream collapsed (no keyword was being
            // processed), which is the symptom of the parec/PulseAudio failure loop.
            if (!keywordDetected &&
                (e.Reason == CancellationReason.Error || e.Reason == CancellationReason.EndOfStream))
                recognitionFailed = true;
            speechTcs.TrySetResult(null);
        };

        _keywordSpeechRecognizer.SessionStopped += (_, _) =>
        {
            speechTcs.TrySetResult(null);
        };

        CurrentState = VoiceState.Idle;
        Console.WriteLine("[VOICE] Waiting for wake word (unified mode)...");
        await _keywordSpeechRecognizer.StartKeywordRecognitionAsync(_keywordModel);

        var completedTask = await Task.WhenAny(speechTcs.Task, Task.Delay(Timeout.Infinite, ct));
        string? transcription = null;
        if (completedTask == speechTcs.Task)
            transcription = await speechTcs.Task;

        try { await _keywordSpeechRecognizer.StopKeywordRecognitionAsync(); }
        catch (Exception ex) { Console.WriteLine($"[VOICE] Stop keyword recognition error: {ex.Message}"); }

        _persistentAudioSession?.PausePush();
        DetachRecognizerEvents();

        if (!string.IsNullOrEmpty(transcription) && !ct.IsCancellationRequested)
        {
            await ProcessTranscriptionAsync(transcription, ct);
        }
        else if (keywordDetected && string.IsNullOrEmpty(transcription) && !ct.IsCancellationRequested)
        {
            Console.WriteLine("[VOICE] Keyword detected but no speech — listening for follow-up...");
            _overlay.ShowFeedback("Speak now...", new SKColor(0, 180, 255), "LISTENING");

            _persistentAudioSession?.ResumePush();
            var followUp = await ListenForFollowUpAsync(ct);
            if (!string.IsNullOrEmpty(followUp) && !ct.IsCancellationRequested)
            {
                await ProcessTranscriptionAsync(followUp, ct);
            }
            else
            {
                _overlay.ShowFeedback("No speech detected", new SKColor(255, 80, 80), "ERROR");
                await Task.Delay(2000, ct);
                _overlay.ClearFeedback();
            }
        }

        return recognitionFailed;
    }

    private async Task<string?> ListenForFollowUpAsync(CancellationToken ct)
    {
        if (_persistentAudioSession == null || !_persistentAudioSession.IsAlive)
            return null;

        try
        {
            var speechConfig = CreateSpeechConfig();
            speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "5000");

            using var recognizer = new SpeechRecognizer(speechConfig, _persistentAudioSession.AudioConfig);

            recognizer.Recognizing += (_, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizingSpeech && !string.IsNullOrEmpty(e.Result.Text))
                    _overlay.ShowFeedback(e.Result.Text + " _", new SKColor(0, 180, 255), "LISTENING");
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(10000);

            var result = await recognizer.RecognizeOnceAsync();

            if (result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(result.Text))
            {
                var text = StripKeywordPrefix(result.Text);
                Console.WriteLine($"[VOICE] Follow-up STT: \"{text}\" (raw: \"{result.Text}\")");
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            Console.WriteLine($"[VOICE] Follow-up: no speech (reason={result.Reason})");
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Follow-up listen error: {ex.Message}");
            return null;
        }
    }

    private static string StripKeywordPrefix(string text)
    {
        var prefixes = new[] { "Hey Pixel", "hey pixel", "Pixel", "pixel" };
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = text[prefix.Length..].TrimStart(' ', ',', '.');
                return string.IsNullOrEmpty(stripped) ? text : stripped;
            }
        }
        return text;
    }

    // ═══════════════════════════════════════════════════════════════
    // Unified Session Lifecycle
    // ═══════════════════════════════════════════════════════════════

    private SpeechConfig CreateSpeechConfig()
    {
        var key = EffectiveSpeechKey
            ?? throw new InvalidOperationException("No speech key — save the Azure OpenAI key (Foundry) or a Speech key.");
        var speechConfig = SpeechConfig.FromSubscription(key, SpeechRegion!);
        speechConfig.SpeechRecognitionLanguage = SpeechLanguage ?? "de-DE";
        speechConfig.SetProperty(PropertyId.SpeechServiceResponse_ProfanityOption, ProfanityFilter ?? "raw");
        return speechConfig;
    }

    private void EnsureUnifiedSessionAlive()
    {
        if (_persistentAudioSession != null && !_persistentAudioSession.IsAlive)
        {
            Console.WriteLine("[VOICE] Audio session died — recreating");
            DisposeUnifiedSession();
        }

        if (_persistentAudioSession == null)
        {
            var paSource = PulseAudioHelper.ResolvePaSource(AudioDevice);
            _persistentAudioSession = new ParecAudioSession(paSource);
            Console.WriteLine("[VOICE] Persistent audio session started");
        }

        if (_keywordModel == null)
        {
            _keywordModel = KeywordRecognitionModel.FromFile(KeywordModelPath);
            Console.WriteLine("[VOICE] Keyword model loaded");
        }

        if (_keywordSpeechRecognizer == null)
        {
            var speechConfig = CreateSpeechConfig();
            _keywordSpeechRecognizer = new SpeechRecognizer(speechConfig, _persistentAudioSession.AudioConfig);
            Console.WriteLine("[VOICE] Unified SpeechRecognizer created");
        }
    }

    private void DetachRecognizerEvents()
    {
        try { _keywordSpeechRecognizer?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[VOICE] Recognizer dispose error: {ex.Message}"); }
        _keywordSpeechRecognizer = null;
    }

    private void DisposeUnifiedSession()
    {
        _overlay.DismissAll();
        DismissVoiceCamera();

        try { _keywordSpeechRecognizer?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[VOICE] Recognizer dispose error: {ex.Message}"); }
        _keywordSpeechRecognizer = null;

        try { _keywordModel?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[VOICE] Keyword model dispose error: {ex.Message}"); }
        _keywordModel = null;

        try { _persistentAudioSession?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[VOICE] Audio session dispose error: {ex.Message}"); }
        _persistentAudioSession = null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Speech-to-Text → Intent Classification → Action + TTS
    // ═══════════════════════════════════════════════════════════════

    private async Task ProcessTranscriptionAsync(string transcription, CancellationToken ct)
    {
        try
        {
            // Dismiss ALL active overlays immediately when a new command arrives
            _overlay.DismissAll();
            _aiImageService.DismissImageOverlay();
            DismissVoiceCamera();

            LastTranscription = transcription;
            LastCommandTime = DateTime.UtcNow;
            CommandCount++;
            Console.WriteLine($"[VOICE] Transcribed: \"{transcription}\"");

            // ── Fast-path: match simple commands locally (no LLM roundtrip) ──
            var fastResult = TryMatchLocalIntent(transcription);
            if (fastResult != null)
            {
                Console.WriteLine($"[VOICE] Fast-path match: {fastResult.Intent}");
                LastIntent = fastResult.Intent;
                LastResponse = fastResult.Response;
                await ExecuteIntentAsync(fastResult, transcription, ct);
                CurrentState = VoiceState.Idle;
                return;
            }

            // ── Slow path: classify via LLM ──
            CurrentState = VoiceState.Processing;
            _overlay.ShowFeedback(transcription, new SKColor(180, 100, 255), "THINKING");

            VoiceCommandResult commandResult;
            if (_aiChatService.IsConfigured)
            {
                commandResult = await _router.ClassifyAsync(transcription);
        }
        else
            {
                commandResult = new VoiceCommandResult
                {
                    Intent = VoiceIntents.GenerateImage,
                    Response = "",
                    Action = new Dictionary<string, JsonElement>
                    {
                        ["prompt"] = JsonSerializer.SerializeToElement(transcription)
                    }
                };
            }

            LastIntent = commandResult.Intent;
            LastResponse = commandResult.Response;

            await ExecuteIntentAsync(commandResult, transcription, ct);
            CurrentState = VoiceState.Idle;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Error processing transcription: {ex.Message}");
            LastError = ex.Message;
            CurrentState = VoiceState.Error;
            _overlay.ClearFeedback();
        }
    }

    private async Task<string?> RecognizeSpeechAndGenerateAsync(CancellationToken ct)
    {
        try
        {
            CurrentState = VoiceState.Listening;
            _overlay.ShowFeedback("Speak now...", new SKColor(0, 180, 255), "LISTENING");

            var transcription = await RecognizeSpeechAsync(ct);
            if (string.IsNullOrEmpty(transcription))
            {
                _overlay.ShowFeedback("No speech detected", new SKColor(255, 80, 80), "ERROR");
                await Task.Delay(2000, ct);
                _overlay.ClearFeedback();
                CurrentState = VoiceState.Idle;
                return null;
            }

            await ProcessTranscriptionAsync(transcription, ct);
            return transcription;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Error: {ex.Message}");
            LastError = ex.Message;
            CurrentState = VoiceState.Error;
            _overlay.ClearFeedback();
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Fast-Path Local Intent Matching (no LLM)
    // ═══════════════════════════════════════════════════════════════

    private static VoiceCommandResult? TryMatchLocalIntent(string text)
    {
        var t = text.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();

        // ── Media control ──
        if (IsOneOf(t, "stop", "stopp", "halt", "anhalten", "musik stopp", "musik stop", "stoppe die musik",
                "alles stoppen", "stoppe alles"))
            return Quick(VoiceIntents.MediaStop);

        if (IsOneOf(t, "pause", "pausieren", "pausiere"))
            return Quick(VoiceIntents.MediaPause);

        if (IsOneOf(t, "weiter", "play", "abspielen", "weiterspielen", "fortsetzen"))
            return Quick(VoiceIntents.MediaPlay);

        if (IsOneOf(t, "nächstes", "nächster", "nächstes lied", "nächster song", "next", "skip", "überspringen"))
            return Quick(VoiceIntents.MediaNext);

        if (IsOneOf(t, "zurück", "vorheriges", "vorheriges lied", "vorheriger song", "previous", "letztes lied"))
            return Quick(VoiceIntents.MediaPrevious);

        if (IsOneOf(t, "leiser", "leise", "ton leiser", "volume down", "ruhiger"))
            return Quick(VoiceIntents.MediaVolume, ("direction", "down"));

        if (IsOneOf(t, "lauter", "laut", "ton lauter", "volume up", "mehr lautstärke"))
            return Quick(VoiceIntents.MediaVolume, ("direction", "up"));

        if (IsOneOf(t, "ton aus", "mute", "stumm", "stummschalten", "lautstärke null"))
            return Quick(VoiceIntents.MediaVolume, ("level", "0"));

        // ── Camera ──
        if (IsOneOf(t, "kamera aus", "kamera stopp", "kamera stop", "kamera schließen", "kamera beenden",
                "verstecke kamera", "hide camera"))
            return Quick(VoiceIntents.HideCamera);

        // ── Brightness ──
        if (IsOneOf(t, "licht aus", "display aus", "bildschirm aus", "dunkel"))
            return Quick(VoiceIntents.SetBrightness, ("level", "0"));

        if (IsOneOf(t, "licht an", "display an", "bildschirm an", "hell"))
            return Quick(VoiceIntents.SetBrightness, ("level", "100"));

        return null;
    }

    private static bool IsOneOf(string input, params string[] options)
    {
        foreach (var opt in options)
            if (input == opt) return true;
        return false;
    }

    private static VoiceCommandResult Quick(string intent, params (string key, string value)[] actions)
    {
        var result = new VoiceCommandResult { Intent = intent, Response = "" };
        foreach (var (key, value) in actions)
            result.Action[key] = JsonSerializer.SerializeToElement(value);
        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // Intent Execution
    // ═══════════════════════════════════════════════════════════════

    private async Task ExecuteIntentAsync(VoiceCommandResult cmd, string transcription, CancellationToken ct)
    {
        Console.WriteLine($"[VOICE] Executing intent: {cmd.Intent}");

        switch (cmd.Intent)
        {
            case VoiceIntents.GenerateImage:
                await HandleGenerateImageAsync(cmd, transcription, ct);
                break;

            case VoiceIntents.Question:
                await HandleQuestionAsync(cmd, ct);
                break;

            case VoiceIntents.MediaPlay:
            case VoiceIntents.MediaPause:
                HandleMediaPlayPause(cmd);
                SpeakAndShowResponseFireAndForget(cmd.Response, new SKColor(0, 200, 100), "MEDIA");
                return;

            case VoiceIntents.MediaStop:
                await _mediaPlayer.StopAsync();
                SpeakAndShowResponseFireAndForget(cmd.Response, new SKColor(0, 200, 100), "MEDIA");
                return;

            case VoiceIntents.MediaNext:
                await _mediaPlayer.PlayNextAsync();
                SpeakAndShowResponseFireAndForget(cmd.Response, new SKColor(0, 200, 100), "MEDIA");
                return;

            case VoiceIntents.MediaPrevious:
                await _mediaPlayer.PlayPreviousAsync();
                SpeakAndShowResponseFireAndForget(cmd.Response, new SKColor(0, 200, 100), "MEDIA");
                return;

            case VoiceIntents.MediaVolume:
                HandleMediaVolume(cmd);
                SpeakAndShowResponseFireAndForget(cmd.Response, new SKColor(0, 200, 100), "VOLUME");
                return;

            case VoiceIntents.SwitchExtension:
                HandleSwitchExtension(cmd);
                SpeakAndShowResponseFireAndForget(cmd.Response, new SKColor(100, 180, 255), "EXTENSION");
                return;

            case VoiceIntents.SetBrightness:
                HandleSetBrightness(cmd);
                SpeakAndShowResponseFireAndForget(cmd.Response, new SKColor(255, 200, 50), "BRIGHTNESS");
                return;

            case VoiceIntents.MusicSearch:
                await HandleMusicSearchAsync(cmd, ct);
                break;

            case VoiceIntents.MusicRadio:
                await HandleMusicRadioAsync(cmd, ct);
                break;

            case VoiceIntents.ShowCamera:
                HandleShowCamera(cmd);
                _tts.SpeakFireAndForget(cmd.Response);
                return;

            case VoiceIntents.HideCamera:
                HandleHideCamera();
                _tts.SpeakFireAndForget(cmd.Response);
                return;

            default:
                await SpeakAndShowResponseAsync(
                    !string.IsNullOrEmpty(cmd.Response) ? cmd.Response : "Das habe ich nicht verstanden.",
                    new SKColor(255, 150, 50), "PIXEL", ct);
                break;
        }
    }

    // ── Intent Handlers ──

    private async Task HandleGenerateImageAsync(VoiceCommandResult cmd, string transcription, CancellationToken ct)
    {
        var prompt = cmd.GetActionString("prompt", transcription);

        if (!string.IsNullOrEmpty(cmd.Response))
            _tts.SpeakFireAndForget(cmd.Response);

        CurrentState = VoiceState.Generating;
        _overlay.ShowFeedback(prompt, new SKColor(180, 100, 255), "GENERATING");

        var result = await _aiImageService.GenerateImageAsync(prompt, DefaultStyle);

        if (!result.Success || result.ImageBase64 == null)
        {
            var err = result.Error ?? "Generation failed";
            Console.WriteLine($"[VOICE] Generation failed: {err}");
            _overlay.ShowFeedback(err, new SKColor(255, 80, 80), "ERROR");
            LastError = err;
            await Task.Delay(3000, ct);
            _overlay.ClearFeedback();
            return;
        }

        CurrentState = VoiceState.Displaying;
        _overlay.ClearFeedback();

        var imageBytes = Convert.FromBase64String(result.ImageBase64);
        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap != null)
            _overlay.ShowImageOverlay(bitmap, DisplayDurationSeconds);

        if (SaveGeneratedImages)
        {
            try
            {
                _aiImageService.SaveImageToDisk(result.ImageBase64, prompt, DefaultStyle ?? "voice");
                Console.WriteLine("[VOICE] Image saved to gallery");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VOICE] Failed to save image to gallery: {ex.Message}");
            }
        }
    }

    private async Task HandleQuestionAsync(VoiceCommandResult cmd, CancellationToken ct)
    {
        await SpeakAndShowResponseAsync(
            !string.IsNullOrEmpty(cmd.Response) ? cmd.Response : "Ich weiß es leider nicht.",
            new SKColor(0, 180, 255), "PIXEL", ct);
    }

    private void HandleMediaPlayPause(VoiceCommandResult cmd)
    {
        _mediaPlayer.TogglePause();
        Console.WriteLine($"[VOICE] Media: toggle pause (now {(_mediaPlayer.IsPaused ? "paused" : "playing")})");
    }

    private void HandleMediaVolume(VoiceCommandResult cmd)
    {
        var direction = cmd.GetActionString("direction");
        if (!string.IsNullOrEmpty(direction))
        {
            var current = (int)(_mediaPlayer.Volume * 100);
            var step = cmd.GetActionInt("step", 10);
            var newLevel = direction == "up" ? current + step : current - step;
            _mediaPlayer.SetVolume(Math.Clamp(newLevel, 0, 100));
        }
        else
        {
            var level = cmd.GetActionInt("level", -1);
            if (level >= 0) _mediaPlayer.SetVolume(Math.Clamp(level, 0, 100));
        }
        Console.WriteLine($"[VOICE] Volume set to {(int)(_mediaPlayer.Volume * 100)}%");
    }

    private void HandleSwitchExtension(VoiceCommandResult cmd)
    {
        var name = cmd.GetActionString("name");
        if (string.IsNullOrEmpty(name)) return;

        var extensions = _extensionDiscovery.GetAvailableInfo();
        var match = extensions?.FirstOrDefault(e =>
            e.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            _contentManager.AssignExtension("Main", match.DisplayName);
            Console.WriteLine($"[VOICE] Switched extension to: {match.DisplayName}");
        }
        else
        {
            Console.WriteLine($"[VOICE] Extension not found: {name}");
        }
    }

    private void HandleSetBrightness(VoiceCommandResult cmd)
    {
        var level = cmd.GetActionInt("level", -1);
        if (level >= 0)
        {
            _canvasManager.Brightness = Math.Clamp(level / 100f, 0f, 1f);
            Console.WriteLine($"[VOICE] Brightness set to {level}%");
        }
    }

    private async Task HandleMusicSearchAsync(VoiceCommandResult cmd, CancellationToken ct)
    {
        var query = cmd.GetActionString("query");
        if (string.IsNullOrEmpty(query))
        {
            await SpeakAndShowResponseAsync("Ich habe keinen Suchbegriff verstanden.",
                new SKColor(255, 80, 80), "ERROR", ct);
            return;
        }

        _overlay.ShowFeedback($"Suche: {query}", new SKColor(255, 100, 200), "MUSIC");

        if (!string.IsNullOrEmpty(cmd.Response))
            _tts.SpeakFireAndForget(cmd.Response);

        var result = await _musicSearchService.SearchAndGetUrlAsync(query, preferVideo: !MusicAudioOnly);
        if (result == null)
        {
            await SpeakAndShowResponseAsync($"Ich konnte nichts finden für \"{query}\".",
                new SKColor(255, 80, 80), "NOT FOUND", ct);
            return;
        }

        Console.WriteLine($"[VOICE] Playing music ({result.Type}, audioOnly={MusicAudioOnly}): \"{result.Title}\" by {result.Artist} → {result.Url}");
        _overlay.ShowFeedback($"♪ {result.Title}\n{result.Artist}", new SKColor(255, 100, 200), "PLAYING");

        try
        {
            var playSuccess = await _mediaPlayer.PlayYouTubeVideoAsync(result.Url, loop: false, audioOnly: MusicAudioOnly);

            if (!playSuccess)
            {
                var error = _mediaPlayer.LastPlaybackError ?? "Wiedergabe fehlgeschlagen";
                Console.WriteLine($"[VOICE] Music playback failed: {error}");
                await SpeakAndShowResponseAsync(
                    $"Das Lied konnte nicht abgespielt werden. {error}",
                    new SKColor(255, 80, 80), "ERROR", ct);
                return;
            }

            var confirmText = $"Spiele jetzt {result.Title} von {result.Artist}.";
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_tts.TtsEnabled)
                        await _tts.SpeakAsync(confirmText);
                    else
                        await Task.Delay(3000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VOICE/TTS] Music confirm error: {ex.Message}");
                }
                finally { _overlay.ClearFeedback(); }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Music playback error: {ex.Message}");
            await SpeakAndShowResponseAsync("Fehler beim Abspielen.",
                new SKColor(255, 80, 80), "ERROR", ct);
        }
    }

    private async Task HandleMusicRadioAsync(VoiceCommandResult cmd, CancellationToken ct)
    {
        var genre = cmd.GetActionString("genre", "music");
        Console.WriteLine($"[VOICE] Starting music radio: \"{genre}\"");

        _overlay.ShowFeedback($"♪ Radio: {genre}", new SKColor(255, 100, 200), "RADIO");

        if (!string.IsNullOrEmpty(cmd.Response))
            _tts.SpeakFireAndForget(cmd.Response);

        var stations = await _radioBrowserService.SearchStationsAsync(genre, limit: 10);
        if (stations.Count == 0)
        {
            await SpeakAndShowResponseAsync($"Ich konnte keinen Radiosender für \"{genre}\" finden.",
                new SKColor(255, 80, 80), "NOT FOUND", ct);
            return;
        }

        var station = stations[0];
        Console.WriteLine($"[RADIO] Selected: \"{station.Name}\" ({station.Codec} {station.Bitrate}kbps) — {station.StreamUrl}");

        _overlay.ShowFeedback($"♪ {station.Name}", new SKColor(255, 100, 200), "RADIO");

        try
        {
            await _mediaPlayer.PlayRadioStreamAsync(station.StreamUrl, station.Name);
            Console.WriteLine($"[RADIO] Stream started: \"{station.Name}\"");

            _ = Task.Run(async () =>
            {
                await Task.Delay(4000);
                _overlay.ClearFeedback();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RADIO] Stream error: {ex.Message}");
            await SpeakAndShowResponseAsync("Der Radiosender konnte nicht gestartet werden.",
                new SKColor(255, 80, 80), "ERROR", ct);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Camera Commands
    // ═══════════════════════════════════════════════════════════════

    private void HandleShowCamera(VoiceCommandResult cmd)
    {
        var camera = cmd.GetActionString("camera", "alert");
        Console.WriteLine($"[VOICE] Show camera: {camera}");

        DismissVoiceCamera();
        _overlay.ClearFeedback();

        if (camera == "local")
        {
            if (_localCameraService.IsStreaming)
            {
                Console.WriteLine("[VOICE] Local camera already streaming");
                return;
            }

            var started = _localCameraService.StartStream();
            if (started)
                Console.WriteLine("[VOICE] Local camera started via voice");
            else
                Console.WriteLine("[VOICE] Failed to start local camera (no device configured?)");
        }
        else
        {
            _alertService.TriggerAlert();
            Console.WriteLine("[VOICE] Alert camera triggered via voice");
        }
    }

    private void HandleHideCamera()
    {
        Console.WriteLine("[VOICE] Hide camera");
        DismissVoiceCamera();
    }

    private void DismissVoiceCamera()
    {
        if (_alertService.IsActive)
        {
            Task.Run(() =>
            {
                try
                {
                    _alertService.DismissAlert();
                    Console.WriteLine("[VOICE] Alert camera dismissed by new command");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VOICE] Alert dismiss error: {ex.Message}");
                }
            });
        }

        if (_localCameraService.IsStreaming)
        {
            try
            {
                _localCameraService.StopStream();
                Console.WriteLine("[VOICE] Local camera dismissed by new command");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VOICE] Local camera dismiss error: {ex.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // TTS + Overlay Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    ///     Show response text on feedback overlay and speak it via TTS.
    ///     Post-TTS reading period is non-blocking.
    /// </summary>
    private async Task SpeakAndShowResponseAsync(string text, SKColor accentColor, string label, CancellationToken ct)
    {
        CurrentState = VoiceState.Speaking;
        _overlay.ShowFeedback(text, accentColor, label);

        if (_tts.TtsEnabled && !string.IsNullOrEmpty(text))
        {
            await _tts.SpeakAsync(text);
            _overlay.ScheduleFeedbackAutoClear(Math.Clamp(text.Length * 30, 3000, 10000));
        }
        else
        {
            _overlay.ScheduleFeedbackAutoClear(Math.Clamp(text.Length * 80, 3000, 15000));
        }
    }

    /// <summary>
    ///     Fire-and-forget TTS for instant actions.
    /// </summary>
    private void SpeakAndShowResponseFireAndForget(string text, SKColor accentColor, string label)
    {
        CurrentState = VoiceState.Speaking;
        _overlay.ShowFeedback(text, accentColor, label);

        _ = Task.Run(async () =>
        {
            try
            {
                if (_tts.TtsEnabled && !string.IsNullOrEmpty(text))
                    await _tts.SpeakAsync(text);
                else
                    await Task.Delay(Math.Min(text.Length * 80, 3000));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VOICE/TTS] Background TTS error: {ex.Message}");
            }
            finally
            {
                _overlay.ClearFeedback();
                CurrentState = VoiceState.Idle;
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // STT for Manual Trigger (Push-to-Talk)
    // ═══════════════════════════════════════════════════════════════

    private async Task<string?> RecognizeSpeechAsync(CancellationToken ct)
    {
        try
        {
            var speechConfig = CreateSpeechConfig();
            speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "10000");

            var strategy = SegmentationStrategy ?? "Default";
            if (strategy == "Semantic")
            {
                speechConfig.SetProperty(PropertyId.Speech_SegmentationStrategy, "Semantic");
                Console.WriteLine($"[VOICE] STT config: lang={SpeechLanguage}, segmentation=Semantic, profanity={ProfanityFilter}, pauseAfterSegment={SilenceTimeoutMs}ms");
            }
            else
            {
                speechConfig.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, SilenceTimeoutMs.ToString());
                speechConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, SilenceTimeoutMs.ToString());
                if (strategy != "Default")
                    speechConfig.SetProperty(PropertyId.Speech_SegmentationStrategy, strategy);
                Console.WriteLine($"[VOICE] STT config: lang={SpeechLanguage}, silence={SilenceTimeoutMs}ms, segmentation={strategy}, profanity={ProfanityFilter}");
            }

            var paSource = PulseAudioHelper.ResolvePaSource(AudioDevice);
            using var session = new ParecAudioSession(paSource);
            using var recognizer = new SpeechRecognizer(speechConfig, session.AudioConfig);

            var segments = new List<string>();
            string? lastError = null;
            var errorOrSessionEnd = new TaskCompletionSource<bool>();
            var lastActivityTime = DateTime.UtcNow;
            var partialText = "";

            recognizer.Recognizing += (_, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizingSpeech && !string.IsNullOrEmpty(e.Result.Text))
                {
                    lastActivityTime = DateTime.UtcNow;
                    partialText = e.Result.Text;
                    var displayText = segments.Count > 0
                        ? string.Join(" ", segments) + " " + partialText + " _"
                        : partialText + " _";
                    _overlay.ShowFeedback(displayText, new SKColor(0, 180, 255), "LISTENING");
                }
            };

            recognizer.Recognized += (_, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrEmpty(e.Result.Text))
                {
                    var segmentText = e.Result.Text.TrimEnd('.');
                    segments.Add(segmentText);
                    partialText = "";
                    lastActivityTime = DateTime.UtcNow;
                    Console.WriteLine($"[VOICE] STT segment {segments.Count}: \"{segmentText}\"");
                    _overlay.ShowFeedback(string.Join(" ", segments), new SKColor(0, 180, 255), "LISTENING");
                }
                else if (e.Result.Reason == ResultReason.NoMatch)
                {
                    Console.WriteLine("[VOICE] No match (silence)");
                }
            };

            recognizer.Canceled += (_, e) =>
            {
                Console.WriteLine($"[VOICE] STT canceled: {e.Reason} — {e.ErrorDetails}");
                lastError = e.ErrorDetails;
                errorOrSessionEnd.TrySetResult(true);
            };

            recognizer.SessionStopped += (_, _) =>
            {
                errorOrSessionEnd.TrySetResult(true);
            };

            await recognizer.StartContinuousRecognitionAsync();

            var overallDeadline = DateTime.UtcNow.AddSeconds(60);
            var pauseTimeout = SilenceTimeoutMs;

            while (DateTime.UtcNow < overallDeadline)
            {
                ct.ThrowIfCancellationRequested();

                if (errorOrSessionEnd.Task.IsCompleted)
                    break;

                var msSinceActivity = (DateTime.UtcNow - lastActivityTime).TotalMilliseconds;

                if (segments.Count > 0 && msSinceActivity >= pauseTimeout)
                {
                    Console.WriteLine($"[VOICE] Done: {pauseTimeout}ms silence after {segments.Count} segment(s)");
                    break;
                }

                if (segments.Count == 0 && string.IsNullOrEmpty(partialText) && msSinceActivity >= 10000)
                {
                    Console.WriteLine("[VOICE] No speech detected within 10s");
                    break;
                }

                await Task.Delay(200, ct);
            }

            try { await recognizer.StopContinuousRecognitionAsync(); }
            catch (Exception ex) { Console.WriteLine($"[VOICE] Stop continuous recognition error: {ex.Message}"); }

            if (lastError != null)
                LastError = lastError;

            if (segments.Count == 0)
            {
                Console.WriteLine("[VOICE] No segments recognized");
                return null;
            }

            var fullText = string.Join(" ", segments);
            Console.WriteLine($"[VOICE] STT complete ({segments.Count} segment(s)): \"{fullText}\"");
            return fullText;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] STT error: {ex.Message}");
            LastError = ex.Message;
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Configuration Persistence
    // ═══════════════════════════════════════════════════════════════

    public void Configure(string? speechKey, string? speechRegion, string? keywordModelPath,
        string? audioDevice, string? videoDevice, string? defaultStyle,
        string? speechLanguage, int? displayDuration, bool? enabled,
        int? silenceTimeoutMs = null, string? profanityFilter = null, string? segmentationStrategy = null,
        bool? ttsEnabled = null, string? ttsVoiceName = null, bool? musicAudioOnly = null,
        bool? saveGeneratedImages = null, bool? ttsDuckingEnabled = null, int? ttsDuckVolumePercent = null)
    {
        if (speechKey != null) SpeechKey = speechKey;
        if (speechRegion != null) SpeechRegion = speechRegion;
        if (keywordModelPath != null) KeywordModelPath = keywordModelPath;
        if (audioDevice != null) AudioDevice = audioDevice;
        if (videoDevice != null) VideoDevice = videoDevice;
        if (defaultStyle != null) DefaultStyle = defaultStyle;
        if (speechLanguage != null) SpeechLanguage = speechLanguage;
        if (displayDuration.HasValue) DisplayDurationSeconds = Math.Clamp(displayDuration.Value, 5, 3600);
        if (silenceTimeoutMs.HasValue) SilenceTimeoutMs = Math.Clamp(silenceTimeoutMs.Value, 500, 10000);
        if (profanityFilter != null) ProfanityFilter = profanityFilter;
        if (segmentationStrategy != null) SegmentationStrategy = segmentationStrategy;
        if (ttsEnabled.HasValue) TtsEnabled = ttsEnabled.Value;
        if (ttsVoiceName != null) TtsVoiceName = ttsVoiceName;
        if (musicAudioOnly.HasValue) MusicAudioOnly = musicAudioOnly.Value;
        if (saveGeneratedImages.HasValue) SaveGeneratedImages = saveGeneratedImages.Value;
        if (enabled.HasValue) Enabled = enabled.Value;
        if (ttsDuckingEnabled.HasValue) TtsDuckingEnabled = ttsDuckingEnabled.Value;
        if (ttsDuckVolumePercent.HasValue) TtsDuckVolumePercent = Math.Clamp(ttsDuckVolumePercent.Value, 0, 100);

        if (!Enabled && IsConfigured && SdkAvailable && HasKeywordModel && !enabled.HasValue)
        {
            Console.WriteLine("[VOICE] Keyword model present — auto-enabling voice listening");
            Enabled = true;
        }

        SyncTtsConfig();
        SaveConfig();
        Console.WriteLine($"[VOICE] Configured: Region={SpeechRegion}, Keyword={KeywordModelPath ?? "none"}, Audio={AudioDevice ?? "default"}");

        if (Enabled && IsConfigured && _listenTask == null)
            Start();
        else if (!Enabled && _listenTask != null)
            Stop();
    }

    /// <summary>Keep TTS service config in sync with our properties.</summary>
    private void SyncTtsConfig()
    {
        _tts.SpeechKey = EffectiveSpeechKey;
        _tts.SpeechRegion = SpeechRegion;
    }

    private void LoadConfig()
    {
        var config = VoiceConfig.Load(_configPath);

        SpeechKey = config.SpeechKey;
        SpeechRegion = config.SpeechRegion;
        KeywordModelPath = config.KeywordModelPath;
        AudioDevice = config.AudioDevice;
        VideoDevice = config.VideoDevice;
        DefaultStyle = config.DefaultStyle ?? "";
        SpeechLanguage = config.SpeechLanguage ?? "de-DE";
        DisplayDurationSeconds = config.DisplayDurationSeconds > 0 ? config.DisplayDurationSeconds : 60;
        SilenceTimeoutMs = config.SilenceTimeoutMs > 0 ? config.SilenceTimeoutMs : 3500;
        ProfanityFilter = config.ProfanityFilter ?? "raw";
        SegmentationStrategy = config.SegmentationStrategy ?? "Semantic";
        TtsEnabled = config.TtsEnabled;
        TtsVoiceName = config.TtsVoiceName ?? "de-DE-ConradNeural";
        MusicAudioOnly = config.MusicAudioOnly;
        SaveGeneratedImages = config.SaveGeneratedImages;
        Enabled = config.Enabled;
        TtsDuckingEnabled = config.TtsDuckingEnabled;
        TtsDuckVolumePercent = config.TtsDuckVolumePercent > 0 ? config.TtsDuckVolumePercent : 15;

        SyncTtsConfig();
    }

    private void SaveConfig()
    {
        var config = new VoiceConfig
        {
            SpeechKey = SpeechKey,
            SpeechRegion = SpeechRegion,
            KeywordModelPath = KeywordModelPath,
            AudioDevice = AudioDevice,
            VideoDevice = VideoDevice,
            DefaultStyle = DefaultStyle,
            SpeechLanguage = SpeechLanguage,
            DisplayDurationSeconds = DisplayDurationSeconds,
            SilenceTimeoutMs = SilenceTimeoutMs,
            ProfanityFilter = ProfanityFilter,
            SegmentationStrategy = SegmentationStrategy,
            TtsEnabled = TtsEnabled,
            TtsVoiceName = TtsVoiceName,
            MusicAudioOnly = MusicAudioOnly,
            SaveGeneratedImages = SaveGeneratedImages,
            Enabled = Enabled,
            TtsDuckingEnabled = TtsDuckingEnabled,
            TtsDuckVolumePercent = TtsDuckVolumePercent
        };
        config.Save(_configPath);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 1)] + "…";

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

public enum VoiceState
{
    Disabled,
    Idle,
    WaitingForKeyword,
    Listening,
    Processing,
    Generating,
    Speaking,
    Displaying,
    Error
}
