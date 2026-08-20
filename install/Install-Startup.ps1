<#
.SYNOPSIS
    Registers Blade Fan Curve as an elevated logon task.

.DESCRIPTION
    A Scheduled Task is used instead of a Run registry entry because the app needs
    administrator rights; a Run entry would raise a UAC prompt at every logon.

    The task overrides the two Scheduled Task defaults that would otherwise break
    on a laptop: refusing to start on battery, and stopping when unplugged.

    The app has the same button under Settings; this script exists for unattended
    or scripted setups.

.EXAMPLE
    .\Install-Startup.ps1 -ExePath "C:\Tools\BladeFanCurve\BladeFanCurve.exe"
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ExePath = (Join-Path $PSScriptRoot '..\BladeFanCurve.exe'),

    [Parameter()]
    [string]$TaskName = 'BladeFanCurve'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell prompt.'
}

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath"
}

$user = "$env:USERDOMAIN\$env:USERNAME"

$action = New-ScheduledTaskAction -Execute $ExePath -Argument '--tray' `
    -WorkingDirectory (Split-Path -Parent $ExePath)

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Delay = 'PT10S'

$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $TaskName `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Temperature-driven fan control for Razer Blade laptops.' `
    -Force | Out-Null

Write-Host "Registered '$TaskName' to start at logon." -ForegroundColor Green
Write-Host "  Executable : $ExePath"
Write-Host "  User       : $user"
Write-Host ''
Write-Host 'Start it now with:  Start-ScheduledTask -TaskName ' -NoNewline
Write-Host $TaskName
