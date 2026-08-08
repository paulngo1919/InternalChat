#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Enforces the constitution's per-layer coverage floors.

.DESCRIPTION
    Constitution v1.2.0, Principle III (Test-First & Unit Test Coverage, NON-NEGOTIABLE):

        Domain >= 90%   Application >= 85%   Infrastructure >= 60%   React >= 80%

        "A merge that lowers any layer below its floor fails the build."

    Coverlet can only apply a single threshold across a whole test run, so it cannot express
    these four floors. This script reads the Cobertura reports produced by
    `dotnet test --settings coverlet.runsettings` and checks each layer independently.

    A layer that produces NO coverage data is treated as a FAILURE, not as a pass. Silence is
    the failure mode that matters here: a layer whose tests stopped running would otherwise
    sail through a naive "no violations found" check.

.PARAMETER ResultsPath
    Directory containing TestResults, searched recursively for coverage.cobertura.xml.

.PARAMETER FrontendSummary
    Optional path to the React coverage-summary.json emitted by Vitest's v8 provider.

.EXAMPLE
    dotnet test --settings coverlet.runsettings --results-directory ./TestResults
    ./tools/Check-Coverage.ps1 -ResultsPath ./TestResults
#>
[CmdletBinding()]
param(
    [string] $ResultsPath = "./TestResults",
    [string] $FrontendSummary = "./src/internalchat-web/coverage/coverage-summary.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Constitution Principle III. Changing a number here is a constitution amendment, not a config tweak.
$floors = [ordered]@{
    'InternalChat.Domain'         = 90
    'InternalChat.Application'    = 85
    'InternalChat.Infrastructure' = 60
}

Write-Host ''
Write-Host 'Coverage floors (Constitution v1.2.0, Principle III)' -ForegroundColor Cyan
Write-Host '---------------------------------------------------'

if (-not (Test-Path $ResultsPath)) {
    Write-Host "FAIL  No coverage results at '$ResultsPath'." -ForegroundColor Red
    Write-Host '      Run: dotnet test --settings coverlet.runsettings --results-directory ' -NoNewline
    Write-Host $ResultsPath
    exit 1
}

$reports = @(Get-ChildItem -Path $ResultsPath -Filter 'coverage.cobertura.xml' -Recurse -ErrorAction SilentlyContinue)
if ($reports.Count -eq 0) {
    Write-Host "FAIL  No coverage.cobertura.xml found under '$ResultsPath'." -ForegroundColor Red
    exit 1
}

# Merge across reports: a layer may be exercised by more than one test project, so take the
# highest observed line-rate per assembly rather than whichever report happened to be read last.
$observed = @{}
foreach ($report in $reports) {
    [xml] $xml = Get-Content -LiteralPath $report.FullName -Raw
    foreach ($package in @($xml.coverage.packages.package)) {
        if ($null -eq $package) { continue }
        $name = [string] $package.name
        $rate = [double] $package.'line-rate' * 100.0
        if (-not $observed.ContainsKey($name) -or $rate -gt $observed[$name]) {
            $observed[$name] = $rate
        }
    }
}

$failed = $false

foreach ($layer in $floors.Keys) {
    $floor = $floors[$layer]

    if (-not $observed.ContainsKey($layer)) {
        # Absent data is a failure. A layer whose tests stopped running must not pass silently.
        Write-Host ("FAIL  {0,-30} no coverage data (floor {1}%)" -f $layer, $floor) -ForegroundColor Red
        $failed = $true
        continue
    }

    $actual = [math]::Round($observed[$layer], 2)
    if ($actual -lt $floor) {
        Write-Host ("FAIL  {0,-30} {1,6}%  < floor {2}%" -f $layer, $actual, $floor) -ForegroundColor Red
        $failed = $true
    }
    else {
        Write-Host ("PASS  {0,-30} {1,6}%  >= floor {2}%" -f $layer, $actual, $floor) -ForegroundColor Green
    }
}

# React floor, when the frontend suite has run. Absent locally is tolerated; CI always runs it
# and passes an explicit path, so a missing report there fails via the -Frontend switch below.
$reactFloor = 80
if (Test-Path $FrontendSummary) {
    $summary = Get-Content -LiteralPath $FrontendSummary -Raw | ConvertFrom-Json
    $actual = [math]::Round([double] $summary.total.lines.pct, 2)
    if ($actual -lt $reactFloor) {
        Write-Host ("FAIL  {0,-30} {1,6}%  < floor {2}%" -f 'internalchat-web', $actual, $reactFloor) -ForegroundColor Red
        $failed = $true
    }
    else {
        Write-Host ("PASS  {0,-30} {1,6}%  >= floor {2}%" -f 'internalchat-web', $actual, $reactFloor) -ForegroundColor Green
    }
}
else {
    Write-Host ("SKIP  {0,-30} no frontend coverage summary at {1}" -f 'internalchat-web', $FrontendSummary) -ForegroundColor Yellow
}

Write-Host ''
if ($failed) {
    Write-Host 'Coverage gate FAILED. Constitution Principle III is non-negotiable:' -ForegroundColor Red
    Write-Host 'add tests rather than lowering a floor. Lowering one requires a constitution amendment.' -ForegroundColor Red
    exit 1
}

Write-Host 'Coverage gate passed.' -ForegroundColor Green
exit 0
