# Voice Command Routing Plan

> Saved for future implementation. This extends the current voice-triggered AI image generation
> to support controlling all display functions via voice commands.

## Architecture: Intent-based Command Routing

### Flow

1. **Speech -> Text** (already working via Azure STT + parec)
2. **Text -> Intent Classification** using a lightweight Azure OpenAI prompt
3. **Intent -> Action** via a dispatcher that calls the appropriate service

### Intent Classification

Send the transcribed text to Azure OpenAI with a system prompt like:

```
You are a voice command classifier for an LED matrix display system.
Classify the user's spoken command into one of these intents:

- generate-image: User wants to create/generate an AI image (e.g. "paint a dragon", "make a picture of...")
- media-play: Play/resume media (e.g. "play", "weiter", "abspielen")
- media-pause: Pause media (e.g. "pause", "stopp", "anhalten")
- media-stop: Stop media completely (e.g. "stop", "aufhören")
- media-next: Next track/video (e.g. "nächstes", "skip", "weiter")
- media-previous: Previous track (e.g. "zurück", "vorheriges")
- media-volume: Change volume (e.g. "Lautstärke auf 50", "lauter", "leiser")
- media-play-url: Play a specific URL or search term (e.g. "spiel Lofi Hip Hop auf YouTube")
- switch-extension: Switch to a display extension (e.g. "zeig die Uhr", "lade Tetris")
- set-brightness: Change display brightness (e.g. "Helligkeit auf 80 Prozent")
- show-gallery: Show saved AI gallery image (e.g. "zeig Galerie", "Diashow starten")
- alert-test: Test the camera alert (e.g. "teste Alarm")
- unknown: Command not recognized

Return JSON: {"intent": "...", "parameters": {...}}

Parameters by intent:
- generate-image: {"prompt": "the full image description"}
- media-volume: {"level": 0-100} or {"direction": "up"|"down", "step": 10}
- media-play-url: {"query": "search term or URL"}
- switch-extension: {"name": "extension name"}
- set-brightness: {"level": 0-100}
- show-gallery: {"action": "slideshow"|"random"|"latest"}
```

### Example Classifications

| Spoken (German)                    | Intent            | Parameters                          |
|------------------------------------|-------------------|-------------------------------------|
| "Mach ein Bild von einem Drachen"  | generate-image    | {prompt: "ein Drachen"}             |
| "Spiel das nächste Video"          | media-next        | {}                                  |
| "Lautstärke auf 50 Prozent"       | media-volume      | {level: 50}                         |
| "Zeig die Uhr"                    | switch-extension  | {name: "clock"}                     |
| "Pause"                           | media-pause       | {}                                  |
| "Helligkeit runter"               | set-brightness    | {direction: "down"}                 |
| "Spiel Lofi Hip Hop auf YouTube"  | media-play-url    | {query: "Lofi Hip Hop"}             |

### Implementation Steps

1. **New `VoiceCommandRouter` class** in `Services/`
   - `ClassifyIntentAsync(string transcription)` - single LLM API call (~200ms)
   - Uses same Azure OpenAI credentials as AiImageService
   - Returns `VoiceIntent` record with intent name + parameters

2. **Intent handler registry**: `Dictionary<string, Func<JsonElement, Task<string>>>`
   - Each handler calls existing services (MediaPlayerService, extension management, etc.)
   - Returns a feedback message to show on display

3. **Modify `RecognizeSpeechAndGenerateAsync`** in VoiceCommandService
   - After STT transcription, route through `VoiceCommandRouter` first
   - If intent is `generate-image`, proceed with current AI image flow
   - For other intents, execute the handler and show brief feedback

4. **Service dependencies** needed:
   - `MediaPlayerService` - for media control intents
   - `CanvasContentManager` - for extension switching
   - `DisplayLayoutManager` - for brightness control
   - `AiImageService` - for gallery/slideshow intents

### Advantages

- **Language-agnostic**: LLM handles German, English, mixed languages naturally
- **Fuzzy matching**: Users can phrase commands many ways
- **Single extra API call**: ~200ms latency, negligible vs image generation
- **Extensible**: Adding new commands = new intent + handler, no retraining
- **Graceful fallback**: Unknown intents can default to image generation

### Complexity Assessment

**Medium**. The speech pipeline and all target services already exist. This is mainly:
- Adding a classification layer between STT and action
- Writing thin handler functions that call existing service methods
- Testing LLM accuracy for German command classification
