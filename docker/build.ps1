# Publish linux-x64, bake Fonts/Extensions/Filters, build the NAS image, optional tar.
# LibVLC is included by default. Smaller image without the VLC player: -SkipVlc
param(
    [switch]$Tar,
    [switch]$SkipVlc,
    [string]$Image = "verpixeld:nas"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$cmRoot = Join-Path (Split-Path $root -Parent) "CanvasManagement"
$csproj = Join-Path $root "verpixeld\verpixeld.csproj"
$out = Join-Path $PSScriptRoot "publish"

Write-Host "Publishing linux-x64 (SkipNative=true, no GPIO .so)..."
dotnet publish $csproj -c Release -r linux-x64 --self-contained false `
    -p:SkipNative=true -p:PublishReadyToRun=false `
    -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$fontSrc = Join-Path $cmRoot "Fonts"
if (Test-Path $fontSrc) {
    $fontOut = Join-Path $out "Fonts"
    New-Item -ItemType Directory -Path $fontOut -Force | Out-Null
    $fonts = Get-ChildItem $fontSrc -Filter *.bdf
    foreach ($f in $fonts) { Copy-Item $f.FullName -Destination $fontOut -Force }
    Write-Host "Copied $($fonts.Count) BDF font(s) from $fontSrc"
} else {
    Write-Warning "No BDF source at $fontSrc - LED text will have no fonts."
}

# Same layout as CanvasManagement/deploy.ps1: unique plugin DLLs under Extensions/<name>/ and Filters/.
$appDlls = New-Object System.Collections.Generic.HashSet[string]
Get-ChildItem $out -Filter *.dll | ForEach-Object { [void]$appDlls.Add($_.Name) }

function Publish-Plugin($projPath, $destRoot) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($projPath)
    Write-Host "  publishing $name"
    $tmp = Join-Path $destRoot ("_tmp_" + $name)
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }

    dotnet publish $projPath -c Release -o $tmp --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "  publish failed: $name (skipping)"
        if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
        return
    }

    $dest = Join-Path $destRoot $name
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    $copied = 0
    Get-ChildItem $tmp -Filter *.dll | Where-Object { -not $appDlls.Contains($_.Name) } | ForEach-Object {
        Copy-Item $_.FullName -Destination $dest -Force
        $copied++
    }
    Remove-Item $tmp -Recurse -Force
    if ($copied -eq 0) {
        Remove-Item $dest -Recurse -Force
        Write-Host "    (no plugin output - skipped)"
    } else {
        Write-Host "    copied $copied dll(s)"
    }
}

$extDir = Join-Path $cmRoot "Extensions"
if (Test-Path $extDir) {
    Write-Host "Collecting extensions..."
    $extOut = Join-Path $out "Extensions"
    New-Item -ItemType Directory -Path $extOut -Force | Out-Null
    Get-ChildItem $extDir -Recurse -Filter *.csproj | ForEach-Object {
        Publish-Plugin $_.FullName $extOut
    }
}

$filterDir = Join-Path $cmRoot "Filters"
if (Test-Path $filterDir) {
    Write-Host "Collecting filters..."
    $filterOut = Join-Path $out "Filters"
    New-Item -ItemType Directory -Path $filterOut -Force | Out-Null
    Get-ChildItem $filterDir -Recurse -Filter *.csproj | ForEach-Object {
        Publish-Plugin $_.FullName $filterOut
    }
}

$vlc = if ($SkipVlc) { "0" } else { "1" }
Write-Host "Building $Image (VLC=$vlc)..."
docker build --pull --build-arg "VLC=$vlc" -t $Image -f (Join-Path $PSScriptRoot "Dockerfile") $PSScriptRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Tar) {
    $tarPath = Join-Path $PSScriptRoot "verpixeld-nas.tar"
    Write-Host "Saving $tarPath ..."
    docker save $Image -o $tarPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Portainer: Images -> Import -> $tarPath"
}

Write-Host "Done. Compose: docker/docker-compose.yml"
