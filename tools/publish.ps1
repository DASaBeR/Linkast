# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Builds a self-contained Linkast package (no .NET or FFmpeg install needed) and, with -Installer,
  the Windows installer and update packages.

.DESCRIPTION
  Output in artifacts/:
    Linkast-<version>-win-x64/            the app folder
    Linkast-<version>-win-x64.zip         that folder zipped (for testing)
  With -Installer, also in artifacts/releases/ (upload all of it to the GitHub release):
    LinkastApp-win-Setup.exe              per-user installer, no admin rights
    LinkastApp-win-Portable.zip           portable copy that can still update
    LinkastApp-<version>-full.nupkg, releases.win.json, assets.win.json   update feed

  FFmpeg is the pinned, hash-verified build from tools/fetch-ffmpeg.ps1 unless -FfmpegDir is given.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/publish.ps1
  powershell -ExecutionPolicy Bypass -File tools/publish.ps1 -Installer
#>
param(
    [string]$Configuration = "Release",
    [string]$FfmpegDir = "",
    [string]$OutputRoot = "",
    [switch]$Installer
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo "artifacts" }

[xml]$props = Get-Content (Join-Path $repo "Directory.Build.props")
$group = $props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
$version = $group.Version
$releaseRepo = $group.LinkastReleaseRepository
$name = "Linkast-$version-win-x64"
$out = Join-Path $OutputRoot $name

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# Native Linkast Camera components (needs Visual Studio's C++ tools); copied into vcam\ by the App project.
& (Join-Path $PSScriptRoot "build-native.ps1")

Write-Host "Publishing $name ..."
dotnet publish (Join-Path $repo "src/Linkast.App/Linkast.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# FFmpeg next to Linkast.exe (Linkast looks in .\ffmpeg\ffmpeg.exe first).
$ffOut = Join-Path $out "ffmpeg"
New-Item -ItemType Directory -Force $ffOut | Out-Null
if (-not $FfmpegDir) {
    & (Join-Path $PSScriptRoot "fetch-ffmpeg.ps1")
    $ffVersion = (Get-Content (Join-Path $PSScriptRoot "ffmpeg-sources.json") -Raw | ConvertFrom-Json).ffmpegVersion
    $FfmpegDir = Join-Path $repo "artifacts\ffmpeg\$ffVersion"
    foreach ($f in "ffmpeg.exe", "LICENSE", "README.txt", "BUILD-INFO.txt") { Copy-Item (Join-Path $FfmpegDir $f) $ffOut }
}
else {
    # Development override: not pinned, never for releases.
    if ($Installer) { throw "-FfmpegDir is for local testing only; installers always use the pinned FFmpeg." }
    Copy-Item (Join-Path $FfmpegDir "ffmpeg.exe") $ffOut
    Set-Content (Join-Path $ffOut "BUILD-INFO.txt") -Encoding utf8 -Value "Unpinned development FFmpeg from $FfmpegDir. Not for distribution."
}

foreach ($f in "vcam\Linkast.VirtualCamera.dll", "vcam\LinkastCameraSetup.exe") {
    if (-not (Test-Path (Join-Path $out $f))) { throw "Linkast Camera component missing from the package: $f" }
}

# Native import libraries are build-time only.
Get-ChildItem $out -Filter "*.lib" | Remove-Item -Force

# AI models (background effects), hash-verified. Linkast verifies them again at load time.
& (Join-Path $PSScriptRoot "fetch-models.ps1")
$modelsOut = Join-Path $out "models"
New-Item -ItemType Directory -Force $modelsOut | Out-Null
foreach ($f in "rvm_mobilenetv3_fp32.onnx", "RVM-LICENSE.txt", "README.md") {
    Copy-Item (Join-Path $repo "models\$f") $modelsOut
}

# Never ship the redistributable DirectML.dll (see docs/adr/0007); Windows provides DirectML.
Get-ChildItem $out -Recurse -Filter "DirectML*.dll" | ForEach-Object { throw "DirectML redistributable found in package: $($_.FullName)" }

# Linkast license and notices.
foreach ($f in "LICENSE", "THIRD-PARTY-NOTICES.md", "README.md") { Copy-Item (Join-Path $repo $f) $out }
$releasePage = if ($releaseRepo) { "https://github.com/$releaseRepo/releases" } else { "the project's GitHub Releases page" }
Set-Content (Join-Path $out "BETA-NOTES.txt") -Encoding utf8 -Value @"
Linkast $version (beta)
=======================

Thank you for testing Linkast. Everything runs on your PC: no account, no cloud, no telemetry.

Getting started
1. Choose what to capture and your audio on the Stream tab.
2. Enter the server URL and stream key, for example:
     Twitch:  rtmp://live.twitch.tv/app            (key from the Twitch dashboard)
     YouTube: rtmps://a.rtmps.youtube.com/live2    (key from YouTube Studio)
3. Webcam tab: pick a camera, choose a preset, drag it where you want it.
4. Press Go live (Ctrl+Enter). Press Stop to end.

Windows SmartScreen may warn because beta builds are not code-signed yet:
More info > Run anyway.

Updates: Linkast checks $releasePage once when it starts, and when you press
"Check for updates". A new version is shown next to the button; it installs only when you
press "Update now". Nothing else is sent.

Your settings, encrypted stream key and logs are in %LOCALAPPDATA%\Linkast. Uninstalling keeps
them; delete that folder to remove them.

Something wrong? Press "Copy diagnostics" (stream keys are removed automatically) and open an
issue on GitHub. The test checklist we'd love help with is docs/field-test-plan.md.
"@

$zip = Join-Path $OutputRoot "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host "Done:"
Write-Host "  Folder: $out"
Write-Host ("  Zip:    $zip ({0:N0} MB)" -f ((Get-Item $zip).Length / 1MB))

if ($Installer) {
    # Package id differs from the data folder name on purpose: Velopack installs into
    # %LOCALAPPDATA%\<packId> and removes it on uninstall; settings live in %LOCALAPPDATA%\Linkast.
    $releases = Join-Path $OutputRoot "releases"
    New-Item -ItemType Directory -Force $releases | Out-Null
    $notes = Join-Path $repo "docs\release-notes\$version.md"
    $packArgs = @(
        "pack", "--packId", "LinkastApp", "--packVersion", $version, "--packDir", $out, "--runtime", "win-x64",
        "--mainExe", "Linkast.exe", "--packTitle", "Linkast", "--packAuthors", "Linkast contributors",
        "--icon", (Join-Path $repo "src\Linkast.App\Assets\linkast.ico"),
        "--splashImage", (Join-Path $repo "src\Linkast.App\Assets\linkast-logo.png"),
        "--shortcuts", "Desktop,StartMenuRoot", "--outputDir", $releases
    )
    if (Test-Path $notes) { $packArgs += @("--releaseNotes", $notes) }
    Push-Location $repo
    try {
        dotnet tool restore | Out-Null
        dotnet vpk @packArgs
        if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
    }
    finally {
        Pop-Location
    }

    Write-Host "Installer and update packages:"
    Get-ChildItem $releases | ForEach-Object { Write-Host ("  {0} ({1:N0} MB)" -f $_.Name, ($_.Length / 1MB)) }
}
