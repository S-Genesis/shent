<#
.SYNOPSIS
  Inicia un nodo SHENT en Windows.

.EXAMPLE
  # Primer nodo (espera conexiones):
  .\scripts\start-node.ps1

.EXAMPLE
  # Segundo nodo: reemplazá IP_PUBLICA y PEER_ID por los datos reales que muestra el primer nodo.
  .\scripts\start-node.ps1 -Bootstrap "/ip4/IP_PUBLICA/tcp/37373/p2p/PEER_ID"

.EXAMPLE
  # Nodo de arranque + relay en un servidor con IP pública:
  .\scripts\start-node.ps1 -Relay -Announce "/ip4/IP_PUBLICA_DEL_SERVIDOR/tcp/37373"
#>
param(
    [ValidateSet("mainnet", "testnet", "regtest")]
    [string]$Network = "mainnet",
    [int]$Port = 0,
    [string[]]$Bootstrap = @(),
    [string]$DataDir = "",
    [string]$Announce = "",
    [switch]$Relay,
    [switch]$NoUpnp,
    [switch]$Mine,
    [string]$Address = "",
    [int]$Threads = 0,
    [switch]$ShowId
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$nodeArgs = @()
if ($ShowId) { $nodeArgs += "id" }
$nodeArgs += @("--network", $Network)
if ($Port -gt 0) { $nodeArgs += @("--port", $Port) }
if ($DataDir) { $nodeArgs += @("--datadir", $DataDir) }
foreach ($b in $Bootstrap) { $nodeArgs += @("--bootstrap", $b) }
if ($Announce) { $nodeArgs += @("--announce", $Announce) }
if ($Relay) { $nodeArgs += "--relay" }
if ($NoUpnp) { $nodeArgs += "--no-upnp" }
if ($Mine) {
    if (-not $Address) { throw "Para minar indicá -Address con tu dirección shent1..." }
    $nodeArgs += @("--mine", "--address", $Address)
    if ($Threads -gt 0) { $nodeArgs += @("--threads", $Threads) }
}

# Ejecutable publicado (dotnet publish) o, si no existe, compilación con dotnet run.
$published = Join-Path $root "publish\shent-node\shent-node.exe"
if (Test-Path $published) {
    & $published @nodeArgs
} else {
    $dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { Join-Path $env:USERPROFILE ".dotnet\dotnet.exe" }
    & $dotnet run --project (Join-Path $root "src\Shent.Node") -c Release -- @nodeArgs
}
