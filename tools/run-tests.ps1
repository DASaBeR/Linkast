# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Builds Linkast, runs every test (unit and integration) and writes a test and performance report to
  docs/reports/test-runs/<date>-<commit>.md, plus one line per run in docs/reports/test-runs/history.csv.

  Integration tests need this PC's real hardware; the local ingest container (tools/test-ingest) is started
  when Docker is running, otherwise the streaming tests are reported as skipped. The raw .trx files stay in
  artifacts/test-runs (not committed).

  Opt-in tests run only when their variables are set in the shell: LINKAST_LIVE_URL + LINKAST_LIVE_KEY
  (live stream to a real platform), LINKAST_SOAK_MINUTES (long soak). Keys are never written to the report.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/run-tests.ps1
  powershell -ExecutionPolicy Bypass -File tools/run-tests.ps1 -UnitOnly
#>
param(
    [switch]$UnitOnly,
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$commit = (git rev-parse --short HEAD).Trim()
$dirty = if (git status --porcelain --untracked-files=no) { " (uncommitted changes)" } else { "" }
$stamp = Get-Date -Format "yyyy-MM-dd-HHmm"
$rawDir = Join-Path $repo "artifacts\test-runs\$stamp"
$reportDir = Join-Path $repo "docs\reports\test-runs"
New-Item -ItemType Directory -Force $rawDir, $reportDir | Out-Null
[xml]$props = Get-Content Directory.Build.props
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

# Local ingest for the streaming tests
$ingest = "not used (unit tests only)"
if (-not $UnitOnly) {
    $ingest = "Docker not running: streaming tests skipped"
    # Through cmd: Windows PowerShell turns Docker's harmless stderr warnings into errors
    cmd /c "docker info >nul 2>&1"
    if ($LASTEXITCODE -eq 0) {
        cmd /c "docker compose -f tools/test-ingest/docker-compose.yml up -d >nul 2>&1"
        if ($LASTEXITCODE -eq 0) { $ingest = "MediaMTX container (tools/test-ingest)" }
    }
}

# This PC, so numbers from different machines are not compared blindly
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
$cores = (Get-CimInstance Win32_Processor | Measure-Object NumberOfLogicalProcessors -Sum).Sum
$ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
$gpus = (Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) (driver $($_.DriverVersion))" }) -join "; "
$os = (Get-CimInstance Win32_OperatingSystem)
$osText = "$($os.Caption) $($os.Version)"

Write-Host "Building ($Configuration)..."
dotnet build Linkast.slnx -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$filter = if ($UnitOnly) { "FullyQualifiedName!~Linkast.IntegrationTests" } else { "FullyQualifiedName~Linkast" }
Write-Host "Running tests..."
$wall = [Diagnostics.Stopwatch]::StartNew()
dotnet test Linkast.slnx -c $Configuration --no-build --nologo --filter $filter `
    --logger "trx;LogFilePrefix=run" --results-directory $rawDir
$testExit = $LASTEXITCODE
$wall.Stop()

# Read every .trx
$results = @()
foreach ($trx in Get-ChildItem $rawDir -Filter *.trx) {
    [xml]$doc = Get-Content $trx.FullName -Raw
    $ns = New-Object Xml.XmlNamespaceManager $doc.NameTable
    $ns.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
    $projects = @{}
    foreach ($def in $doc.SelectNodes("//t:UnitTest", $ns)) {
        $projects[$def.id] = [IO.Path]::GetFileNameWithoutExtension($def.storage)
    }
    foreach ($r in $doc.SelectNodes("//t:UnitTestResult", $ns)) {
        $out = $r.SelectSingleNode("t:Output/t:StdOut", $ns)
        $err = $r.SelectSingleNode("t:Output/t:ErrorInfo/t:Message", $ns)
        $results += [pscustomobject]@{
            Project  = $projects[$r.testId]
            Name     = $r.testName
            Outcome  = $r.outcome
            Seconds  = if ($r.duration) { [TimeSpan]::Parse($r.duration).TotalSeconds } else { 0 }
            StdOut   = if ($out) { $out.InnerText } else { "" }
            Message  = if ($err) { $err.InnerText } else { "" }
        }
    }
}

# Never let a stream key reach the report
$secrets = @($env:LINKAST_LIVE_KEY, $env:LINKAST_SOAK_KEY) | Where-Object { $_ }
function Hide([string]$text) {
    foreach ($s in $secrets) { $text = $text.Replace($s, "<key>") }
    return $text
}

$passed = @($results | Where-Object Outcome -eq "Passed").Count
$failed = @($results | Where-Object Outcome -eq "Failed").Count
$skipped = @($results | Where-Object { $_.Outcome -eq "NotExecuted" -or $_.Outcome -eq "Skipped" }).Count
$verdict = if ($failed -eq 0 -and $testExit -eq 0) { "PASS" } else { "FAIL" }

$md = New-Object Text.StringBuilder
function Line([string]$s = "") { [void]$md.AppendLine($s) }

Line "# Test run ${stamp}: $verdict"
Line
Line "| | |"
Line "|---|---|"
Line "| Linkast | $version, commit ``$commit``$dirty |"
Line "| Scope | $(if ($UnitOnly) { 'Unit tests' } else { 'Unit and integration tests' }), $Configuration |"
Line "| Ingest | $ingest |"
Line "| PC | $cpu, $cores threads, $ramGb GB RAM |"
Line "| GPU | $gpus |"
Line "| Windows | $osText |"
Line "| Result | $passed passed, $failed failed, $skipped skipped, $($results.Count) total in $([math]::Round($wall.Elapsed.TotalMinutes, 1)) min |"
Line

Line "## By project"
Line
Line "| Project | Passed | Failed | Skipped | Time (s) |"
Line "|---|---:|---:|---:|---:|"
foreach ($g in $results | Group-Object Project | Sort-Object Name) {
    $p = @($g.Group | Where-Object Outcome -eq "Passed").Count
    $f = @($g.Group | Where-Object Outcome -eq "Failed").Count
    $s = $g.Count - $p - $f
    Line "| $($g.Name) | $p | $f | $s | $([math]::Round(($g.Group | Measure-Object Seconds -Sum).Sum, 1)) |"
}
Line

if ($failed -gt 0) {
    Line "## Failures"
    Line
    foreach ($r in $results | Where-Object Outcome -eq "Failed") {
        Line "### $($r.Name)"
        Line
        Line '```'
        Line (Hide ($r.Message.Trim()))
        Line '```'
        Line
    }
}

