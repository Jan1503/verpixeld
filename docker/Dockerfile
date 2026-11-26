# Debian/Ubuntu ffmpeg is built without the smb protocol. The Pi host uses a
# custom build (--enable-libsmbclient). Compile the same here so Network Shares
# play works; smbclient is still used for directory browsing.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS ffmpeg-smb

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      build-essential yasm nasm pkg-config wget ca-certificates xz-utils \
      libsmbclient-dev libgnutls28-dev \
      libasound2-dev libpulse-dev \
 && rm -rf /var/lib/apt/lists/*

# Pinned source. First image build is slow; later builds hit this layer cache.
ARG FFMPEG_TAG=n7.1.1
RUN wget -q "https://github.com/FFmpeg/FFmpeg/archive/refs/tags/${FFMPEG_TAG}.tar.gz" -O /tmp/ffmpeg.tar.gz \
 && mkdir -p /tmp/ffmpeg && tar -xzf /tmp/ffmpeg.tar.gz -C /tmp/ffmpeg --strip-components=1 \
 && cd /tmp/ffmpeg \
 && ./configure \
      --prefix=/usr/local \
      --enable-gpl \
      --enable-version3 \
      --enable-libsmbclient \
      --enable-gnutls \
      --enable-libpulse \
      --disable-doc \
      --disable-ffplay \
      --disable-debug \
 && grep -q '^#define CONFIG_ALSA_OUTDEV 1' config_components.h \
 && make -j"$(nproc)" \
 && make install \
 && rm -rf /tmp/ffmpeg /tmp/ffmpeg.tar.gz

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble

COPY --from=ffmpeg-smb /usr/local /usr/local

# LibVLC is large (codecs + plugins). Default on for the VLC Media Player extension.
# Skip: docker build --build-arg VLC=0   or   docker/build.ps1 -SkipVlc
ARG VLC=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates curl \
      fonts-dejavu-core \
      libsmbclient libgnutls30t64 \
      libasound2t64 libpulse0 \
      smbclient \
      python3 python3-pip \
      tzdata \
 && if [ "$VLC" = "1" ]; then \
      apt-get install -y --no-install-recommends \
        libvlc-dev vlc-plugin-base vlc-plugin-video-output; \
    fi \
 && echo /usr/local/lib > /etc/ld.so.conf.d/local-ffmpeg.conf \
 && ldconfig \
 && ln -snf /usr/share/zoneinfo/Europe/Berlin /etc/localtime \
 && echo Europe/Berlin > /etc/timezone \
 && rm -rf /var/lib/apt/lists/* \
 && ffmpeg -hide_banner -version \
 && ffmpeg -hide_banner -protocols 2>&1 | grep -E '^[[:space:]]*smb[[:space:]]*$' \
 && ffmpeg -hide_banner -formats 2>/dev/null | grep -E '^[[:space:]]*D?E.?[[:space:]]+alsa' \
 && smbclient -V \
 && if [ "$VLC" = "1" ]; then test -e /usr/lib/x86_64-linux-gnu/libvlc.so; \
      find /usr/lib -name 'libalsa_plugin.so' -delete; fi

# yt-dlp changes with YouTube, so it gets its own layer that every image build refreshes (build.ps1
# passes the date as YTDLP_REFRESH; a cached layer used to keep the version of the first build).
# YouTube needs a JavaScript runtime for its challenges: Deno (yt-dlp's default, >= 2.3) plus the
# yt-dlp-ejs scripts from the "default" extra.
ARG DENO_VERSION=v2.9.8
ARG YTDLP_REFRESH=unset
RUN echo "yt-dlp refresh: ${YTDLP_REFRESH}" \
 && apt-get update \
 && apt-get install -y --no-install-recommends unzip \
 && curl -fsSL "https://github.com/denoland/deno/releases/download/${DENO_VERSION}/deno-x86_64-unknown-linux-gnu.zip" -o /tmp/deno.zip \
 && unzip -q /tmp/deno.zip -d /usr/local/bin \
 && chmod +x /usr/local/bin/deno \
 && rm -f /tmp/deno.zip \
 && apt-get purge -y unzip && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/* \
 && pip3 install --no-cache-dir --break-system-packages -U "yt-dlp[default]" \
 && deno --version \
 && yt-dlp --version

WORKDIR /app
COPY publish/ ./

ENV PATH="/usr/local/bin:${PATH}" \
    LD_LIBRARY_PATH="/usr/local/lib:/usr/lib/x86_64-linux-gnu" \
    VLC_PLUGIN_PATH="/usr/lib/x86_64-linux-gnu/vlc/plugins" \
    ASPNETCORE_ENVIRONMENT=Docker \
    TZ=Europe/Berlin \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    # Survive Portainer/TrueNAS overwriting the environment name (they often set Production).
    App__OutputMode=network \
    WebServer__EnableHttps=false \
    WebServer__HttpPort=5000 \
    # CreateBuilder file watchers hang when /app/Data|/Config|/Media are volumes.
    DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false \
    ASPNETCORE_hostBuilder__reloadConfigOnChange=false \
    DOTNET_USE_POLLING_FILE_WATCHER=true \
    DOTNET_EnableDiagnostics=0 \
    # Workstation GC: same as the Pi csproj — shorter pauses than Server GC.
    DOTNET_gcServer=0

EXPOSE 5000
ENTRYPOINT ["dotnet", "verpixeld.dll"]
