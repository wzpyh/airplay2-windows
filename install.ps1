# Installs the AirPlay 2 receiver as a boot-time service (scheduled task running as SYSTEM).
# Run from an elevated PowerShell inside the unpacked release folder. Safe to re-run, e.g. to
# change the name, port or output device.
#   -OutputDevice   always play to this device (its name, or a part of it, as -ListOutputDevices
#                   shows it); empty = the Windows default device, followed when it changes
param(
    [string]$Name = "$env:COMPUTERNAME AirPlay",
    [int]$Port = 7000,
    [string]$OutputDevice = '',
    [switch]$ListOutputDevices
)
$ErrorActionPreference = 'Stop'
$TaskName = 'airplay2-windows'
$root = $PSScriptRoot
$bin = Join-Path $root 'usr\bin'

if ($ListOutputDevices) {
    # render endpoints are SWD\MMDEVAPI\{0.0.0.00000000}.{guid}; capture ones are {0.0.1...}
    Get-PnpDevice -Class AudioEndpoint |
        Where-Object InstanceId -like 'SWD\MMDEVAPI\{0.0.0.*' |
        Sort-Object @{ Expression = { $_.Status -ne 'OK' } }, FriendlyName |
        Format-Table @{ Label = 'Output device'; Expression = { $_.FriendlyName } },
                     @{ Label = 'Active'; Expression = { $_.Status -eq 'OK' } } -AutoSize
    return
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}
foreach ($exe in 'nqptp.exe', 'shairport-sync.exe') {
    if (-not (Test-Path (Join-Path $bin $exe))) {
        throw "$exe not found in $bin -- run this from an unpacked release (or a bundle made by scripts/bundle.sh)."
    }
}
Get-ChildItem $root -Recurse -File | Unblock-File   # downloaded zips are marked as coming from the internet

# Config from template
$template = Get-Content (Join-Path $root 'config\shairport-sync.conf.in') -Raw
$deviceValue = $OutputDevice.Trim().Replace('\', '\\').Replace('"', '\"')   # libconfig string escapes
$config = $template.Replace('@NAME@', $Name).Replace('@PORT@', [string]$Port).Replace('@OUTPUT_DEVICE@', $deviceValue)
[IO.File]::WriteAllText((Join-Path $root 'config\shairport-sync.conf'), $config)
$deviceText = if ($OutputDevice.Trim()) { "'$($OutputDevice.Trim())'" } else { 'Windows default' }
Write-Host "Config: name '$Name', port $Port, output device $deviceText"

# Firewall: inbound for the programs that listen, Private networks only
Get-NetFirewallRule -DisplayName 'airplay2-windows *' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
foreach ($exe in 'nqptp', 'shairport-sync') {
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

# Stop any running airplay2-windows instance, including one installed in another folder (an
# upgrade unpacked elsewhere): stopping the task ends service.ps1 but not the programs it started.
Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" |
    Where-Object { $_.CommandLine -like '*\runtime\service.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-Process nqptp, shairport-sync -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and (Test-Path (Join-Path (Split-Path (Split-Path (Split-Path $_.Path))) 'runtime\service.ps1')) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Start-ScheduledTask -TaskName $TaskName
Write-Host "Installed and started '$TaskName'. Pick '$Name' in the AirPlay menu. Logs: $(Join-Path $root 'logs')"
