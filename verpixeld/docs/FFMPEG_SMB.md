# Compiling FFmpeg with PulseAudio & SMB Support on Raspberry Pi

This guide shows how to compile FFmpeg with `libpulse` (PulseAudio) and `libsmbclient` (SMB/CIFS) support. Both are missing from the default Raspberry Pi OS FFmpeg package.

## Why Compile FFmpeg?

The default FFmpeg package on Raspberry Pi OS lacks PulseAudio output and SMB protocol support. Without these, verpixeld cannot:

- **Route audio through PulseAudio** — Required for Bluetooth speaker output (see [Bluetooth Audio Setup](BLUETOOTH_AUDIO_SETUP.md))
- **Stream from SMB/CIFS network shares** — Direct playback from NAS devices with full seeking support

## Prerequisites

Install the required build dependencies:

```bash
# Update package lists
sudo apt update

# Install build tools
sudo apt install -y build-essential git pkg-config

# Install FFmpeg dependencies
sudo apt install -y \
    libsmbclient-dev \
    libpulse-dev \
    libavcodec-dev \
    libavformat-dev \
    libavutil-dev \
    libswscale-dev \
    libswresample-dev \
    libavfilter-dev \
    yasm \
    nasm \
    libx264-dev \
    libx265-dev \
    libvpx-dev \
    libfdk-aac-dev \
    libmp3lame-dev \
    libopus-dev \
    libass-dev \
    libfreetype6-dev \
    libvorbis-dev \
    libtheora-dev \
    libssl-dev \
    libv4l-dev \
    libasound2-dev
```

> **Note:** If `libfdk-aac-dev` is not available, you can remove `--enable-libfdk-aac` from the configure step.

## Download FFmpeg Source

```bash
# Create a build directory
mkdir -p ~/ffmpeg_build
cd ~/ffmpeg_build

# Clone FFmpeg source (use a stable release tag)
git clone --depth 1 --branch n6.1 https://github.com/FFmpeg/FFmpeg.git ffmpeg
cd ffmpeg
```

## Configure and Compile

### Full Configuration (PulseAudio + SMB + Hardware Acceleration)

```bash
./configure \
    --prefix=/usr/local \
    --enable-gpl \
    --enable-nonfree \
    --enable-version3 \
    --enable-libsmbclient \
    --enable-libpulse \
    --enable-libx264 \
    --enable-libx265 \
    --enable-libvpx \
    --enable-libfdk-aac \
    --enable-libmp3lame \
    --enable-libopus \
    --enable-libass \
    --enable-libfreetype \
    --enable-libvorbis \
    --enable-libtheora \
    --enable-openssl \
    --enable-v4l2-m2m
```

**Expected output:** Look for these lines near the end:

```text
Enabled indevs: ...
Enabled outdevs: ... pulse ...
...
libpulse enabled              yes
libsmbclient enabled          yes
v4l2_m2m enabled              yes
```

If you see `ERROR: libpulse not found using pkg-config`, run:

```bash
sudo apt install libpulse-dev
```

> **Time estimate:** 45-90 minutes on Raspberry Pi 4

### Build

```bash
# Compile using all CPU cores
make -j$(nproc)

# Install
sudo make install

# Update library cache
sudo ldconfig
```

## Verify Installation

Check that PulseAudio output is enabled (critical for Bluetooth audio):

```bash
ffmpeg -formats 2>&1 | grep pulse
# Expected: " DE pulse           PulseAudio output"
```

Check that SMB protocol is enabled:

```bash
ffmpeg -protocols 2>&1 | grep smb
# Expected: "smb"
```

Check that hardware decoders are available:

```bash
ffmpeg -decoders 2>&1 | grep -E "h264_v4l2m2m|hevc_v4l2m2m"
# Expected:
#  V..... h264_v4l2m2m         V4L2 mem2mem H.264 decoder wrapper (codec h264)
#  V..... hevc_v4l2m2m         V4L2 mem2mem HEVC decoder wrapper (codec hevc)
```

## Test SMB Playback

Test with ffplay:

```bash
ffplay "smb://username:password@server/share/path/video.mkv"
```

