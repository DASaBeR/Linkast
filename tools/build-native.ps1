# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Builds the native Linkast Camera components (virtual camera media source and its elevated setup
  helper) with MSBuild from Visual Studio (C++ workload) into artifacts/native/<Configuration>/.
#>
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio with the C++ workload is required (vswhere.exe not found)." }
$msbuild = & $vswhere -latest -prerelease -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild with the C++ tools was not found. Install Visual Studio's 'Desktop development with C++' workload." }

foreach ($project in "native\Linkast.VirtualCamera\Linkast.VirtualCamera.vcxproj", "native\LinkastCameraSetup\LinkastCameraSetup.vcxproj") {
    & $msbuild (Join-Path $repo $project) -nologo -verbosity:minimal -m `
        "-p:Configuration=$Configuration" "-p:Platform=x64" "-p:SolutionDir=$(Join-Path $repo 'native')\"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

$out = Join-Path $repo "artifacts\native\$Configuration"
Get-ChildItem $out -Include *.dll, *.exe -Recurse | ForEach-Object { Write-Host ("Built {0} ({1:N0} KB)" -f $_.Name, ($_.Length / 1KB)) }
