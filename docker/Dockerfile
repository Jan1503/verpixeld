# Debian/Ubuntu ffmpeg is built without the smb protocol. The Pi host uses a
# custom build (--enable-libsmbclient). Compile the same here so Network Shares
# play works; smbclient is still used for directory browsing.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS ffmpeg-smb

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      build-essential yasm nasm pkg-config wget ca-certificates xz-utils \
      libsmbclient-dev libgnutls28-dev \
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
      --disable-doc \
      --disable-ffplay \
      --disable-debug \
 && make -j"$(nproc)" \
 && make install \
 && rm -rf /tmp/ffmpeg /tmp/ffmpeg.tar.gz

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble

COPY --from=ffmpeg-smb /usr/local /usr/local

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates curl \
      fonts-dejavu-core \
      libsmbclient libgnutls30t64 \
      smbclient \
      python3 python3-pip \
 && echo /usr/local/lib > /etc/ld.so.conf.d/local-ffmpeg.conf \
 && ldconfig \
 && pip3 install --no-cache-dir --break-system-packages yt-dlp \
 && rm -rf /var/lib/apt/lists/* \
 && ffmpeg -hide_banner -version \
 && ffmpeg -hide_banner -protocols 2>&1 | grep -E '^[[:space:]]*smb[[:space:]]*$' \
 && yt-dlp --version \
 && smbclient -V

WORKDIR /app
COPY publish/ ./

ENV PATH="/usr/local/bin:${PATH}" \
    LD_LIBRARY_PATH="/usr/local/lib" \
    ASPNETCORE_ENVIRONMENT=Docker \
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
