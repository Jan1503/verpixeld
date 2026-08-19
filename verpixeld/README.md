<details>
<summary>2026-02-15: Major overhaul of web-gui to optimize UX</summary>
<div align="center">
<img width="1120" height="1123" alt="image" src="https://github.com/user-attachments/assets/79e0cd3b-3f1b-43f3-a422-12bddc4eaf9f" />
</div>
</details>

---
<details>
<summary>2026-01-30: Sneak peek of the web-gui...</summary>
<div align="center">
<img width="1108" height="1384" alt="image" src="https://github.com/user-attachments/assets/7852da20-f50e-4389-89b5-fb5def7930e2" />
</div>
</details>

---
<div align="center">
<img width="942" height="338" alt="image" src="https://github.com/user-attachments/assets/54668fcd-eccb-4e52-96cb-eed5bc0098d1" />
</div>

---
<div align="center">

# 🎨 verpixeld

### LED Matrix Control System

*Transform your RGB LED matrix into a dynamic, controllable display*

[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple?style=for-the-badge)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Raspberry%20Pi%20%2B%20network%20panel-red?style=for-the-badge)](https://www.raspberrypi.org/)
[![License](https://img.shields.io/badge/License-GPL--3.0--or--later-blue?style=for-the-badge)](#-license)

</div>

---

## 🌟 What is verpixeld?

**verpixeld** is a .NET 10 LED display host: it composes multi-layer canvas content and drives it from one web UI. It started on a Raspberry Pi with HUB75 panels and now talks to several output backends.

| `App.OutputMode` | Backend | Typical hardware |
|------------------|---------|------------------|
| `gpio` | [rpi-rgb-led-matrix](https://github.com/hzeller/rpi-rgb-led-matrix) | Pi GPIO → HUB75 wall |
| `network` | [PixPlane](https://github.com/Jan1503/pixplane) UDP :7777 | [verpixeld-panel](https://github.com/Jan1503/verpixeld-panel) (RP2350 + W6300, ICND1065L 256×128) |
| `hdmi` | Linux framebuffer `/dev/fb0` | HDMI → sender card (Colorlight / Novastar style) |
| `spi` | SPI `VPX2` bit-planes | Pi → RP2040 receiver bridge |
| `simulation` | no-op + MJPEG preview | Any machine, no panel |

Switch backends in **Settings → Output**. Network / HDMI / SPI / simulation can live-swap when the pixel size matches. GPIO always needs a process restart (the native matrix library owns realtime GPIO). GPIO init failure falls back to simulation so the web UI stays up.

The canvas engine, extensions and filters live in the sibling [CanvasManagement](#-related-projects) library. Desktop capture is [DeskCast](https://github.com/Jan1503/deskcast).

Whether you want to show the time, display weather information, run animations, stream your camera, play YouTube videos, or create collaborative pixel art – verpixeld provides a unified platform with:

- 🖥️ **Beautiful Web Control Panel** — Control everything from any device with a browser
- 🧩 **Plugin Architecture** — Extend functionality with custom content extensions
- 🎨 **Real-time Visual Filters** — Apply effects like blur, color correction, and more
- 📐 **Flexible Layouts** — Multi-canvas support with independent layers and opacity
- ⏰ **Smart Scheduling** — Automate content changes based on time
- 🌙 **Night Mode** — Automatic brightness adjustment for different times of day
- 📷 **Camera Streaming** — Stream live video from your phone to the display
- ✏️ **Drawing Mode** — Create pixel art directly on the display
- 🎬 **Media Player** — Full video/audio playback with YouTube, network shares, and local files
- ⭐ **Favorites & History** — Save and quickly replay media with remembered settings
- 🔊 **Bluetooth Audio** — Output audio to Bluetooth speakers via PulseAudio
- 📹 **Camera Motion Alerts** — Auto-switch to a camera feed when motion is detected
- 🖼️ **Image & Video Upload** — Upload photos or stream video clips from any device to the display
- 🏠 **Home Assistant Integration** — Live sensor tiles, a multi-entity dashboard grid, and history graphs (temperature, power, …) via the HA WebSocket + History API, with a searchable entity picker
- 🧲 **Live Layer Editor** — Drag, resize, reorder and configure canvases directly over the live display preview, with per-canvas transparent backgrounds
- 🕒 **Rich Clocks & Extensions** — Flexible Digital Clock (12/24h, BDF/seven-segment, glow, colour cycle) plus many extensions: games (Pac-Man, Snake, Pong, Tetris, Space Invaders, Dino, Flappy Bird), Weather, News Ticker, Now Playing, Falling Sand, and more
- 🤖 **AI Art Generation** — Generate images with Azure OpenAI or OpenAI, with image-to-image stylization, gallery storage, and scheduled auto-generation
- 🎙️ **Voice Assistant** — Hands-free voice commands with wake word detection, fast-path instant execution, intent classification, spoken responses with audio ducking, via Azure Speech + OpenAI
- 🎵 **Music Search & Radio** — Search and play YouTube Music songs, start endless genre radio, or tune into internet radio stations (Radio Browser) by voice or through the web UI
- 📹 **Voice Camera Control** — Show or dismiss camera feeds (IP/RTSP alert camera or USB webcam) by voice command
- 📺 **Live Matrix Preview** — Real-time MJPEG stream of the LED matrix output viewable from the web UI, with scalable pixelated rendering
- 🖥️ **Simulation Mode** — Run the full application without physical LED hardware for development and testing
- 🔒 **Certificate Management** — Upload custom HTTPS certificates or regenerate self-signed ones through the web interface
- 📡 **Network panels** — Discover (`VPXD` UDP 7778), bind by chip-id (DHCP-safe), identify-flash, and stream 8/14-bit planes with live seam correction
- 🖥️ **HDMI / SPI outputs** — Drive a sender card via framebuffer, or an RP2040 SPI bridge
- 🪟 **DeskCast** — Windows region/window capture straight to the panel (PixPlane) or into verpixeld via the Network Stream Player (TPM2.NET)

---

## 🔗 Related projects

Local layout (siblings under `RGB-Display/`):

```
verpixeld/           this host (web UI, media, outputs)
CanvasManagement/    canvas engine, extensions, filters, deploy.ps1
pixplane/            BGRA → bit-plane UDP library
DesktopStreamer/     DeskCast (Windows capture)
verpixeld-panel/     RP2350 + W6300 firmware
```

| Repo | Role |
|------|------|
| [verpixeld](https://github.com/Jan1503/verpixeld) | This application |
| [pixplane](https://github.com/Jan1503/pixplane) | Panel protocol (stream, discovery, `/cmd`) |
| [verpixeld-panel](https://github.com/Jan1503/verpixeld-panel) | ICND1065L firmware 1.3 |
| [deskcast](https://github.com/Jan1503/deskcast) | Windows desktop → panel or verpixeld |
| [rpi-rgb-led-matrix](https://github.com/hzeller/rpi-rgb-led-matrix) | GPIO HUB75 driver (not vendored) |

CanvasManagement is the shared framework; it is not on GitHub yet.

---

## ✨ Features

### 🖼️ Display Management

| Feature | Description |
|---------|-------------|
| **Multi-Canvas System** | Layer multiple content sources with independent z-ordering, opacity, and brightness |
| **Layout Profiles** | Pre-defined layouts: FullScreen, HeaderContent, ThreePanel, SplitView, Dashboard |
| **Custom Overlays** | Create positioned overlay canvases for notifications, clocks, etc. |
| **Hot Reload** | Change content and settings without restarting |
| **Output backends** | `gpio` / `network` / `hdmi` / `spi` / `simulation` — pick in Settings |
| **Panel discovery** | LAN scan + bind by `PanelId`; identify flash on the wall |
| **Seam correction** | Per-column gain/lift for ICND scan-home columns (network path) |
| **Image correction** | Global gamma / contrast / brightness / white-balance (all modes) |

### 🧩 Extensions (Content Plugins)

Extensions are dynamically loaded plugins that provide content for canvases:

- Clock displays (analog, digital, world time)
- Weather information
- RSS/News feeds
- Image slideshows
- Animations and visualizations
- Custom content via plugin API

### 🏠 Home Assistant Integration

Pull live state from Home Assistant over its WebSocket API (long-lived token, kept server-side):

- **HA Sensor** — one entity as a tile: value + unit + icon, threshold colouring, on/off badge, "last changed" age, state remapping, optional history sparkline
- **HA Grid** — several entities on one canvas (compact dashboard)
- **HA Graph** — a history line/area chart for a numeric entity (e.g. temperature/power), seeded from the HA History API and updated live
- **Searchable entity picker** in the web UI; icons drawn from the entity's `mdi:` icon / domain

### 🧲 Live Layer Editor

- Drag, resize and reorder canvases directly over the live MJPEG preview
- Per-canvas **opacity**, **z-order**, **rename**, and **transparent background** (alpha compositing reveals layers beneath)
- Extensions reflow to the new size automatically

### 🎨 Visual Filters

Real-time post-processing filters applied to the entire display:

- **Color Adjustments**: Brightness, contrast, saturation, hue shift
- **Effects**: Blur, sharpen, pixelate, noise
- **Artistic**: Color tint, gradient overlay, vignette
- **Corrections**: Gamma, color temperature

### 📅 Scheduling

Automated layout switching based on time with daily/weekly schedules, priorities, and manual override capability.

### 🌙 Night Mode

Automatic brightness management with configurable time ranges and gradual transitions.

### 📷 Camera Streaming

Stream live video from any device camera to the display with configurable FPS and real-time downsampling.

### ✏️ Drawing Mode

Interactive drawing with freehand tools, shapes, color picker, and the ability to save/load drawings.

### 🎬 Media Player

Full video and audio playback system powered by FFmpeg:

- **Local Files** — Play videos and audio from the device filesystem
- **Network Streaming** — Native SMB/CIFS support via FFmpeg libsmbclient (no mount required)
- **YouTube** — Stream YouTube videos via `yt-dlp` with automatic format selection
- **Generic Streams** — Play any HTTP/HTTPS/RTSP/RTMP stream URL directly (e.g. IP cameras)
- **Audio-Only Mode** — Efficient playback for MP3/FLAC/etc without video decoding overhead
- **Bluetooth Audio** — Output audio to Bluetooth speakers via PulseAudio
- **A/V Sync Control** — Real-time audio/video synchronization with configurable offset (±5 seconds)
- **Configurable Video Scaling** — Choose FFmpeg scale filter per stream (area, lanczos, bicubic, gauss, etc.)
- **Hardware Acceleration** — V4L2 M2M hardware decoding on Raspberry Pi for efficient video playback
- **Seeking Support** — Full seek support for local and network files
- **Metadata Extraction** — ID3 tags and container metadata (title, artist, album, etc.)
- **Playlist Support** — Queue management with shuffle, repeat, and auto-advance
- **Pause/Resume** — Signal-based pause using SIGSTOP/SIGCONT (Linux)
- **Pre-buffering** — Configurable frame buffering for smooth A/V sync on network streams
- **Audio Visualizer** — Real-time FFT-based audio visualization with multiple modes and color schemes

### ⭐ Favorites & History

Save and replay your media with full context:

- **Favorites** — Save any currently playing media (video, audio, YouTube, network stream) with a custom name
- **A/V Sync Remembered** — Audio sync offset is saved per-favorite and re-applied on playback
- **Scale Filter Remembered** — The chosen video scaling algorithm is saved and restored
- **Thumbnail Extraction** — Automatic thumbnail generation for videos in favorites and history lists
- **Recently Played History** — Persistent list of recently played media with one-click replay
- **Auto-Play** — Sequential or shuffled playback through your entire favorites list with animated loading screens between tracks

### 📹 Camera Motion Alerts

Automatic camera feed display triggered by motion detection webhooks:

- **Webhook Trigger** — Simple `POST /api/alert/trigger` endpoint for any camera's HTTP action
- **Auto-Display** — Pauses current media playback and shows camera stream on a high-priority overlay canvas
- **Auto-Dismiss** — Configurable timeout (5–120 seconds) with automatic return to normal
- **Re-trigger Reset** — Consecutive motion events reset the timeout timer
- **Manual Dismiss** — Dismiss button in the GUI or via API
- **Resume Playback** — Automatically resumes paused media when the alert ends
- **Animated Connecting Screen** — Surveillance-style animated overlay while the camera stream connects
- **Double-Buffered Rendering** — Decode pipeline decoupled from display for flicker-free camera feed
- **RTSP Optimized** — TCP transport, low-latency flags, and tuned probe settings for IP cameras
- **Configurable Scale Filter** — Choose the downscaling algorithm for the camera stream
- **Persistent Config** — Stream URL, timeout, and settings saved to disk

### 🖼️ Image & Video Upload

Upload media directly from any device (phone, tablet, desktop) to the LED matrix:

- **Photo Upload** — Select or drag-drop images (JPG, PNG, GIF, WebP) to instantly display on the matrix
- **Video Upload** — Load video files, seek to any frame, and stream frames to the display at configurable FPS
- **Drag & Drop** — Full drag-and-drop support in the web interface
- **Auto-Scaling** — Images automatically scaled to the display resolution
- **Uses Existing Pipeline** — Leverages the `/api/draw/apply` endpoint, no new backend needed

### 🤖 AI Art Generation

Generate unique artwork for your LED matrix using AI image generation:

- **Azure OpenAI (Default)** — Supports DALL-E 3 and GPT Image models via Azure credits
- **OpenAI (Alternative)** — Direct OpenAI API support for DALL-E 3, GPT Image 1, GPT Image 1 Mini
- **Text-to-Image** — Describe what you want and the AI generates it, optimized for LED matrix display
- **Image-to-Image** — Upload a photo and have the AI stylize it (pixel art, watercolor, cyberpunk, etc.)
- **Style Presets** — Pixel Art, Retro 8-bit, Neon Synthwave, Abstract, Photograph, Watercolor, Oil Painting, Comic, Minimalist, Cyberpunk
- **Quality Control** — Low/Medium/High quality settings to balance speed and detail
- **Generation History** — Browse and re-apply past generations with one click
- **Scheduled Auto-Generation** — Configure prompts and intervals to auto-generate fresh artwork periodically
- **Live Preview** — See generated images before applying them to the display
- **Gallery with Overlay Display** — Save generated images to a gallery, browse thumbnails, and apply images to the display via an overlay canvas (z=250) that stays visible above running extensions until dismissed
- **Gallery Slideshow** — Auto-cycle through gallery images with configurable interval and shuffle/sequential order
- **Persistent Configuration** — API keys and schedule settings saved to disk

### 🎙️ Voice Assistant

A full voice assistant that listens for a wake word, understands spoken commands in any language, and responds with actions and spoken audio:

- **Wake Word Detection** — Trigger with a custom keyword (Azure Custom Keyword `.table` model)
- **Unified Keyword + STT Pipeline** — Single audio stream for keyword detection and cloud speech-to-text, eliminating the gap between wake word and command recognition
- **Follow-Up Listening** — If you pause after the wake word ("Hey Pixel" ... "wie spät ist es?"), the system automatically listens for your follow-up command
- **Fast-Path Instant Commands** — Simple commands like "Stop", "Pause", "Leiser", "Kamera aus" execute instantly without LLM roundtrip (~0ms vs 1-3s)
- **Intent Classification via LLM** — Complex commands are routed through Azure OpenAI (GPT-4o/GPT-5) to classify intent and generate a natural-language response
- **Text-to-Speech** — Spoken responses via Azure TTS with configurable voice (German/English voices available)
- **Audio Ducking** — Music volume is automatically lowered during voice responses and restored afterward, so TTS is always clearly audible over background music (configurable volume level)
- **Non-Blocking Feedback** — After the assistant speaks, the response text stays visible on the display for a reading period while the listen loop resumes immediately, so you can speak a new command without waiting
- **Smart Overlay Management** — AI images, camera feeds, and feedback overlays are automatically dismissed when a new voice command is received
- **Stale Audio Prevention** — Audio capture is paused during command processing (LLM, image generation, TTS) and resumed with fresh audio when listening restarts, ensuring instant wake word detection
- **Content Filter Resilience** — When Azure's content filter blocks or drops the LLM response, the system falls back to local intent detection (recognizing German draw commands like "male", "zeichne" automatically)
- **Push-to-Talk** — Manual trigger via web UI button in addition to wake word

Supported voice commands:

| Command Type | Examples | Action |
|---|---|---|
| **AI Image Generation** | "Male einen Drachen", "Paint a sunset" | Generates and displays an AI image (auto-dismissed on next command) |
| **Questions & Chat** | "Wie spät ist es?", "Tell me a joke" | LLM answers, response spoken aloud |
| **Media Control** | "Pause", "Nächstes Lied", "Stop" | Controls media player playback (fast-path, instant) |
| **Volume** | "Lauter", "Leiser", "Ton aus" | Adjusts media volume (fast-path, instant) |
| **Brightness** | "Licht an", "Display aus", "Helligkeit auf 80" | Adjusts LED matrix brightness (fast-path for on/off) |
| **Extension Switching** | "Zeig die Uhr" | Switches active display extension |
| **Music Search** | "Spiele Bohemian Rhapsody", "Play something by Daft Punk" | Searches YouTube Music and plays the top result |
| **Music Radio** | "Spiele Trance Musik", "Spiele Jazz" | Starts endless genre radio — shuffled playback with auto-refill |
| **Internet Radio** | "Spiele Techno Radio", "Play jazz radio" | Searches and plays a live internet radio station |
| **Show Camera** | "Zeig mir die Kamera", "Zeig USB-Kamera" | Shows alert (IP/RTSP) or local USB camera on the display |
| **Hide Camera** | "Kamera aus", "Kamera stopp" | Dismisses any active camera feed (fast-path, instant) |

### 🎵 Music Search & Radio

Search and play music from YouTube Music or internet radio — by voice or through the web interface:

- **YouTube Music Integration** — Search for songs, artists, or albums using the YouTubeMusicAPI (no API key required)
- **Songs vs Music Videos** — Toggle between audio tracks (with album art) and actual music videos
- **Audio-Only Mode** — Play songs without overlaying the display, keeping the current content visible (default for voice commands)
- **Voice-Triggered** — Say "Hey Pixel, spiele Bohemian Rhapsody von Queen" to search and play instantly
- **Genre Radio** — Say "Hey Pixel, spiele Trance Musik" to start endless genre playback with shuffled tracks and automatic queue refill
- **Internet Radio** — Search and play live internet radio stations by genre using the Radio Browser API (free, no API key required)
- **Click-to-Play Results** — Search results displayed as a list with title, artist, album, and duration
- **yt-dlp Playback** — Uses the existing media player pipeline (yt-dlp + FFmpeg) for reliable playback
- **Error Handling** — Restricted or unavailable videos show a clear error message in the UI and via voice

### 🖥️ System Console

Live backend log streaming to the web interface:

- **Real-time Log Viewer** — All `Console.WriteLine` output captured and streamed to a dedicated Console tab
- **Search & Filter** — Filter logs by keyword in real-time
- **Auto-scroll** — Follows new output automatically with pause/resume control
- **Ring Buffer** — Memory-efficient circular buffer keeps recent log history

### 🎵 Audio Output & Bluetooth

Comprehensive audio output management:

- **PulseAudio Integration** — Full control over audio routing and volume
- **Bluetooth Discovery** — Scan, pair, and connect Bluetooth speakers from the web interface
- **Device Selection** — Switch audio output between ALSA, PulseAudio sinks, and Bluetooth devices
- **Volume Control** — System-wide volume adjustment with mute toggle
- **Real-time Updates** — Server-Sent Events for instant UI feedback on volume/device changes

### 📺 Live Matrix Preview

Real-time visualization of the LED matrix output in the web browser:

- **MJPEG Stream** — Live video stream of the composited canvas output, viewable from the Settings tab
- **Zero Overhead** — Frames are only encoded when a viewer is connected; no performance cost when inactive
- **Scalable View** — Adjustable 1x–4x scale slider with pixelated rendering for crisp pixel visibility
- **All Modes** — Works in both hardware mode (remote monitoring) and simulation mode (development)
- **Snapshot API** — Single-frame JPEG capture via `/api/preview/frame`

### 🖥️ Simulation Mode

Run verpixeld without physical LED matrix hardware:

- **No Hardware Required** — Set `"App": { "OutputMode": "simulation" }` (or the older `"SimulationMode": true` when `OutputMode` is empty)
- **Full Feature Parity** — All services, extensions, media playback, and the web UI work identically
- **Live Preview** — Use the Live Matrix Preview to see the canvas output without an LED panel
- **Development Friendly** — Develop and test on Windows/Mac/Linux without a Raspberry Pi

### 🔒 Certificate Management

Manage HTTPS certificates through the web interface:

- **Certificate Info** — View current certificate details (subject, issuer, expiry, thumbprint, self-signed status)
- **Custom Upload** — Upload a `.pfx` / `.p12` certificate with password via the Settings tab
- **Regenerate** — Generate a new self-signed certificate with current local IPs/hostnames
- **Auto-Generation** — Self-signed certificate created automatically on first run if none exists
- **Configurable Path** — Override certificate path and password in `appsettings.json`

---

## 🏗️ Architecture

```text
┌─────────────────────────────────────────────────────────────────┐
│                       Web Control Panel                         │
│                     (HTML/CSS/JavaScript)                       │
│  Tabs: Layouts│Schedule│Canvas│AI│Media│Effects│Voice│Console│  │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                        ASP.NET Core API                         │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐            │
│  │  Layout  │ │  Media   │ │ YouTube  │ │Favorites │            │
│  │Endpoints │ │Endpoints │ │Endpoints │ │Endpoints │            │
│  ├──────────┤ ├──────────┤ ├──────────┤ ├──────────┤            │
│  │  Alert   │ │   AI     │ │  Audio   │ │  Log     │            │
│  │Endpoints │ │Endpoints │ │Endpoints │ │Endpoints │            │
│  ├──────────┤ ├──────────┤ ├──────────┤ └──────────┘            │
│  │  Voice   │ │  Music   │ │ Preview  │                         │
│  │Endpoints │ │Endpoints │ │Endpoints │                         │
│  └──────────┘ └──────────┘ └──────────┘                         │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                         Core Services                           │
│  ┌────────────────┐ ┌────────────────┐ ┌─────────────────────┐  │
│  │LayoutManager   │ │ContentManager  │ │ ScheduleManager     │  │
│  ├────────────────┤ ├────────────────┤ ├─────────────────────┤  │
│  │MediaPlayerSvc  │ │ AlertService   │ │ FavoritesService    │  │
│  ├────────────────┤ ├────────────────┤ ├─────────────────────┤  │
│  │AudioOutputSvc  │ │AiImageService  │ │ NetworkShareService │  │
│  ├────────────────┤ ├────────────────┤ ├─────────────────────┤  │
│  │ LogService     │ │AiChatService   │ │ MusicSearchService  │  │
│  ├────────────────┤ ├────────────────┤ ├─────────────────────┤  │
│  │VoiceCommandSvc │ │RadioBrowserSvc │ │ FrameStreamService  │  │
│  ├────────────────┤ ├────────────────┤ ├─────────────────────┤  │
│  │CertificateSvc  │ │MediaProbeSvc   │ │ FfmpegCapabilities  │  │
│  └────────────────┘ └────────────────┘ └─────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                          Canvas Management                      │
│             (Multi-layer composition, z-ordering & filters)     │
│                                                                 │
│  z=100: Extensions   z=200: Media   z=250: AI/Gallery Overlay   │
│  z=300: CameraAlert  z=350: VoiceFeedback                       │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                       OutputRuntime                             │
│                  (one active IMatrixRenderer)                   │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌────────┐ │
│  │  gpio    │ │ network  │ │   hdmi   │ │   spi    │ │  sim   │ │
│  │ HUB75    │ │ PixPlane │ │ /dev/fb0 │ │ VPX2     │ │preview │ │
│  │ rpi-rgb  │ │ UDP:7777 │ │  HDMI    │ │ spidev   │ │        │ │
│  └──────────┘ └──────────┘ └──────────┘ └──────────┘ └────────┘ │
│                        │                                        │
│                        ▼                                        │
│               ┌──────────────────┐                              │
│               │ FrameStreamSvc   │ ──► MJPEG Live Preview       │
│               │ (on-demand JPEG) │                              │
│               └──────────────────┘                              │
└─────────────────────────────────────────────────────────────────┘
```

### 🎵 Media Player Architecture

```text
┌─────────────────────────────────────────────────────────────────┐
│                      MediaPlayerService                         │
│              (Orchestrates video/audio playback)                │
│  ┌───────────────┐  ┌──────────────────┐  ┌──────────────────┐  │
│  │  VideoPlayer  │  │ AlsaAudioService │  │ NetworkShareSvc  │  │
│  │  (FFmpeg)     │  │  (System Volume) │  │ (SMB Credentials)│  │
│  └───────────────┘  └──────────────────┘  └──────────────────┘  │
│  ┌───────────────┐  ┌──────────────────┐  ┌──────────────────┐  │
│  │ YouTubeService│  │ FavoritesService │  │ Auto-Play Queue  │  │
│  │  (yt-dlp)     │  │ (JSON Persist)   │  │ (Sequential/Shuf)│  │
│  └───────────────┘  └──────────────────┘  └──────────────────┘  │
│  ┌───────────────┐  ┌──────────────────┐                        │
│  │MediaProbeSvc  │  │FfmpegCapabilities│                        │
│  │(metadata/info)│  │(avail/hw checks) │                        │
│  └───────────────┘  └──────────────────┘                        │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                      AudioOutputService                         │
│         (PulseAudio/ALSA routing, Bluetooth management)         │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                        AlertService                             │
│          (Camera motion alerts, independent canvas z=300)       │
│  ┌───────────────┐  ┌──────────────────┐  ┌──────────────────┐  │
│  │ FFmpeg Decode │  │ Double-Buffered  │  │  Auto-Dismiss    │  │
│  │ (RTSP/HTTP)   │  │ Display Loop     │  │  Timer           │  │
│  └───────────────┘  └──────────────────┘  └──────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

**Supported Protocols:**

- `smb://` — SMB/CIFS network shares (requires FFmpeg with libsmbclient)
- `rtsp://` — RTSP camera streams (TCP transport, optimized for IP cameras)
- `http://` / `https://` — HTTP streams, HLS, HTTP-FLV
- `rtmp://` — RTMP streams
- YouTube URLs — via `yt-dlp` automatic format extraction
- Local filesystem paths

---

## 🚀 Getting Started

### Prerequisites

Depends on the output you use:

| Path | You need |
|------|----------|
| **Network panel** | Panel flashed with [verpixeld-panel](https://github.com/Jan1503/verpixeld-panel), LAN, `OutputMode: "network"` |
| **Pi GPIO HUB75** | Raspberry Pi 4 (or 3), HUB75 wall, [rpi-rgb-led-matrix](https://github.com/hzeller/rpi-rgb-led-matrix) built as a sibling |
| **HDMI wall** | Pi with `/dev/fb0` + sender card, `OutputMode: "hdmi"` |
| **SPI bridge** | Pi SPI enabled + RP2040 firmware, `OutputMode: "spi"` |
| **Dev / no hardware** | Any OS, `OutputMode: "simulation"`, Live Preview in Settings |
| **Desktop mirroring** | [DeskCast](https://github.com/Jan1503/deskcast) on Windows → panel (`:7777`) or verpixeld Network Stream Player (`:65506`) |

Common stack:

- .NET 10.0 ASP.NET Core Runtime (framework-dependent publish)
- FFmpeg (PulseAudio + libsmbclient — see [compilation guide](docs/FFMPEG_SMB.md))
- `yt-dlp` (optional, YouTube — install a current binary, not the apt package)
- Sibling checkouts: `CanvasManagement`, `pixplane` (and rpi-rgb-led-matrix for GPIO)

### Installation

1. **Clone the siblings**
   ```bash
   git clone https://github.com/Jan1503/verpixeld.git
   git clone https://github.com/Jan1503/pixplane.git
   # CanvasManagement is not public yet — keep the local sibling next to verpixeld
   # Copy the template, then edit OutputMode / Network.Host / Matrix size.
   cp verpixeld/appsettings.example.json verpixeld/appsettings.json
   # appsettings.json is gitignored (LAN IPs, cert password, HA token).
   ```

2. **GPIO only — build rpi-rgb-led-matrix** (on the Raspberry Pi)

   The LED hardware driver must be compiled from source. Clone it as a sibling directory and build both the native C library and the C# bindings:

   ```bash
   git clone https://github.com/hzeller/rpi-rgb-led-matrix.git rpi-rgb-led-matrix-master
   cd rpi-rgb-led-matrix-master
   make
   cd bindings/c#
   dotnet build
   ```

   After building, copy `lib/librgbmatrix.so.1` next to `verpixeld.dll`. Skip this step for network / HDMI / SPI / simulation.

3. **Build & gather everything** from the **CanvasManagement** folder (bundles the app + all extensions, filters and fonts):
   ```powershell
   ./deploy.ps1 -Configuration Release -Rid linux-arm64 -FontsSource <folder-with-clean-.bdf-files>
   ```
   Or build just the host: `dotnet publish -c Release -r linux-arm64` from `verpixeld/verpixeld/`.

4. **Deploy to Raspberry Pi**
   ```bash
   rsync -av --exclude appsettings.json --exclude Layouts/ --exclude server.pfx \
     deploy/ pi@raspberrypi:/home/pi/verpixeld/
   ```

5. **Pick an output** in `appsettings.json` (or later in the web UI):
   ```json
   "App": { "OutputMode": "network", "DisplayWidth": 256, "DisplayHeight": 128 }
   "Network": { "Port": 7777, "ColorBits": 14, "PanelId": "<chip-id or empty>" }
   ```
   `ColorBits` must match the firmware (`8` or `14`). Empty `PanelId` uses `Network.Host`; a bound id is re-resolved at boot (DHCP-safe).

6. **systemd** — see [Raspberry Pi Setup Guide](docs/RASPBERRY_PI_SETUP.md). For updates, [UPDATE.md](../../CanvasManagement/UPDATE.md).

7. **Web UI**
   - HTTP: `http://<pi-ip>:5000`
   - HTTPS: `https://<pi-ip>:5001`

---

## 🔌 API Reference

verpixeld exposes a comprehensive REST API for integration with external systems.

### Key Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/media/status` | Full media player status (playback, position, metadata, alert state) |
| `POST` | `/api/media/play/{filename}` | Play a local video file |
| `POST` | `/api/media/pause` | Toggle pause/resume |
| `POST` | `/api/media/stop` | Stop playback |
| `POST` | `/api/media/seek` | Seek to position |
| `POST` | `/api/media/scale-filter` | Set the video scaling algorithm |
| `GET` | `/api/media/scale-filters` | List available FFmpeg scale filters |
| `POST` | `/api/youtube/play` | Play a YouTube URL or generic stream |
| `GET` | `/api/favorites` | List all favorites |
| `POST` | `/api/favorites/add-current` | Save currently playing media as favorite |
| `POST` | `/api/favorites/{id}/play` | Play a saved favorite |
| `POST` | `/api/favorites/auto-play/start` | Start auto-play through favorites |
| `POST` | `/api/favorites/auto-play/stop` | Stop auto-play |
| `GET` | `/api/favorites/history` | Get recently played history |
| `POST` | `/api/alert/trigger` | **Webhook**: Trigger camera motion alert |
| `POST` | `/api/alert/dismiss` | Dismiss active camera alert |
| `GET` | `/api/alert/status` | Get alert status and configuration |
| `POST` | `/api/alert/configure` | Configure camera stream URL and timeout |
| `POST` | `/api/ai/generate` | Generate an image from a text prompt |
| `POST` | `/api/ai/edit` | Image-to-image: stylize an uploaded photo |
| `POST` | `/api/ai/apply` | Apply a generated image to the display (overlay) |
| `POST` | `/api/ai/dismiss` | Dismiss the image overlay from the display |
| `GET` | `/api/ai/gallery` | List saved gallery images |
| `GET` | `/api/ai/gallery/{filename}` | Get a gallery image as base64 |
| `DELETE` | `/api/ai/gallery/{filename}` | Delete a gallery image |
| `GET` | `/api/ai/status` | AI provider status and configuration |
| `POST` | `/api/ai/configure` | Configure AI provider (Azure/OpenAI) |
| `POST` | `/api/ai/schedule` | Configure scheduled auto-generation |
| `GET` | `/api/ai/history` | Get generation history |
| `GET` | `/api/voice/status` | Voice assistant status, config, and last command info |
| `POST` | `/api/voice/configure` | Configure voice settings (speech key, TTS, language, etc.) |
| `POST` | `/api/voice/start` | Start voice listening |
| `POST` | `/api/voice/stop` | Stop voice listening |
| `POST` | `/api/voice/trigger` | Manual push-to-talk trigger |
| `POST` | `/api/music/search` | Search YouTube Music (songs or music videos) |
| `POST` | `/api/music/play` | Play a music search result or search-and-play by query |
| `GET` | `/api/audio/status` | Audio output and Bluetooth status |
| `GET` | `/api/logs/recent` | Get recent console log entries |
| `GET` | `/api/preview/stream` | MJPEG live stream of the LED matrix output |
| `GET` | `/api/preview/frame` | Single JPEG snapshot of the current frame |
| `GET` | `/api/preview/status` | Preview stream status (client count, active state) |
| `GET` | `/api/settings/certificate` | Current HTTPS certificate information |
| `POST` | `/api/settings/certificate/upload` | Upload a custom PFX/P12 certificate |
| `POST` | `/api/settings/certificate/regenerate` | Regenerate self-signed certificate |
| `GET` | `/api/settings/outputs` | Snapshot of active/saved output mode and per-backend options |
| `PUT` | `/api/settings/output` | Switch output mode (`gpio` / `network` / `hdmi` / `spi` / `simulation`) |
| `GET` | `/api/settings/network/discover` | LAN scan for verpixeld-panel (UDP 7778 + HTTP `/status`) |
| `POST` | `/api/settings/network/identify` | Flash the bound panel |
| `GET`/`POST` | `/api/settings/seam` | Per-column seam correction (network path) |
| `GET` | `/health` | Health check endpoint |

### Camera Alert Webhook

The camera alert system is designed for easy integration with IP cameras. Configure your camera's motion detection to call:

```bash
curl -X POST http://<verpixeld-host>:5000/api/alert/trigger
```

No body, no authentication, no parameters needed. The endpoint returns `200 OK` immediately. Compatible with Reolink, Hikvision, Dahua, and any camera that supports HTTP webhook actions.

---

## 🎙️ Voice Assistant & AI Setup

The voice assistant and AI art features require Azure cloud services. This section covers everything needed to get them running.

### Azure Resources Required

You need **two** Azure resources (both have generous free tiers):

| Resource | Used For | Free Tier |
|---|---|---|
| **Azure OpenAI** | Image generation + intent classification (chat) | Pay-as-you-go (see costs below) |
| **Azure Speech Services** | Speech-to-text, text-to-speech, wake word | 5 hours STT + 500K TTS chars/month free |

### Step 1: Create Azure OpenAI Resource

1. Go to [Azure Portal](https://portal.azure.com/) > **Create a resource** > search **"Azure OpenAI"**
2. Select your subscription and resource group
3. Choose a region (e.g. `swedencentral`, `eastus`) — check [model availability](https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/models)
4. Select pricing tier **Standard S0**
5. Click **Create**

Once created, note:
- **Endpoint**: Found in **Keys and Endpoint** (e.g. `https://myresource.openai.azure.com/`)
- **API Key**: Found in **Keys and Endpoint** (Key 1 or Key 2)

### Step 2: Deploy Models in Azure OpenAI

You need **two** model deployments:

#### Image Model (for AI art generation)

1. Go to [Azure AI Foundry](https://ai.azure.com/) or Azure Portal > your OpenAI resource > **Model Deployments**
2. Click **Create new deployment**
3. Select model: **gpt-image-1** (recommended) or **dall-e-3**
4. Name the deployment (e.g. `gpt-image-1`)
5. Click **Create**

#### Chat Model (for voice assistant intent routing & Q&A)

1. Click **Create new deployment** again
2. Select model — recommended options (best to cheapest):
   - **gpt-5** — Best quality, 75% cheaper input tokens than GPT-4o, 400K context
   - **gpt-5-mini** — Great quality, very cheap ($0.25/M input tokens)
   - **gpt-4o** — Proven reliable, widely available
   - **gpt-4o-mini** — Budget option, adequate for intent classification
3. Name the deployment (e.g. `gpt-5-mini` or `gpt-4o`)
4. Click **Create**

> **Cost note:** Each voice command makes one chat call (~100-300 tokens) for intent classification. With `gpt-5-mini` that's about $0.0001 per command. Even heavy use (100 commands/day) costs less than $1/month.

### Step 3: Create Azure Speech Services Resource

1. Go to [Azure Portal](https://portal.azure.com/) > **Create a resource** > search **"Speech"**
2. Select **Speech Services**
3. Choose your subscription, resource group, and region
4. Select pricing tier **Free F0** (5 hours STT + 500K TTS characters/month) or **Standard S0**
5. Click **Create**

Once created, note:
- **Key**: Found in **Keys and Endpoint** (Key 1)
- **Region**: e.g. `westeurope`, `eastus`

### Step 4: (Optional) Create a Custom Wake Word

A custom wake word (e.g. "Hey Pixel") allows hands-free activation:

1. Go to [Speech Studio](https://speech.microsoft.com/) > **Custom Keyword**
2. Click **Create new model**
3. Enter your wake word phrase (e.g. "Hey Pixel")
4. Click **Create** and wait for training (~10 minutes)
5. Download the `.table` model file
6. Upload it via the verpixeld Voice Settings in the web UI

### Step 5: USB Microphone Setup (Raspberry Pi)

The voice assistant requires a USB microphone on the Raspberry Pi:

```bash
# Verify USB mic is detected
arecord -l

# Check PulseAudio sees it
pactl list sources short
```

The microphone source name (e.g. `alsa_input.usb-Lenovo_Lenovo_510_Camera-...`) will appear in the voice settings dropdown.

### Step 6: Configure in verpixeld Web UI

1. **AI Art tab > Settings subtab:**
   - Provider: Azure
   - Azure Endpoint: `https://yourresource.openai.azure.com/`
   - Azure API Key: your key
   - Image Deployment: `gpt-image-1` (from Step 2)
   - Chat Deployment: `gpt-5-mini` (from Step 2)
   - Click **Save**

2. **AI Art tab > Voice subtab:**
   - Azure Speech Key: your Speech key (from Step 3)
   - Azure Region: your Speech region (e.g. `westeurope`)
   - Speech Language: `de-DE` (German) or `en-US` (English)
   - Microphone: Select your USB mic from the dropdown
   - Voice Responses: Enabled
   - TTS Voice: Choose a voice (e.g. `Conrad (DE, Male)`)
   - Audio Ducking: Enabled (lowers music volume during speech)
   - Duck Volume: 15% (how quiet music gets during speech)
   - Upload wake word `.table` file (optional, from Step 4)
   - Click **Save Voice Settings**
   - Click **Start Listening**

### Voice Assistant Architecture

```text
┌──────────────┐      ┌─────────────────────────────────┐
│  USB Mic     │────▶│  Unified Keyword + STT Pipeline │
│  (parec)     │      │  (single audio stream)          │
│  persistent  │      │  1. On-device keyword detection │
└──────────────┘      │  2. Cloud STT (same stream)     │
                      └───────────────┬─────────────────┘
                                      │ transcription
                                      ▼
                            ┌─────────────────────┐
                            │  Fast-Path Matcher  │──▶ instant execution
                            │  (local, no LLM)    │    (stop, pause, etc.)
                            └─────────┬───────────┘
                                      │ no match
                                      ▼
                            ┌──────────────────┐
                            │  Azure OpenAI    │
                            │  Chat (GPT-5)    │
                            └────────┬─────────┘
                                     │ JSON intent + response
                                     ▼
                           ┌───────────────────┐
                           │ VoiceCommandRouter│
                           │  Intent Dispatch  │
                           └─────────┬─────────┘
              ┌────────┬──────┬──────┼──────┬────────┬────────┬────────┐
              ▼        ▼      ▼      ▼      ▼        ▼        ▼        ▼
          ┌───────┐┌──────┐┌─────┐┌─────┐┌──────┐┌───────┐┌──────┐┌───────┐
          │ Image ││Media ││Q&A  ││Brig-││Music ││Music  ││Camera││Exten- │
          │ Gen   ││Ctrl  ││     ││htns ││Search││Radio  ││Show/ ││ sion  │
          └───────┘└──────┘└─────┘└─────┘└──────┘└───────┘│ Hide │└───────┘
                                                          └──────┘
                                     │
                                     ▼
                            ┌──────────────────┐
                            │   Azure TTS      │────▶ Speakers
                            │  (paplay output) │
                            │  + Audio Ducking │
                            └──────────────────┘
```

---

## 🔊 Audio & Bluetooth Setup (Raspberry Pi)

verpixeld supports audio playback via ALSA or PulseAudio, with optional Bluetooth speaker support.

> **Quick Start**: If you just want basic ALSA audio (no Bluetooth), verpixeld works out of the box — no extra setup needed.

For Bluetooth speaker support, you need to:

1. Install PulseAudio with Bluetooth modules
2. Configure PulseAudio in system-wide mode
3. Set up D-Bus permissions for the `pulse` user
4. Pair your Bluetooth speaker
5. Compile FFmpeg with PulseAudio support

Detailed setup guides are in the `docs/` folder:

| Guide | Description |
|-------|-------------|
| **[Audio & Bluetooth Setup](docs/BLUETOOTH_AUDIO_SETUP.md)** | Complete step-by-step guide for PulseAudio, Bluetooth pairing, system configuration, and troubleshooting |
| **[FFmpeg Compilation](docs/FFMPEG_SMB.md)** | Compiling FFmpeg with PulseAudio output and SMB network share support |
| **[Raspberry Pi Setup](docs/RASPBERRY_PI_SETUP.md)** | General Pi setup: systemd service, HTTPS certificate, web-based reboot |

### Quick Bluetooth Test

Once set up, verify your Bluetooth audio from the command line:

```bash
# Check Bluetooth is enabled
bluetoothctl show | grep "Powered:"

# Check PulseAudio sees Bluetooth sink
pactl list short sinks | grep bluez

# Test audio output
paplay /usr/share/sounds/alsa/Front_Left.wav
```

---

## 📚 Libraries & Dependencies

verpixeld is built on the shoulders of giants. The following libraries make this project possible:

### Core Framework

| Library | Purpose | License |
|---------|---------|---------|
| [.NET 10.0](https://dotnet.microsoft.com/) | Runtime and base framework | MIT |
| [ASP.NET Core](https://github.com/dotnet/aspnetcore) | Web server and API framework | MIT |

### Graphics & Rendering

| Library | Purpose | License |
|---------|---------|---------|
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 2D graphics rendering, canvas operations | MIT |
| [rpi-rgb-led-matrix](https://github.com/hzeller/rpi-rgb-led-matrix) | HUB75 LED matrix hardware driver (`gpio` mode) | **GPL-2.0-or-later** |
| [PixPlane](https://github.com/Jan1503/pixplane) | Network panel stream, discovery, seam types | MIT |

### Fonts

| Resource | Purpose | License |
|----------|---------|---------|
| BDF Fonts | Bitmap fonts for LED display text rendering | Various (Public Domain / MIT) |

### Media & Streaming

| Tool / Library | Purpose | License |
|----------------|---------|---------|
| [FFmpeg](https://ffmpeg.org/) | Video/audio decoding, scaling, streaming, and audio output | LGPL/GPL |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | YouTube URL extraction and format selection | Unlicense |
| [YouTubeMusicAPI](https://github.com/IcySnex/YouTubeMusicAPI) (NuGet) | YouTube Music search (songs, videos, albums) — no API key required | GPL-3.0 |

### AI & Voice

| Library | Purpose | License |
|---------|---------|---------|
| [Microsoft.CognitiveServices.Speech](https://www.nuget.org/packages/Microsoft.CognitiveServices.Speech) (NuGet) | Azure Speech SDK — wake word detection, speech-to-text, text-to-speech | MIT |

### Web UI

| Library | Purpose | License |
|---------|---------|---------|
| [Google Fonts (Orbitron, Rajdhani, JetBrains Mono)](https://fonts.google.com/) | UI typography | OFL |

---

## 🙏 Acknowledgments

Special thanks to:

- **[Henner Zeller](https://github.com/hzeller)** for the incredible [rpi-rgb-led-matrix](https://github.com/hzeller/rpi-rgb-led-matrix) library that makes LED matrix control possible on the Raspberry Pi
- **[The Mono Project](https://github.com/mono)** for [SkiaSharp](https://github.com/mono/SkiaSharp), providing powerful cross-platform 2D graphics
- **[The .NET Team](https://github.com/dotnet)** for the excellent .NET 10 runtime and ASP.NET Core framework
- **[IcySnex](https://github.com/IcySnex)** for [YouTubeMusicAPI](https://github.com/IcySnex/YouTubeMusicAPI), enabling YouTube Music search without an API key
- **The open-source community** for countless tools, libraries, and inspiration
- **All contributors** who help improve this project

---

## 🤖 AI Assistance Disclosure

Portions of this application's code were generated with the assistance of AI tools. The AI was used as a coding assistant to help with:

- Code generation and refactoring
- Documentation writing
- UI/UX improvements
- Bug fixing and optimization

All AI-generated code has been reviewed and integrated by the project maintainer. The use of AI tools is intended to accelerate development while maintaining code quality and functionality.

---

## ⚠️ Copyright / third-party brands

This is a personal LED-display project. It is **not affiliated with** id Software, ZeniMax, Microsoft, Nintendo, Namco, The Tetris Company, Disney, Google, YouTube, Home Assistant, VLC, or any other trademark holder named below.

**Fan-style extensions** (Pac-Man, Tetris Clock, Space Invaders, Dino Runner, Flappy Bird, Snake, Pong, Matrix rain) draw original SkiaSharp shapes. They do not ship ripped sprite sheets. They are still *inspired by* commercial games — keep that in mind if you redistribute.

**Quake 3 Screensaver** currently embeds:

- vector traces of the **official Quake III Arena logo** (`Logo/q3logo.svg`, `q3symbol.svg`, `q3text.svg`)
- a BDF named **Q3Arena-Tech** (`Fonts/q3arena.bdf`)

Those are **game assets / trademarks**, not covered by the id Tech 3 GPL (engine source only). They must **not** go into a public GitHub tree. The C# screensaver code can stay; replace or omit the logo/font before publishing CanvasManagement.

**Do not commit** runtime secrets: `server.pfx`, `Config/` (API keys, SMB `.share_key`), `appsettings.json` with real passwords/IPs. Use a sanitized template.

If you believe something here infringes your rights, contact the maintainer and it will be reviewed and removed if necessary.

---

## 📄 License

**GNU General Public License v3.0 or later**

Copyright (c) 2022-2026 Jan R. Wrage

This program is free software: you can redistribute it and/or modify it under
the terms of the GNU General Public License as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later
version.

This program is distributed in the hope that it will be useful, but WITHOUT
ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

You should have received a copy of the GNU General Public License along with
this program. If not, see <https://www.gnu.org/licenses/>.

### Why GPL-3.0?

This application uses several libraries licensed under the GNU GPL:

- **[rpi-rgb-led-matrix](https://github.com/hzeller/rpi-rgb-led-matrix)** — GPL-2.0-or-later (`gpio` output)
- **[YouTubeMusicAPI](https://github.com/IcySnex/YouTubeMusicAPI)** — GPL-3.0

PixPlane is MIT and does not change the combined-work license. Because YouTubeMusicAPI requires GPL-3.0 and rpi-rgb-led-matrix permits "GPL-2.0 *or later*",
the combined work must be distributed under **GPL-3.0-or-later** to satisfy both licenses.

**What this means for you:**

- You can freely use, study, and modify this software
- You can distribute copies of this software
- You can distribute modified versions
- If you distribute this software (modified or not), you must:
  - Make the source code available
  - License your modifications under GPL-3.0 or later
  - Include this license notice

For the full license text, see the [LICENSE](LICENSE) file or visit
<https://www.gnu.org/licenses/gpl-3.0.html>

---

<div align="center">

**Made with ❤️ and lots of ☕**

*© 2022-2026 Jan R. Wrage*

</div>
