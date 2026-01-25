<div align="center">

# 🎨 verpixeld

### LED Matrix Control System

*Transform your RGB LED matrix into a dynamic, controllable display*

[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple?style=for-the-badge)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Raspberry%20Pi-red?style=for-the-badge)](https://www.raspberrypi.org/)
[![License](https://img.shields.io/badge/License-GPL--2.0--or--later-blue?style=for-the-badge)](#-license)

</div>

---

## COMING SOONER OR LATER ;)

## 🌟 What is verpixeld?

**verpixeld** is a comprehensive .NET 8 application designed to control large RGB LED matrix displays through an elegant, modern web interface. Originally developed for Raspberry Pi with HUB75 LED panels, verpixeld transforms your LED matrix into a powerful, programmable display system.

Whether you want to show the time, display weather information, run animations, stream your camera, or create collaborative pixel art – verpixeld provides a unified platform with:

- 🖥️ **Beautiful Web Control Panel** - Control everything from any device with a browser
- 🧩 **Plugin Architecture** - Extend functionality with custom content extensions
- 🎨 **Real-time Visual Filters** - Apply effects like blur, color correction, and more
- 📐 **Flexible Layouts** - Multi-canvas support with independent layers and opacity
- ⏰ **Smart Scheduling** - Automate content changes based on time
- 🌙 **Night Mode** - Automatic brightness adjustment for different times of day
- 📷 **Camera Streaming** - Stream live video from your phone to the display
- ✏️ **Drawing Mode** - Create pixel art directly on the display

---

## ✨ Features

### 🖼️ Display Management

| Feature | Description |
|---------|-------------|
| **Multi-Canvas System** | Layer multiple content sources with independent z-ordering, opacity, and brightness |
| **Layout Profiles** | Pre-defined layouts: FullScreen, HeaderContent, ThreePanel, SplitView, Dashboard |
| **Custom Overlays** | Create positioned overlay canvases for notifications, clocks, etc. |
| **Hot Reload** | Change content and settings without restarting |

### 🧩 Extensions (Content Plugins)

Extensions are dynamically loaded plugins that provide content for canvases:

- Clock displays (analog, digital, world time)
- Weather information
- RSS/News feeds
- Image slideshows
- Animations and visualizations
- Custom content via plugin API

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

---

## 🏗️ Architecture

```text
┌─────────────────────────────────────────────────────────────────┐
│                       Web Control Panel                         │
│                     (HTML/CSS/JavaScript)                       │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                        ASP.NET Core API                         │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌────────────┐ │
│  │   Layout    │ │  Extension  │ │   Filter    │ │  Schedule  │ │
│  │  Endpoints  │ │  Endpoints  │ │  Endpoints  │ │  Endpoints │ │
│  └─────────────┘ └─────────────┘ └─────────────┘ └────────────┘ │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                         Core Services                           │
│  ┌─────────────────┐ ┌──────────────────┐ ┌──────────────────┐  │
│  │  LayoutManager  │ │  ContentManager  │ │ ScheduleManager  │  │
│  └─────────────────┘ └──────────────────┘ └──────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                        Canvas Management                        │
│                (Multi-layer composition & filters)              │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                       RGB Matrix Renderer                       │
│                  (Hardware abstraction layer)                   │
│                        ┌─────────────┐                          │
│                        │  HUB75 LED  │                          │
│                        │   Matrix    │                          │
│                        └─────────────┘                          │
└─────────────────────────────────────────────────────────────────┘
```

---

## 🚀 Getting Started

### Prerequisites

- Raspberry Pi 4 (recommended) or Pi 3
- HUB75 RGB LED Matrix panels
- .NET 8.0 Runtime
- Raspberry Pi OS (64-bit recommended)

### Installation

1. **Clone the repository**
   ```bash
   git clone https://github.com/Jan1503/verpixeld.git
   cd verpixeld
   ```

2. **Build the application**
   ```bash
   dotnet publish -c Release -r linux-arm64
   ```

3. **Deploy to Raspberry Pi**
   ```bash
   scp -r bin/Release/net8.0/linux-arm64/publish/* pi@raspberrypi:/home/pi/verpixeld/
   ```

4. **Configure systemd service** (see [Raspberry Pi Setup Guide](docs/RASPBERRY_PI_SETUP.md))

5. **Access the web interface**
   - HTTP: `<http://<pi-ip>>:5000`
   - HTTPS: `<https://<pi-ip>>:5001`

---

## 📚 Libraries & Dependencies

verpixeld is built on the shoulders of giants. The following libraries make this project possible:

### Core Framework

| Library | Purpose | License |
|---------|---------|---------|
| [.NET 8.0](https://dotnet.microsoft.com/) | Runtime and base framework | MIT |
| [ASP.NET Core](https://github.com/dotnet/aspnetcore) | Web server and API framework | MIT |

### Graphics & Rendering

| Library | Purpose | License |
|---------|---------|---------|
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 2D graphics rendering, canvas operations | MIT |
| [rpi-rgb-led-matrix](https://github.com/hzeller/rpi-rgb-led-matrix) | HUB75 LED matrix hardware driver | **GPL-2.0-or-later** ¹ |

> ¹ The rpi-rgb-led-matrix library's GPL-2.0-or-later license requires that any software
> linking with it must also be distributed under GPL-compatible terms. This is why
> verpixeld is licensed under GPL-2.0-or-later.

### Fonts

| Resource | Purpose | License |
|----------|---------|---------|
| BDF Fonts | Bitmap fonts for LED display text rendering | Various (Public Domain / MIT) |

### Web UI

| Library | Purpose | License |
|---------|---------|---------|
| [Google Fonts (Orbitron, Rajdhani)](https://fonts.google.com/) | UI typography | OFL |

---

## 🙏 Acknowledgments

Special thanks to:

- **[Henner Zeller](https://github.com/hzeller)** for the incredible [rpi-rgb-led-matrix](https://github.com/hzeller/rpi-rgb-led-matrix) library that makes LED matrix control possible on the Raspberry Pi
- **[The Mono Project](https://github.com/mono)** for [SkiaSharp](https://github.com/mono/SkiaSharp), providing powerful cross-platform 2D graphics
- **[The .NET Team](https://github.com/dotnet)** for the excellent .NET 8 runtime and ASP.NET Core framework
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

## ⚠️ Copyright Disclaimer

This project is developed for educational and personal use purposes.

**Any resemblance to or inclusion of copyrighted material is entirely unintentional.** If you believe any content in this project infringes on your copyright or intellectual property rights, please contact the maintainer immediately, and the material will be promptly reviewed and removed if necessary.

The developers make no claims to any third-party trademarks, logos, or copyrighted materials that may be referenced or inadvertently included. All product names, logos, and brands are property of their respective owners.

---

## 📄 License

**GNU General Public License v2.0 or later**

Copyright (c) 2022-2026 Jan R. Wrage

This program is free software; you can redistribute it and/or modify it under
the terms of the GNU General Public License as published by the Free Software
Foundation; either version 2 of the License, or (at your option) any later
version.

This program is distributed in the hope that it will be useful, but WITHOUT
ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

You should have received a copy of the GNU General Public License along with
this program; if not, write to the Free Software Foundation, Inc., 51 Franklin
Street, Fifth Floor, Boston, MA 02110-1301 USA.

### Why GPL?

This application uses the [rpi-rgb-led-matrix](https://github.com/hzeller/rpi-rgb-led-matrix)
library by Henner Zeller, which is licensed under GPL-2.0-or-later. As per the GPL's
copyleft requirements, this means that verpixeld, which links with this library, must
also be distributed under GPL-compatible terms.

**What this means for you:**

- ✅ You can freely use, study, and modify this software
- ✅ You can distribute copies of this software
- ✅ You can distribute modified versions
- ⚠️ If you distribute this software (modified or not), you must:
  - Make the source code available
  - License your modifications under GPL-2.0 or later
  - Include this license notice

For the full license text, see the [LICENSE](LICENSE) file or visit
<https://www.gnu.org/licenses/old-licenses/gpl-2.0.html>

---

<div align="center">

**Made with ❤️ and lots of ☕**

*© 2022-2026 Jan R. Wrage*

</div>
