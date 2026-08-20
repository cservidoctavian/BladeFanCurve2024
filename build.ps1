<#
.SYNOPSIS
    Builds Blade Fan Curve.

.DESCRIPTION
    Two output shapes:

      -Standalone   One ~68 MB BladeFanCurve.exe with the .NET runtime baked in.
                    Nothing to install, runs on any Windows 10/11 x64 machine.

      (default)     One ~5 MB BladeFanCurve.exe that needs the .NET 8 Desktop
                    Runtime present on the machine.

    Requires the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8

.EXAMPLE
    .\build.ps1 -Standalone

.EXAMPLE
    .\build.ps1 -Test
#>

[CmdletBinding()]
param(
    [switch]$Standalone,
    [switch]$Test,
    [string]$Output = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\BladeFanCurve'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install it with:  winget install Microsoft.DotNet.SDK.8'
}

if ($Test) {
    Write-Host 'Running the test suite (no hardware needed)…' -ForegroundColor Cyan
    dotnet run --project (Join-Path $PSScriptRoot 'tests\ProtocolTests') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    Write-Host ''
}

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

$common = @(
    '-c', 'Release',
    '-o', $Output,
    '--nologo',
    '-p:RuntimeIdentifier=win-x64',
    '-p:PublishSingleFile=true'
)

if ($Standalone) {
    Write-Host 'Building standalone (runtime included)…' -ForegroundColor Cyan
    dotnet publish $project @common -p:SelfContained=true -p:EnableCompressionInSingleFile=true
} else {
    Write-Host 'Building framework-dependent (needs the .NET 8 Desktop Runtime)…' -ForegroundColor Cyan
    dotnet publish $project @common -p:SelfContained=false -p:EnableCompressionInSingleFile=false
}

if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $Output 'BladeFanCurve.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host "Built $exe ($size MB)" -ForegroundColor Green
Write-Host 'Run it as Administrator, and quit Razer Synapse first.'
