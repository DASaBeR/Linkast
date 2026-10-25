# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Downloads the AI model files Linkast ships with into ./models and verifies them against pinned
  SHA-256 hashes. Linkast never downloads models at runtime; releases bundle these exact files.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/fetch-models.ps1
#>
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$models = Join-Path $repo "models"
New-Item -ItemType Directory -Force $models | Out-Null

# Keep in sync with src/Linkast.Effects/ModelCatalog.cs
$artifacts = @(
    @{
        File    = "rvm_mobilenetv3_fp32.onnx"
        Url     = "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx"
        Size    = 14975696
        Sha256  = "88D4531297118F595BF2FD60F6F566AEC2E559393802D1F436C380F0CBBD2828"
    },
    @{
        File    = "RVM-LICENSE.txt"
        Url     = "https://raw.githubusercontent.com/PeterL1n/RobustVideoMatting/master/LICENSE"
        Size    = $null
        Sha256  = $null
    }
)

foreach ($a in $artifacts) {
    $target = Join-Path $models $a.File
    if ($a.Sha256 -and (Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash -eq $a.Sha256)) {
        Write-Host "OK      $($a.File) (already present, hash verified)"
        continue
    }

    Write-Host "Fetching $($a.File) ..."
    $temp = "$target.download"
    curl.exe -sSfL -o $temp $a.Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $($a.Url)" }

    if ($a.Sha256) {
        $size = (Get-Item $temp).Length
        $hash = (Get-FileHash $temp -Algorithm SHA256).Hash
        if ($size -ne $a.Size -or $hash -ne $a.Sha256) {
            Remove-Item $temp -Force
            throw "Verification failed for $($a.File): size $size, SHA-256 $hash. Refusing to use it."
        }
    }

    Move-Item $temp $target -Force
    Write-Host "OK      $($a.File)"
}