## Troubleshooting

### "Protocol not found" Error

If FFmpeg still shows "Protocol not found" for smb://:

1. Check libsmbclient is installed:
   ```bash
   pkg-config --exists smbclient && echo "Found" || echo "Not found"
   ```

2. Recompile FFmpeg and look for "libsmbclient" in the configure output:
   ```
   External libraries:
   libsmbclient            ✓
   ```

### Permission Denied

If you get permission errors accessing shares:

1. Check credentials in the URL are correct
2. Try mounting the share manually first:
   ```bash
   sudo mount -t cifs //server/share /mnt/test -o username=user,password=pass
   ```

### Slow Performance

1. Ensure hardware acceleration is enabled (check for `-hwaccel auto` in FFmpeg command)
2. Check network speed: `iperf3 -c <server_ip>`
3. For very large files, consider mounting the share locally:
   ```bash
   sudo mount -t cifs //server/share /mnt/videos -o username=user,password=pass
   ```

## Keeping System FFmpeg

If you want to keep the system FFmpeg alongside your custom build:

```bash
# Install to a different prefix
./configure --prefix=/opt/ffmpeg-smb ...

# Run the custom version explicitly
/opt/ffmpeg-smb/bin/ffmpeg ...
```

Or set up alternatives:
```bash
sudo update-alternatives --install /usr/bin/ffmpeg ffmpeg /opt/ffmpeg-smb/bin/ffmpeg 100
```

## Uninstall Custom FFmpeg

If you need to revert to system FFmpeg:

```bash
cd ~/ffmpeg_build/ffmpeg
sudo make uninstall
sudo ldconfig

# Reinstall system FFmpeg
sudo apt install --reinstall ffmpeg
```

## Build Script

Here's a complete build script for convenience:

```bash
#!/bin/bash
# build_ffmpeg.sh - Build FFmpeg with PulseAudio + SMB support on Raspberry Pi

set -e

echo "=== Installing dependencies ==="
sudo apt update
sudo apt install -y \
    build-essential git pkg-config yasm nasm \
    libsmbclient-dev libpulse-dev \
    libx264-dev libx265-dev libvpx-dev \
    libfdk-aac-dev libmp3lame-dev libopus-dev \
    libass-dev libfreetype6-dev libvorbis-dev libtheora-dev \
    libssl-dev libv4l-dev libasound2-dev

echo "=== Downloading FFmpeg ==="
mkdir -p ~/ffmpeg_build
cd ~/ffmpeg_build
rm -rf ffmpeg
git clone --depth 1 https://git.ffmpeg.org/ffmpeg.git ffmpeg
cd ffmpeg

echo "=== Configuring FFmpeg ==="
./configure \
    --prefix=/usr/local \
    --enable-gpl \
    --enable-nonfree \
    --enable-version3 \
    --enable-libsmbclient \
    --enable-libpulse \
    --enable-libx264 \
    --enable-libx265 \
    --enable-libvpx \
    --enable-libfdk-aac \
    --enable-libmp3lame \
    --enable-libopus \
    --enable-libass \
    --enable-libfreetype \
    --enable-libvorbis \
    --enable-libtheora \
    --enable-openssl \
    --enable-v4l2-m2m

echo "=== Building FFmpeg (this takes a while) ==="
make -j$(nproc)

echo "=== Installing FFmpeg ==="
sudo make install
sudo ldconfig

echo "=== Verifying installation ==="
echo "PulseAudio output support:"
ffmpeg -formats 2>&1 | grep pulse || echo "PulseAudio NOT FOUND!"

echo ""
echo "SMB protocol support:"
ffmpeg -protocols 2>&1 | grep smb || echo "SMB NOT FOUND!"

echo ""
echo "Hardware decoders:"
ffmpeg -decoders 2>&1 | grep -E "h264_v4l2m2m|hevc_v4l2m2m"

echo ""
echo "=== Done! ==="
echo "Restart verpixeld to use the new FFmpeg."
```

Save as `build_ffmpeg.sh`, make executable with `chmod +x build_ffmpeg.sh`, and run with `./build_ffmpeg.sh`.

---

*Last updated: February 2026*