# Performance: the measurements integration tests print (fps, ms, kbps, speed and similar)
$metric = '\d+(\.\d+)?\s*(fps|ms|kbps|MB|dB|x\b|s\b)|speed[ =]\d'
Line "## Performance"
Line
Line "Measurements the tests printed on this PC. Compare runs only on the same PC."
Line
$perfTests = $results | Where-Object { $_.Outcome -eq "Passed" -and $_.StdOut } | Sort-Object Name
$anyPerf = $false
foreach ($r in $perfTests) {
    $lines = @($r.StdOut -split "`r?`n" | Where-Object {
        $_ -match $metric -and $_ -notmatch '^\s*ffmpeg:' -and $_ -notmatch '^\d\d:\d\d:\d\d state ' -and $_ -notmatch 'frame='
    } | Select-Object -First 12)
    if ($lines.Count -eq 0) { continue }
    $anyPerf = $true
    Line "**$($r.Name)** ($([math]::Round($r.Seconds, 1)) s)"
    Line
    Line '```'
    foreach ($l in $lines) { Line (Hide $l.Trim()) }
    Line '```'
    Line
}
if (-not $anyPerf) { Line "No measurements in this run (integration tests skipped or not run)."; Line }

Line "## Slowest tests"
Line
Line "| Test | Time (s) | Result |"
Line "|---|---:|---|"
foreach ($r in $results | Sort-Object Seconds -Descending | Select-Object -First 15) {
    Line "| $($r.Name) | $([math]::Round($r.Seconds, 1)) | $($r.Outcome) |"
}
Line

$skippedList = @($results | Where-Object { $_.Outcome -eq "NotExecuted" -or $_.Outcome -eq "Skipped" })
if ($skippedList.Count -gt 0) {
    Line "## Skipped"
    Line
    foreach ($r in $skippedList | Sort-Object Name) {
        $why = (Hide ($r.Message -split "`r?`n")[0]).Trim()
        Line "- $($r.Name)$(if ($why) { ": $why" })"
    }
    Line
}

$reportName = "$stamp-$commit.md"
$md.ToString() | Out-File (Join-Path $reportDir $reportName) -Encoding utf8

$history = Join-Path $reportDir "history.csv"
if (-not (Test-Path $history)) {
    "date,commit,version,scope,result,passed,failed,skipped,minutes,cpu,report" | Out-File $history -Encoding utf8
}
$scope = if ($UnitOnly) { "unit" } else { "all" }
"$stamp,$commit,$version,$scope,$verdict,$passed,$failed,$skipped,$([math]::Round($wall.Elapsed.TotalMinutes, 1)),`"$cpu`",$reportName" |
    Out-File $history -Append -Encoding utf8

Write-Host ""
Write-Host "$verdict - $passed passed, $failed failed, $skipped skipped. Report: docs/reports/test-runs/$reportName"
exit $(if ($verdict -eq "PASS") { 0 } else { 1 })
