# Installs the AirPlay 2 receiver as a boot-time service (scheduled task running as SYSTEM).
# Run from an elevated PowerShell after scripts/build.sh has finished. Safe to re-run.
param(
    [string]$Name = "$env:COMPUTERNAME AirPlay",
    [int]$Port = 7000,
    [string]$MsysRoot = 'C:\msys64'
)
$ErrorActionPreference = 'Stop'
$TaskName = 'airplay2-windows'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

$bin = Join-Path $MsysRoot 'usr\local\bin'
foreach ($exe in 'nqptp.exe', 'shairport-sync.exe', 'mdns-pub.exe') {
    if (-not (Test-Path (Join-Path $bin $exe))) { throw "$exe not found in $bin -- run scripts/build.sh first." }
}

# Config from template
$template = Get-Content (Join-Path $PSScriptRoot 'config\shairport-sync.conf.in') -Raw
$config = $template.Replace('@NAME@', $Name).Replace('@PORT@', [string]$Port)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'config\shairport-sync.conf'), $config)
Write-Host "Config: name '$Name', port $Port"

# Firewall: inbound for the three programs, Private networks only
foreach ($exe in 'nqptp', 'shairport-sync', 'mdns-pub') {
    Get-NetFirewallRule -DisplayName "airplay2-windows $exe*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    foreach ($proto in 'TCP', 'UDP') {
        New-NetFirewallRule -DisplayName "airplay2-windows $exe $proto" -Direction Inbound -Action Allow `
            -Profile Private -Program (Join-Path $bin "$exe.exe") -Protocol $proto | Out-Null
    }
}
Write-Host 'Firewall rules added (Private profile).'

$public = Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public'
if ($public) {
    Write-Warning ("These networks are 'Public', where the receiver is blocked and cannot be discovered: " +
        ($public.InterfaceAlias -join ', ') +
        ". If your home network is among them: Set-NetConnectionProfile -InterfaceAlias '<name>' -NetworkCategory Private")
}

# Boot task (SYSTEM, no logon needed)
$start = Join-Path $PSScriptRoot 'runtime\start.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$start`" -MsysRoot `"$MsysRoot`""
$trigger = New-ScheduledTaskTrigger -AtStartup
$trigger.Delay = 'PT30S'
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan) -StartWhenAvailable
$taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $taskPrincipal -Description "AirPlay 2 receiver '$Name' (shairport-sync)" -Force | Out-Null

# (Re)start now
Stop-Process -Name shairport-sync, nqptp, mdns-pub, gst-launch-1.0 -Force -ErrorAction SilentlyContinue
Get-Process bash -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$MsysRoot*" } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-ScheduledTask -TaskName $TaskName
Write-Host "Installed and started scheduled task '$TaskName'. Logs: $(Join-Path $PSScriptRoot 'logs')"
