# Removes the scheduled task and firewall rules created by install.ps1 and stops the receiver.
# The folder itself is left in place; delete it afterwards if you like.
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}
$bin = Join-Path $PSScriptRoot 'usr\bin'

if (Get-ScheduledTask -TaskName 'airplay2-windows' -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName 'airplay2-windows'
    Unregister-ScheduledTask -TaskName 'airplay2-windows' -Confirm:$false
}
Get-NetFirewallRule -DisplayName 'airplay2-windows *' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-Process nqptp, shairport-sync -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host 'Uninstalled.'
