# Runs the receiver and keeps it running. Started at boot by the scheduled task that
# install.ps1 creates (as SYSTEM); can also be run by hand for testing.
#
#   nqptp (PTP clock)  +  shairport-sync -c config | pcm-play  (PCM to the default audio device)
#
# If the pipeline exits (crash, audio device removed, ...), everything is restarted after 3 s.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'usr\bin'
$logs = Join-Path $root 'logs'
$conf = Join-Path $root 'config\shairport-sync.conf'
New-Item -ItemType Directory -Force $logs | Out-Null

if (-not (Test-Path $conf)) { throw "missing $conf -- run install.ps1 first" }

# mdns-pub.exe is started by shairport-sync via PATH
$env:PATH = "$bin;$env:PATH"

function Stop-Receiver {
    Get-Process nqptp, shairport-sync, mdns-pub, pcm-play -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

while ($true) {
    Stop-Receiver
    Remove-Item (Join-Path $root 'dev\shm\nqptp*') -Force -ErrorAction SilentlyContinue

    # Cygwin's getifaddrs() has no link-layer entries: give nqptp the MAC of the adapter that
    # holds the default route (looked up every restart in case the network changed).
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric | Select-Object -First 1
    if ($route) {
        $env:NQPTP_MAC = ((Get-NetAdapter -InterfaceIndex $route.InterfaceIndex).MacAddress) -replace '-', ':'
    }

    $nqptp = Start-Process -FilePath (Join-Path $bin 'nqptp.exe') -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'nqptp.out.log') -RedirectStandardError (Join-Path $logs 'nqptp.log')
    Start-Sleep -Seconds 1

    # cmd.exe for the pipe: PowerShell 5.1 pipes between native programs re-encode bytes as text.
    $sps = '"' + (Join-Path $bin 'shairport-sync.exe') + '"'
    $play = '"' + (Join-Path $bin 'pcm-play.exe') + '"'
    $confArg = '"' + ($conf -replace '\\', '/') + '"'
    $pipeline = "$sps -c $confArg 2>`"$logs\shairport-sync.log`" | $play 2>`"$logs\pcm-play.log`""
    Start-Process -FilePath 'cmd.exe' -ArgumentList "/d /c `"$pipeline`"" -WindowStyle Hidden -Wait

    Add-Content (Join-Path $logs 'restarts.log') "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') pipeline exited, restarting"
    if (-not $nqptp.HasExited) { Stop-Process -Id $nqptp.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
}
