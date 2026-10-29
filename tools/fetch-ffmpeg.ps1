# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Downloads the exact FFmpeg build Linkast ships, and FFmpeg's matching source code, and verifies
  both against pinned SHA-256 hashes. Output: artifacts/ffmpeg/<version>/ (binary, license, build
  info) and artifacts/ffmpeg/<version>/source/ (source tarball, published next to each release).

  Pinned build: gyan.dev 9.0.1 "essentials" (GPL v3), from its versioned GitHub release. It has
  everything Linkast uses (gfxcapture, ddagrab, zmq, NVENC/AMF/QSV/Media Foundation, x264, TLS)
  at less than half the size of the "full" build. FFmpeg source: the official n9.0.1 release
  tarball, which is the commit the build reports (bf1b838f2a).

  To change the build, update the table below and run the integration tests with
  LINKAST_FFMPEG pointing at the new ffmpeg.exe.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/fetch-ffmpeg.ps1
#>
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$pin = @{
    Version       = "9.0.1"
    Variant       = "essentials_build"
    BuildUrl      = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.1/ffmpeg-9.0.1-essentials_build.zip"
    BuildSha256   = "FEC81AE03971D9DD4BE3EBE02E263BD2EC1D789483F931BDBA5F5715E65DA2E9"
    FfmpegSha256  = "72A489ECCD008C2EC2C0A5856C5C75BC3D8BBFA90166C4566865C246445E6AA3"
    SourceUrl     = "https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz"
    SourceSha256  = "CF38E0E28C7E5605942C4A77755349B0145804A397AF37EB1FB4C77CB237F635"
    SourceCommit  = "bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa"
}

$cache = Join-Path $repo "artifacts\cache"
$out = Join-Path $repo "artifacts\ffmpeg\$($pin.Version)"
$sourceOut = Join-Path $out "source"
New-Item -ItemType Directory -Force $cache, $out, $sourceOut | Out-Null

function Get-Verified([string]$url, [string]$sha256) {
    $file = Join-Path $cache (Split-Path $url -Leaf)
    if ((Test-Path $file) -and (Get-FileHash $file -Algorithm SHA256).Hash -eq $sha256) {
        Write-Host "OK      $(Split-Path $file -Leaf) (cached, hash verified)"
        return $file
    }

    Write-Host "Fetching $url ..."
    $temp = "$file.download"
    # Retries: a release once failed because ffmpeg.org did not answer the build machine for 21 s.
    curl.exe -sSfL --retry 4 --retry-all-errors --retry-delay 10 --connect-timeout 30 --max-time 900 -o $temp $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
    $hash = (Get-FileHash $temp -Algorithm SHA256).Hash
    if ($hash -ne $sha256) {
        Remove-Item $temp -Force
        throw "Verification failed for $url (SHA-256 $hash). Refusing to use it."
    }

    Move-Item $temp $file -Force
    Write-Host "OK      $(Split-Path $file -Leaf) (hash verified)"
    return $file
}

# The FFmpeg tarball, also inside the corresponding source every Linkast release publishes: used when
# ffmpeg.org can't be reached. Same pinned hash either way, so it is the identical file.
function Get-SourceFromRelease {
    $tarball = Split-Path $pin.SourceUrl -Leaf
    $file = Join-Path $cache $tarball
    $repoName = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { "DASaBeR/Linkast" }
    $url = "https://github.com/$repoName/releases/latest/download/ffmpeg-$($pin.Version)-corresponding-source.zip"
    $released = Join-Path $cache "released-ffmpeg-$($pin.Version)-corresponding-source.zip"
    Write-Host "Fetching $tarball from the latest Linkast release: $url ..."
    curl.exe -sSfL --retry 4 --retry-all-errors --retry-delay 10 --connect-timeout 30 --max-time 1800 -o $released $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($released)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $tarball } | Select-Object -First 1
        if (-not $entry) { throw "$tarball is not in $url" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "$file.download", $true)
    }
    finally {
        $archive.Dispose()
    }

    $hash = (Get-FileHash "$file.download" -Algorithm SHA256).Hash
    if ($hash -ne $pin.SourceSha256) {
        Remove-Item "$file.download" -Force
        throw "Verification failed for $tarball from the latest release (SHA-256 $hash). Refusing to use it."
    }

    Move-Item "$file.download" $file -Force
    Remove-Item $released -Force
    Write-Host "OK      $tarball (from the latest release, hash verified)"
    return $file
}

$zip = Get-Verified $pin.BuildUrl $pin.BuildSha256
try {
    $source = Get-Verified $pin.SourceUrl $pin.SourceSha256
}
catch {
    Write-Host "$($_.Exception.Message)"
    $source = Get-SourceFromRelease
}

Copy-Item $source $sourceOut -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $archive.Entries | Where-Object { $_.Name -in "ffmpeg.exe", "LICENSE", "README.txt" }) {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $out $entry.Name), $true)
    }
}
finally {
    $archive.Dispose()
}

$exe = Join-Path $out "ffmpeg.exe"
$exeHash = (Get-FileHash $exe -Algorithm SHA256).Hash
if ($exeHash -ne $pin.FfmpegSha256) { throw "ffmpeg.exe inside the archive has SHA-256 $exeHash, expected $($pin.FfmpegSha256)." }

$configure = (& $exe -hide_banner -buildconf 2>$null | Where-Object { $_ -match '^\s+--' } | ForEach-Object { $_.Trim() }) -join " "
Set-Content (Join-Path $out "BUILD-INFO.txt") -Encoding utf8 -Value @"
FFmpeg bundled with Linkast
===========================

Version:        $($pin.Version) ($($pin.Variant), www.gyan.dev), license GPL v3
Binary:         $($pin.BuildUrl)
Archive SHA-256 $($pin.BuildSha256)
ffmpeg.exe SHA-256 $exeHash

FFmpeg source:  $($pin.SourceUrl)
Source SHA-256  $($pin.SourceSha256)
Source commit:  https://github.com/FFmpeg/FFmpeg/commit/$($pin.SourceCommit) (tag n$($pin.Version))
The source tarball is also published with every Linkast release (ffmpeg-$($pin.Version).tar.xz).

Configure line:
$configure

Statically linked libraries are listed in README.txt in this folder. Their source code is
available from each project; see docs/licensing.md in the Linkast repository for the status of
the complete corresponding source.

Linkast runs FFmpeg as a separate program; it does not link against it.
"@

Write-Host "FFmpeg $($pin.Version) ready in $out"
