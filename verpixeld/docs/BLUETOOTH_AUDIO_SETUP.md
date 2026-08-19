# Audio & Bluetooth Setup (Raspberry Pi)

verpixeld supports audio playback via ALSA or PulseAudio, with optional Bluetooth speaker support. This comprehensive guide covers everything needed to enable audio and Bluetooth on a **fresh Raspberry Pi installation**.

> **Quick Start**: If you just want basic ALSA audio (no Bluetooth), verpixeld works out of the box. Follow this guide only if you need Bluetooth speaker support.

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Install Required Packages](#2-install-required-packages)
3. [Enable Bluetooth Hardware](#3-enable-bluetooth-hardware)
4. [Configure PulseAudio (System-Wide)](#4-configure-pulseaudio-system-wide)
5. [Start Services](#5-start-services)
6. [Pair a Bluetooth Speaker](#6-pair-a-bluetooth-speaker)
7. [Compile FFmpeg with PulseAudio & SMB Support](#7-compile-ffmpeg-with-pulseaudio--smb-support)
8. [Verify Everything Works](#8-verify-everything-works)
9. [Using Bluetooth in verpixeld](#9-using-bluetooth-in-verpixeld)
10. [Troubleshooting](#10-troubleshooting)
11. [Quick Reference](#quick-reference)

---

## Understanding PulseAudio Modes

PulseAudio can run in two modes:

### User Mode (Default)

- Runs per-user as part of the desktop session
- Automatically started by systemd user services or desktop environment
- Has access to user's D-Bus session bus
- Bluetooth "just works" in most cases

### System Mode

- Runs as a system service (as user `pulse`)
- Used for headless/kiosk systems without a desktop environment
- **Bluetooth requires additional configuration**
- verpixeld typically uses this mode on Raspberry Pi

To check which mode you're using:

```bash
# Check if system mode daemon is running
systemctl status pulseaudio

# Or check the process
ps aux | grep pulseaudio
# System mode shows: pulse (user), not your username
```

---

## 1. Prerequisites

- Raspberry Pi 4 (recommended) or Pi 3/3B+
- Raspberry Pi OS (Bookworm or later recommended)
- A Bluetooth speaker in pairing mode
- Internet connection for package installation

---

## 2. Install Required Packages

```bash
# Update system first
sudo apt update && sudo apt upgrade -y

# Install PulseAudio with Bluetooth support
sudo apt install -y \
  pulseaudio \
  pulseaudio-module-bluetooth \
  bluez \
  bluez-tools \
  rfkill \
  libpulse-dev

# Install additional Bluetooth utilities (optional but recommended)
sudo apt install -y bluez-firmware pi-bluetooth
```

**What each package does:**

- `pulseaudio` — Sound server that manages audio devices
- `pulseaudio-module-bluetooth` — Bluetooth audio profiles (A2DP, HSP/HFP)
- `bluez` — Linux Bluetooth protocol stack
- `bluez-tools` — Additional Bluetooth utilities
- `rfkill` — Tool to enable/disable wireless devices
- `libpulse-dev` — Development headers (needed if compiling FFmpeg)

### Kernel Modules

Ensure Bluetooth kernel modules are loaded:

```bash
sudo modprobe btusb
sudo modprobe bluetooth
```

For Raspberry Pi's built-in Bluetooth:

```bash
sudo modprobe hci_uart
```

---

## 3. Enable Bluetooth Hardware

On Raspberry Pi, Bluetooth may be blocked by default. Let's enable it:

```bash
# Check if Bluetooth is blocked
rfkill list

# Look for output like:
# 0: hci0: Bluetooth
#    Soft blocked: yes   <-- This needs to be "no"
#    Hard blocked: no

# Unblock Bluetooth if soft blocked
sudo rfkill unblock bluetooth

# Verify it's unblocked
rfkill list
# Should now show: Soft blocked: no

# Enable and start the Bluetooth service
sudo systemctl enable bluetooth
sudo systemctl start bluetooth

# Verify Bluetooth is working
bluetoothctl show
# Should show "Powered: yes" and list the adapter
```

**Make unblock permanent** (survives reboot):

```bash
# Create a systemd service to unblock on boot
sudo tee /etc/systemd/system/rfkill-unblock-bluetooth.service > /dev/null << 'EOF'
[Unit]
Description=Unblock Bluetooth at boot
After=bluetooth.service

[Service]
Type=oneshot
ExecStart=/usr/sbin/rfkill unblock bluetooth
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable rfkill-unblock-bluetooth
```

---

## 4. Configure PulseAudio (System-Wide)

> **Important:** verpixeld runs as a systemd service, often as a different user than your login session. For Bluetooth audio to work reliably, PulseAudio should run **system-wide**, not in user mode.

### Option A: System-Wide PulseAudio (Recommended for verpixeld)

```bash
# Stop any existing user PulseAudio instance
systemctl --user stop pulseaudio.socket pulseaudio.service 2>/dev/null || true
pulseaudio -k 2>/dev/null || true

# Disable user PulseAudio
systemctl --user disable pulseaudio.socket pulseaudio.service 2>/dev/null || true
systemctl --user mask pulseaudio.socket 2>/dev/null || true

# Create the pulse user if it doesn't exist
sudo useradd --system --group audio pulse 2>/dev/null || true

# Add pulse user to required groups
sudo usermod -a -G bluetooth,audio pulse

# Also add the user that runs verpixeld to the audio and pulse-access groups
sudo usermod -a -G audio,pulse-access $USER
sudo usermod -a -G audio,pulse-access pi  # if running as 'pi' user
sudo usermod -a -G audio,pulse-access root  # if running as root
```

### Configure System-Wide PulseAudio

Edit the system-wide PulseAudio configuration:

```bash
sudo nano /etc/pulse/system.pa
```

Add these lines at the end of the file:

```text
### Enable Bluetooth audio
load-module module-bluetooth-policy
load-module module-bluetooth-discover
```

**Alternative:** If the file doesn't exist or is empty, create it:

```bash
sudo tee /etc/pulse/system.pa > /dev/null << 'EOF'
#!/usr/bin/pulseaudio -nF
#
# PulseAudio system-wide configuration

# Automatically restore audio device settings
load-module module-device-restore
load-module module-stream-restore
load-module module-card-restore

# Detect available hardware
load-module module-udev-detect

# Load native protocol for local connections
load-module module-native-protocol-unix auth-anonymous=1

# Enable Bluetooth audio
load-module module-bluetooth-policy
load-module module-bluetooth-discover

# Allow connections from local network (for debugging)
load-module module-native-protocol-tcp auth-ip-acl=127.0.0.1;192.168.0.0/16 auth-anonymous=1

# Automatically switch to newly connected sinks
load-module module-switch-on-connect

# ALSA sink (fallback)
load-module module-alsa-sink device=hw:0,0

# Set default sink to ALSA initially
set-default-sink alsa_output.hw_0_0
EOF
```

### Configure D-Bus Permissions

Create or edit `/etc/dbus-1/system.d/pulseaudio-bluetooth.conf`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-BUS Bus Configuration 1.0//EN"
 "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
<busconfig>
  <!-- Allow pulse user to access BlueZ -->
  <policy user="pulse">
    <allow send_destination="org.bluez"/>
    <allow send_interface="org.bluez.Manager"/>
    <allow send_interface="org.bluez.Adapter1"/>
    <allow send_interface="org.bluez.Device1"/>
    <allow send_interface="org.bluez.MediaTransport1"/>
    <allow send_interface="org.bluez.MediaEndpoint1"/>
    <allow send_interface="org.freedesktop.DBus.ObjectManager"/>
    <allow send_interface="org.freedesktop.DBus.Properties"/>
  </policy>
</busconfig>
```

### Create PulseAudio Systemd Service

```bash
sudo tee /etc/systemd/system/pulseaudio.service > /dev/null << 'EOF'
[Unit]
Description=PulseAudio System-Wide Server
After=sound.target bluetooth.service
Requires=sound.target

[Service]
Type=notify
ExecStart=/usr/bin/pulseaudio --system --disallow-exit --disallow-module-loading=0 --log-target=journal
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable pulseaudio
```

---

## 5. Start Services

```bash
# Reload D-Bus configuration
sudo systemctl reload dbus

# Restart Bluetooth service
sudo systemctl restart bluetooth

# Start PulseAudio system-wide
sudo systemctl start pulseaudio

# Verify services are running
sudo systemctl status bluetooth pulseaudio

# Verify PulseAudio is detecting devices
pactl info
pactl list short sinks

# Should show at least one sink (probably ALSA)
```

---

## 6. Pair a Bluetooth Speaker

### Initial Pairing (One-Time)

Pair your Bluetooth speaker/headphones from the terminal:

```bash
# Start bluetoothctl
bluetoothctl

# Power on the adapter (if not already)
power on

# Enable scanning
scan on

# Put your speaker in pairing mode now!
# Wait for your speaker to appear:
# [NEW] Device XX:XX:XX:XX:XX:XX SpeakerName

# Stop scanning once you see your device
scan off

# Trust the device (enables auto-reconnect)
trust XX:XX:XX:XX:XX:XX

# Pair with the device
pair XX:XX:XX:XX:XX:XX

# Connect to the device
connect XX:XX:XX:XX:XX:XX

# Verify connection
info XX:XX:XX:XX:XX:XX
# Should show: Connected: yes

# Exit bluetoothctl
quit
```

### Verify Bluetooth Audio Sink Appears

```bash
# After connecting, PulseAudio should detect the Bluetooth speaker
# Wait a few seconds, then:
pactl list short sinks

# You should see something like:
# 1  bluez_sink.XX_XX_XX_XX_XX_XX.a2dp_sink  module-bluez5-device.c  s16le 2ch 44100Hz  RUNNING

# If you don't see it, reload the Bluetooth module:
pactl unload-module module-bluetooth-discover
pactl load-module module-bluetooth-discover
```

---

## 7. Compile FFmpeg with PulseAudio & SMB Support

The default FFmpeg on Raspberry Pi OS **lacks PulseAudio and SMB support**. To enable Bluetooth audio output and network video streaming, you must compile FFmpeg from source.

See the dedicated guide: **[Compiling FFmpeg with PulseAudio & SMB Support](FFMPEG_SMB.md)**

---

## 8. Verify Everything Works

Run this checklist to confirm your setup:

```bash
echo "=== Bluetooth Check ==="
rfkill list bluetooth
bluetoothctl show | grep "Powered:"

echo ""
echo "=== PulseAudio Check ==="
pactl info | grep "Server Name:"
pactl list short sinks

echo ""
echo "=== FFmpeg Check ==="
ffmpeg -formats 2>&1 | grep pulse && echo "PulseAudio supported" || echo "PulseAudio NOT supported"
ffmpeg -protocols 2>&1 | grep smb && echo "SMB supported" || echo "SMB NOT supported"

echo ""
echo "=== Audio Test ==="
# Test ALSA directly
speaker-test -t wav -c 2 -l 1 2>/dev/null && echo "ALSA audio works" || echo "ALSA failed"

# Test PulseAudio
paplay /usr/share/sounds/alsa/Front_Left.wav 2>/dev/null && echo "PulseAudio works" || echo "PulseAudio failed"
```

---

## 9. Using Bluetooth in verpixeld

Once everything is configured:

1. **Open the verpixeld web interface**
2. **Go to the "Media" tab**
3. **Expand "Audio Output & Bluetooth" section**
4. You should see:
   - **PulseAudio status:** Connected
   - **Bluetooth status:** On (with Power Off button)
5. Click **"Scan for Devices"** to find Bluetooth speakers
6. Click **"Pair"** next to your speaker
7. Once connected, select the Bluetooth sink from **"Audio Output"** dropdown
8. Play a video or audio file — it should play through the Bluetooth speaker!

### Connection Flow (Internal)

When you connect to a Bluetooth device via the verpixeld UI:

1. **D-Bus Connect**
   - Calls BlueZ `Device1.Connect()` method via system D-Bus
   - Waits up to 8 seconds for PulseAudio to create the A2DP sink

2. **A2DP Profile Activation** (if no sink appears)
   - Queries device UUIDs to verify connection
   - Explicitly calls `Device1.ConnectProfile()` with A2DP sink UUID
   - UUID: `0000110b-0000-1000-8000-00805f9b34fb`

3. **Direct Module Load** (if still no sink)
   - Attempts: `pactl load-module module-bluez5-device path=/org/bluez/hci0/dev_XX_XX_XX_XX_XX_XX`

4. **Module Reload** (last resort)
   - Unloads and reloads `module-bluez5-discover`
   - This forces PulseAudio to rescan all Bluetooth devices

5. **Interactive Fallback**
   - Uses interactive `bluetoothctl` session
   - Waits for full A2DP handshake (up to 15 seconds)
   - Monitors output for transport establishment

### API Endpoints

```text
GET  /api/audio/sinks              - List audio outputs (includes Bluetooth)
GET  /api/audio/bluetooth/devices  - List paired Bluetooth devices
POST /api/audio/bluetooth/connect  - Connect to device
     Body: { "address": "XX:XX:XX:XX:XX:XX" }
POST /api/audio/bluetooth/disconnect - Disconnect device
GET  /api/audio/bluetooth/status   - Check adapter status
```

### Logs to Watch

When debugging Bluetooth issues, look for these log prefixes:

```text
[BLUETOOTH] - Connection status and errors
[AUDIO]     - PulseAudio sink detection
[PULSE-SSE] - PulseAudio event monitoring
```

---

## 10. Troubleshooting

### Bluetooth won't power on

```bash
# Check rfkill status
rfkill list

# Output shows "Soft blocked: yes"?
sudo rfkill unblock bluetooth

# Still not working? Check if Bluetooth is hard-blocked in firmware
# Edit /boot/config.txt and ensure this line is NOT present:
# dtoverlay=disable-bt

# Reboot after changes
sudo reboot
```

### "br-connection-profile-unavailable" when connecting

This error means PulseAudio's Bluetooth module isn't loaded:

```bash
# Load the module manually
pactl load-module module-bluetooth-discover

# If it fails with "Module initialization failed", check if PulseAudio is running
pactl info

# If PulseAudio isn't running, start it
sudo systemctl start pulseaudio
```

### Bluetooth device connects but no audio sink appears

**First, verify the `pulse` user is in the `bluetooth` group (CRITICAL):**

```bash
# Check if pulse user is in bluetooth group
groups pulse
# Should include: bluetooth

# If not, add it:
sudo usermod -a -G bluetooth pulse

# IMPORTANT: Restart both services after this change
sudo systemctl restart bluetooth
sudo systemctl restart pulseaudio
```

**Check D-Bus permissions:**

```bash
# Test if pulse can access BlueZ
sudo -u pulse busctl --system call org.bluez /org/bluez org.freedesktop.DBus.ObjectManager GetManagedObjects
# Should return device data, not "permission denied"
```

**Check Bluetooth audio capabilities:**

```bash
# Check if Bluetooth audio UUIDs are available
bluetoothctl info XX:XX:XX:XX:XX:XX | grep UUID
# Should show: UUID: Audio Sink (0000110b-0000-1000-8000-00805f9b34fb)

# Reload the Bluetooth module
pactl unload-module module-bluetooth-discover
pactl load-module module-bluetooth-discover

# If still not appearing, check PulseAudio logs
journalctl -u pulseaudio -f

# Common fix: restart both services
sudo systemctl restart bluetooth
sudo systemctl restart pulseaudio
```

### `le-connection-abort-by-local` error when connecting

This error often means PulseAudio can't establish the A2DP audio profile. Common causes:

**Cause 1: The `pulse` user is not in the `bluetooth` group**

```bash
sudo usermod -a -G bluetooth pulse
sudo systemctl restart bluetooth pulseaudio
```

**Cause 2: Speaker is advertising wrong address (BLE vs BR/EDR)**

Some Bluetooth speakers have two addresses:

- A **BLE (Low Energy)** address for discovery (random, starts with high nibble like `65:38:...`)
- A **BR/EDR (Classic)** address for audio streaming (fixed, like `98:52:3D:...`)

If you're connecting to the BLE address, audio won't work:

```bash
# Put speaker in PAIRING MODE (hold pairing button until fast flashing)
# This exposes the correct BR/EDR address

# Scan for devices
bluetoothctl scan on

# Look for your speaker with a "normal" MAC address (not random BLE)
# The BR/EDR address usually has a recognizable OUI prefix

# Remove the old BLE device entry
bluetoothctl remove 65:38:1A:32:14:60  # Example BLE address

# Pair with the correct BR/EDR address
bluetoothctl pair 98:52:3D:4C:AA:C2    # Example BR/EDR address
bluetoothctl trust 98:52:3D:4C:AA:C2
bluetoothctl connect 98:52:3D:4C:AA:C2
```

### "Requested output format 'pulse' is not known"

Your FFmpeg doesn't have PulseAudio support compiled in:

```bash
# Check FFmpeg formats
ffmpeg -formats 2>&1 | grep pulse

# If empty, you need to recompile FFmpeg with --enable-libpulse
# Follow the FFmpeg compilation guide: docs/FFMPEG_SMB.md
```

### Two PulseAudio instances running (user + system)

This causes conflicts and prevents Bluetooth from working:

```bash
# Check for multiple instances
ps aux | grep pulseaudio

# Kill user instance
pulseaudio -k

# Disable user PulseAudio permanently (BOTH socket AND service)
systemctl --user stop pulseaudio.socket pulseaudio.service
systemctl --user disable pulseaudio.socket pulseaudio.service
systemctl --user mask pulseaudio.socket pulseaudio.service

# Ensure only system-wide PulseAudio runs
sudo systemctl restart pulseaudio

# Verify only one instance
ps aux | grep pulseaudio
# Should show only: pulse ... pulseaudio --system ...
```

### `pactl` commands fail with "Connection refused" when running as root

When verpixeld runs as root but PulseAudio runs in system mode, `pactl` needs to know where to connect:

```bash
# For system-mode PulseAudio, set the socket path:
export PULSE_SERVER=unix:/var/run/pulse/native

# Now pactl will work
pactl info
pactl list short sinks

# To make this permanent for the root user:
echo 'export PULSE_SERVER=unix:/var/run/pulse/native' >> /root/.bashrc
```

**Note:** verpixeld automatically sets this environment variable when it detects system-mode PulseAudio.

### Audio plays through wrong device

```bash
# List all sinks
pactl list short sinks

# Set default sink to your Bluetooth speaker
pactl set-default-sink bluez_sink.XX_XX_XX_XX_XX_XX.a2dp_sink

# Or by name pattern (easier)
pactl set-default-sink $(pactl list short sinks | grep bluez | cut -f2)
```

### Speaker disconnects frequently

```bash
# Increase Bluetooth connection reliability
sudo nano /etc/bluetooth/main.conf

# Add/modify these settings:
[General]
FastConnectable = true
ReconnectAttempts=7
ReconnectIntervals=1,2,4,8,16,32,64
AutoEnable=true

# Restart Bluetooth
sudo systemctl restart bluetooth
```

### No sound but everything seems connected

```bash
# Check if sink is muted
pactl list sinks | grep -A 10 "bluez"

# Unmute if necessary
pactl set-sink-mute @DEFAULT_SINK@ 0

# Set volume
pactl set-sink-volume @DEFAULT_SINK@ 100%

# Test direct audio
paplay /usr/share/sounds/alsa/Front_Left.wav
```

### A2DP sink exists but no sound

1. **Set as default sink:**
   ```bash
   pactl set-default-sink bluez_sink.XX_XX_XX_XX_XX_XX.a2dp_sink
   ```

2. **Check volume:**
   ```bash
   pactl set-sink-volume bluez_sink.XX_XX_XX_XX_XX_XX.a2dp_sink 100%
   pactl set-sink-mute bluez_sink.XX_XX_XX_XX_XX_XX.a2dp_sink 0
   ```

3. **Verify profile is A2DP (not HSP/HFP):**
   ```bash
   pactl list cards
   # Look for "Active Profile: a2dp_sink"
   
   # If not, set it:
   pactl set-card-profile bluez_card.XX_XX_XX_XX_XX_XX a2dp_sink
   ```

### "Connection successful" but no audio sink

**Symptoms:**

- `bluetoothctl connect` succeeds
- `pactl list sinks` shows no Bluetooth sink
- App shows "Connected but no A2DP sink"

**Solutions:**

1. **Reload Bluetooth modules:**
   ```bash
   pactl unload-module module-bluetooth-discover
   pactl unload-module module-bluetooth-policy
   pactl load-module module-bluetooth-policy
   pactl load-module module-bluetooth-discover
   ```

2. **Check BlueZ logs:**
   ```bash
   journalctl -u bluetooth -f
   # Look for A2DP profile errors
   ```

3. **Check PulseAudio logs:**
   ```bash
   journalctl -u pulseaudio -f
   # Look for bluez-related errors
   ```

### Device connects from terminal but not from app

**Symptoms:**

- `bluetoothctl connect` from SSH/terminal works
- Connecting via verpixeld UI fails

**Cause:** The app runs as a different user (root or daemon) without proper D-Bus session access.

**Solution:** verpixeld now handles this by:

1. Using interactive `bluetoothctl` with expect-style interaction
2. Explicitly requesting the A2DP profile via D-Bus
3. Directly loading the PulseAudio Bluetooth module

If issues persist, try connecting once from terminal, then the device should auto-reconnect.

### "Failed to load cookie file" errors

**Symptoms:**

```text
Failed to load cookie file from cookie: Permission denied
```

**Cause:** PulseAudio trying to use cookie authentication in system mode.

**Solution:** This is usually harmless if `auth-anonymous=1` is set. Verify in `/etc/pulse/system.pa`:

```text
load-module module-native-protocol-unix auth-anonymous=1
```

### Bluetooth device disconnects randomly

**Solutions:**

1. **Disable power management:**
   ```bash
   # For built-in Bluetooth
   sudo hciconfig hci0 noauth
   
   # Or in /etc/bluetooth/main.conf:
   [Policy]
   AutoEnable=true
   ```

2. **Keep device trusted:**
   ```bash
   bluetoothctl trust XX:XX:XX:XX:XX:XX
   ```

3. **Check for interference:**
   - WiFi and Bluetooth share the 2.4GHz band on Raspberry Pi
   - Try using 5GHz WiFi or a USB Bluetooth adapter

### Bluetooth audio crackling / dropouts / stuttering

Bluetooth audio quality issues are often caused by buffer underruns, codec issues, or WiFi interference.

**Fix 1: Increase PulseAudio buffer sizes**

```bash
# Edit PulseAudio daemon configuration
sudo nano /etc/pulse/daemon.conf

# Add/modify these lines to increase buffers:
default-fragments = 8
default-fragment-size-msec = 10

# Restart PulseAudio
sudo systemctl restart pulseaudio
```

**Fix 2: Disable WiFi power saving (reduces interference)**

On Raspberry Pi, WiFi and Bluetooth share the same chip. WiFi power saving can cause Bluetooth dropouts:

```bash
# Disable WiFi power management
sudo nano /etc/rc.local

# Add before "exit 0":
/sbin/iwconfig wlan0 power off

# Alternatively, create a udev rule:
echo 'ACTION=="add", SUBSYSTEM=="net", KERNEL=="wlan0", RUN+="/sbin/iwconfig wlan0 power off"' | \
  sudo tee /etc/udev/rules.d/70-wifi-powersave.rules
```

**Fix 3: Use a better Bluetooth codec (if supported)**

Check your current codec and try to switch to a higher-quality one:

```bash
# Check current codec
pactl list cards | grep -A 20 "bluez" | grep "a2dp"

# For earbuds/speakers that support AAC (better than SBC):
pactl set-card-profile <card_name> a2dp_sink_aac

# Or try SBC-XQ for devices that support it:
pactl set-card-profile <card_name> a2dp_sink_sbc_xq
```

**Fix 4: Reduce audio sample rate (less bandwidth needed)**

```bash
# Edit PulseAudio daemon configuration
sudo nano /etc/pulse/daemon.conf

# Change sample rate:
default-sample-rate = 44100
alternate-sample-rate = 48000

# Restart PulseAudio
sudo systemctl restart pulseaudio
```

**Fix 5: Keep Bluetooth device closer to Pi**

- Bluetooth range is limited, especially for audio streaming
- Keep devices within 3-5 meters of the Pi
- Remove obstacles between the devices
- Move other 2.4GHz devices away (WiFi routers, microwaves)

---

## Quick Reference

### Essential Commands

```bash
# Bluetooth
rfkill unblock bluetooth          # Enable Bluetooth hardware
bluetoothctl power on             # Power on adapter
bluetoothctl scan on              # Start scanning
bluetoothctl pair XX:XX:XX:XX     # Pair device
bluetoothctl connect XX:XX:XX:XX  # Connect device
bluetoothctl devices              # List known devices
bluetoothctl remove XX:XX:XX:XX   # Forget device

# PulseAudio
pactl info                        # Show PulseAudio status
pactl list short sinks            # List audio outputs
pactl list short sources          # List audio inputs
pactl set-default-sink <name>     # Set default output
pactl set-sink-volume <name> 80%  # Set volume
pactl load-module module-bluetooth-discover  # Enable Bluetooth audio

# Services
sudo systemctl status bluetooth   # Check Bluetooth service
sudo systemctl status pulseaudio  # Check PulseAudio service
sudo systemctl restart bluetooth  # Restart Bluetooth
sudo systemctl restart pulseaudio # Restart PulseAudio

# FFmpeg verification
ffmpeg -formats 2>&1 | grep pulse # Check PulseAudio support
ffmpeg -protocols 2>&1 | grep smb # Check SMB support
```

### Required Group Memberships

| User | Groups |
|------|--------|
| pulse | pulse, audio, bluetooth |
| pi (or app user) | audio, bluetooth |

### Important Files

| File | Purpose |
|------|---------|
| `/etc/pulse/system.pa` | System mode PulseAudio config |
| `/etc/pulse/daemon.conf` | PulseAudio daemon settings (buffer sizes, sample rate) |
| `/etc/bluetooth/main.conf` | BlueZ configuration |
| `/etc/dbus-1/system.d/pulseaudio-bluetooth.conf` | D-Bus permissions |
| `/var/run/pulse/native` | PulseAudio socket (system mode) |

---

## Still Having Issues?

If Bluetooth audio still doesn't work after following this guide:

1. **Test from terminal first**: If `bluetoothctl connect` + `pactl list sinks` doesn't show the Bluetooth sink, the issue is system configuration, not verpixeld.

2. **Check kernel/driver issues**:
   ```bash
   dmesg | grep -i bluetooth
   dmesg | grep -i hci
   ```

3. **Try a USB Bluetooth adapter**: The Raspberry Pi's built-in Bluetooth can be problematic. A USB adapter often works better.

4. **Consider user-mode PulseAudio**: If you have a desktop environment, user-mode PulseAudio is much easier to configure for Bluetooth.

---

*Last updated: February 2026*
