# Installs the AirPlay 2 receiver as a boot-time service (scheduled task running as SYSTEM).
# Run from an elevated PowerShell inside the unpacked release folder. Safe to re-run, e.g. to
# change the name or port.
param(
    [string]$Name = "$env:COMPUTERNAME AirPlay",
    [int]$Port = 7000
)
$ErrorActionPreference = 'Stop'
$TaskName = 'airplay2-windows'
$root = $PSScriptRoot
$bin = Join-Path $root 'usr\bin'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}
foreach ($exe in 'nqptp.exe', 'shairport-sync.exe', 'mdns-pub.exe', 'pcm-play.exe') {
    if (-not (Test-Path (Join-Path $bin $exe))) {
        throw "$exe not found in $bin -- run this from an unpacked release (or a bundle made by scripts/bundle.sh)."
    }
}
Get-ChildItem $root -Recurse -File | Unblock-File   # downloaded zips are marked as coming from the internet

# Config from template
$template = Get-Content (Join-Path $root 'config\shairport-sync.conf.in') -Raw
$config = $template.Replace('@NAME@', $Name).Replace('@PORT@', [string]$Port)
[IO.File]::WriteAllText((Join-Path $root 'config\shairport-sync.conf'), $config)
Write-Host "Config: name '$Name', port $Port"

# Firewall: inbound for the programs that listen, Private networks only
Get-NetFirewallRule -DisplayName 'airplay2-windows *' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
foreach ($exe in 'nqptp', 'shairport-sync', 'mdns-pub') {
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

# Boot task (SYSTEM, no logon needed); runtime\service.ps1 keeps running and supervises
$service = Join-Path $root 'runtime\service.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$service`""
$trigger = New-ScheduledTaskTrigger -AtStartup
$trigger.Delay = 'PT30S'
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan) -StartWhenAvailable -MultipleInstances IgnoreNew
$taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $TaskName
}
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $taskPrincipal -Description "AirPlay 2 receiver '$Name' (airplay2-windows)" -Force | Out-Null

Get-Process nqptp, shairport-sync, mdns-pub, pcm-play -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-ScheduledTask -TaskName $TaskName
Write-Host "Installed and started '$TaskName'. Pick '$Name' in the AirPlay menu. Logs: $(Join-Path $root 'logs')"
