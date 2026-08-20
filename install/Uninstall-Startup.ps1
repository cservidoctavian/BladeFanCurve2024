<#
.SYNOPSIS
    Removes the Blade Fan Curve logon task.

.DESCRIPTION
    Stops the task if it is running and unregisters it. This does not touch the
    executable or the settings under %AppData%\BladeFanCurve.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$TaskName = 'BladeFanCurve'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell prompt.'
}

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host "No task named '$TaskName' is registered." -ForegroundColor Yellow
    return
}

if ($task.State -eq 'Running') {
    Stop-ScheduledTask -TaskName $TaskName
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Host "Removed '$TaskName'." -ForegroundColor Green
Write-Host 'Settings and logs are still under %AppData%\BladeFanCurve.'
