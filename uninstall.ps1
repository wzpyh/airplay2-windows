# Removes the scheduled task and firewall rules created by install.ps1 and stops the receiver.
# Built binaries in <MSYS2>\usr\local are left in place.
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

Unregister-ScheduledTask -TaskName 'airplay2-windows' -Confirm:$false -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName 'airplay2-windows *' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Stop-Process -Name shairport-sync, nqptp, mdns-pub, gst-launch-1.0 -Force -ErrorAction SilentlyContinue
Write-Host 'Uninstalled.'
