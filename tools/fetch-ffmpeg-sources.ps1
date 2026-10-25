# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Assembles the complete corresponding source of the bundled FFmpeg build: FFmpeg itself (from
  fetch-ffmpeg.ps1) plus every statically linked library listed in tools/ffmpeg-sources.json,
  each at the exact version or commit the build reports. The result is published as a release
  asset next to every Linkast release, so the GPL source offer stays valid.

  Output: artifacts/ffmpeg/<version>/source/libs/*.tar.gz and SOURCES.txt (what was fetched, from
  where, resolved commit and SHA-256), zipped as artifacts/ffmpeg-<version>-corresponding-source.zip.

  Needs git and curl on PATH. Runs on the release build machine (GitHub Actions); it takes a while
  because several repositories are large.

.PARAMETER Only
  Fetch only the named libraries (for testing a manifest change).
#>
param([string[]]$Only = @())

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $PSScriptRoot "ffmpeg-sources.json") -Raw | ConvertFrom-Json
$version = $manifest.ffmpegVersion
$sourceDir = Join-Path $repo "artifacts\ffmpeg\$version\source"
$libs = Join-Path $sourceDir "libs"
$work = Join-Path $repo "artifacts\cache\src-git"
New-Item -ItemType Directory -Force $libs, $work | Out-Null

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("Corresponding source for FFmpeg $version as bundled with Linkast")
$lines.Add("Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm zzz')")
$lines.Add("")
$failed = @()
$gaps = @()

foreach ($lib in $manifest.libraries) {
    if ($Only.Count -gt 0 -and $lib.name -notin $Only) { continue }
    $label = "$($lib.name)-$($lib.version)" -replace '[^A-Za-z0-9._-]', '_'
    $target = Join-Path $libs "$label.tar.gz"
    if ($lib.gap) {
        # Known gap: listed, never silently skipped. See docs/licensing.md.
        $gaps += $lib.name
        $lines.Add("$($lib.name) $($lib.version)  SOURCE NOT IDENTIFIABLE: $($lib.gap)")
        Write-Warning "$($lib.name): known gap"
        continue
    }

    try {
        if ($lib.url) {
            # Try the upstream URL, then the mirror (the same upstream release from a long-lived archive).
            $origin = $null
            foreach ($candidate in @($lib.url, $lib.mirror) | Where-Object { $_ }) {
                $target = Join-Path $libs ("$label-" + (Split-Path $candidate -Leaf))
                if (-not (Test-Path $target)) {
                    curl.exe -sSfL --retry 3 --max-time 600 -o "$target.download" $candidate
                    if ($LASTEXITCODE -ne 0) { Remove-Item "$target.download" -ErrorAction SilentlyContinue; continue }
                    Move-Item "$target.download" $target -Force
                }

                $origin = $candidate
                break
            }

            if (-not $origin) { throw "download failed" }
        }
        else {
            $clone = Join-Path $work $lib.name
            if (-not (Test-Path $clone)) {
                git clone --quiet --filter=blob:none --no-checkout $lib.git $clone 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
            }

            if ($lib.count) {
                # A revision number such as x264's 3223 is the commit count from the root.
                $commit = git -c safe.directory=* -C $clone rev-list --reverse HEAD | Select-Object -Index ($lib.count - 1)
            }
            else {
                $commit = git -c safe.directory=* -C $clone rev-parse --verify --quiet "$($lib.ref)^{commit}"
            }

            if (-not $commit) { throw "ref $($lib.ref)$($lib.count) not found" }
            if (-not (Test-Path $target)) {
                git -c safe.directory=* -C $clone archive --format=tar.gz --prefix="$label/" -o $target $commit
                if ($LASTEXITCODE -ne 0) { throw "git archive failed" }
            }

            $origin = "$($lib.git) at $commit"
        }

        $hash = (Get-FileHash $target -Algorithm SHA256).Hash
        $note = if ($lib.note) { " ($($lib.note))" } else { "" }
        $lines.Add("$($lib.name) $($lib.version)$note")
        $lines.Add("  from   $origin")
        $lines.Add("  file   libs/$(Split-Path $target -Leaf)")
        $lines.Add("  sha256 $hash")
        Write-Host "OK      $($lib.name) $($lib.version)"
    }
    catch {
        $failed += $lib.name
        $lines.Add("$($lib.name) $($lib.version)  NOT FETCHED: $($_.Exception.Message)")
        Write-Warning "$($lib.name): $($_.Exception.Message)"
    }
}

$lines.Add("")
if ($gaps.Count -gt 0) { $lines.Add("Known gaps: $($gaps -join ', '), plus the unversioned libraries below.") }
foreach ($u in $manifest.unversioned) { $lines.Add($u) }
$lines.Add($manifest.buildScripts)
Set-Content (Join-Path $sourceDir "SOURCES.txt") -Encoding utf8 -Value $lines

if ($Only.Count -eq 0) {
    $zip = Join-Path $repo "artifacts\ffmpeg-$version-corresponding-source.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $sourceDir "*") -DestinationPath $zip
    Write-Host "Source bundle: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
}

if ($failed.Count -gt 0) {
    throw "Could not fetch: $($failed -join ', '). A release must not be published without complete source."
}
