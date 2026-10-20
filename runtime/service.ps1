# Runs the receiver and keeps it running. Started at boot by the scheduled task that
# install.ps1 creates (as SYSTEM); can also be run by hand for testing.
#
#   nqptp.exe           PTP clock (UDP 319/320), shared with shairport-sync via shared memory
#   shairport-sync.exe  AirPlay 2 service, mDNS advertisement and WASAPI audio output
#
# If shairport-sync exits, both are restarted after 3 seconds.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'usr\bin'
$logs = Join-Path $root 'logs'
$conf = Join-Path $root 'config\shairport-sync.conf'
New-Item -ItemType Directory -Force $logs | Out-Null

if (-not (Test-Path $conf)) { throw "missing $conf -- run install.ps1 first" }

# shairport-sync saves the last AirPlay volume here and starts with it after a restart
$env:SPS_VOLUME_FILE = (Join-Path $root 'config\volume.txt') -replace '\\', '/'

function Stop-Receiver {
    Get-Process nqptp, shairport-sync -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

while ($true) {
    Stop-Receiver
    Remove-Item (Join-Path $root 'dev\shm\nqptp*') -Force -ErrorAction SilentlyContinue

    # Pin both daemons to the adapter that holds the default route (looked up on every restart in
    # case the network changed):
    #  - nqptp gets its MAC (Cygwin's getifaddrs() has no link-layer entries);
    #  - the mDNS responder gets its IPv4 address. Left to itself it guesses from the multicast
    #    route, which can pick a virtual adapter (Wi-Fi Direct, VPN, ...) and then never sees
    #    queries. With several addresses on the adapter, use the one in the gateway's subnet.
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object { $_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric } |
        Select-Object -First 1
    if ($route) {
        $env:NQPTP_MAC = ((Get-NetAdapter -InterfaceIndex $route.InterfaceIndex).MacAddress) -replace '-', ':'
        $addrs = Get-NetIPAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
        $gw = [Net.IPAddress]::Parse($route.NextHop).GetAddressBytes()
        $env:MDNSD_IPV4 = ($addrs | Where-Object {
                $ip = [Net.IPAddress]::Parse($_.IPAddress).GetAddressBytes()
                $bits = $_.PrefixLength
                $same = $true
                for ($i = 0; $i -lt 4; $i++) {
                    $maskBits = [Math]::Max(0, [Math]::Min(8, $bits - 8 * $i))
                    $mask = (0xFF -shl (8 - $maskBits)) -band 0xFF
                    if (($ip[$i] -band $mask) -ne ($gw[$i] -band $mask)) { $same = $false }
                }
                $same
            } | Select-Object -First 1).IPAddress
        if (-not $env:MDNSD_IPV4) { $env:MDNSD_IPV4 = ($addrs | Select-Object -First 1).IPAddress }
    }

    $nqptp = Start-Process -FilePath (Join-Path $bin 'nqptp.exe') -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'nqptp.out.log') -RedirectStandardError (Join-Path $logs 'nqptp.log')
    Start-Sleep -Seconds 1

    Start-Process -FilePath (Join-Path $bin 'shairport-sync.exe') -ArgumentList '-c', ('"' + ($conf -replace '\\', '/') + '"') `
        -WindowStyle Hidden -Wait `
        -RedirectStandardOutput (Join-Path $logs 'shairport-sync.out.log') -RedirectStandardError (Join-Path $logs 'shairport-sync.log')

    Add-Content (Join-Path $logs 'restarts.log') "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') shairport-sync exited, restarting"
    if (-not $nqptp.HasExited) { Stop-Process -Id $nqptp.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
}
