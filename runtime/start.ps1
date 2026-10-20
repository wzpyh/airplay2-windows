# Starts runtime/run.sh under MSYS2. Called by the scheduled task that install.ps1 creates;
# can also be run by hand. Does nothing if the receiver is already running.
param(
    [string]$MsysRoot = 'C:\msys64'
)

if (Get-Process shairport-sync -ErrorAction SilentlyContinue) { exit 0 }

# Cygwin's getifaddrs() has no link-layer entries, so hand nqptp the MAC of the adapter
# that carries the default route.
$route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
    Sort-Object RouteMetric | Select-Object -First 1
if ($route) {
    $mac = (Get-NetAdapter -InterfaceIndex $route.InterfaceIndex).MacAddress
    $env:NQPTP_MAC = $mac -replace '-', ':'
}

$runSh = Join-Path $PSScriptRoot 'run.sh'
$msysPath = '/' + $runSh.Substring(0, 1).ToLower() + ($runSh.Substring(2) -replace '\\', '/')

$env:MSYSTEM = 'MSYS'
Start-Process -FilePath (Join-Path $MsysRoot 'usr\bin\bash.exe') `
    -ArgumentList '-lc', "`"bash '$msysPath'`"" -WindowStyle Hidden
