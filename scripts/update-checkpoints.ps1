# Actualiza los puntos de control (src\Shent.Core\Consensus\checkpoints.json) desde un nodo SHENT
# sincronizado que esté corriendo en esta PC (API local de solo lectura en 127.0.0.1).
#   .\scripts\update-checkpoints.ps1                    # mainnet, API en el puerto por defecto (37380)
#   .\scripts\update-checkpoints.ps1 -ApiPort 37391     # si el nodo usa otro puerto de API
# Nunca cambia un punto de control existente: si el nodo tiene otro bloque en esa altura, se detiene,
# porque significa que ese nodo está en otra cadena.
param(
    [string]$Network = "mainnet",
    [int]$ApiPort = 0
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$file = Join-Path $root "src\Shent.Core\Consensus\checkpoints.json"
if ($ApiPort -eq 0) { $ApiPort = if ($Network -eq "testnet") { 47380 } else { 37380 } }

$url = "http://127.0.0.1:$ApiPort/api/chain/checkpoints"
try {
    $data = Invoke-RestMethod -Uri $url -TimeoutSec 10
} catch {
    throw "No se pudo consultar $url. ¿Está abierto un nodo de $Network con la API local activada? ($($_.Exception.Message))"
}
if ($data.network -ne $Network) { throw "El nodo en el puerto $ApiPort es de la red '$($data.network)', no de '$Network'." }
if ($data.syncing) { throw "El nodo todavía se está sincronizando. Esperá a que termine y repetí." }

$json = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
$current = @{}
foreach ($net in $json.PSObject.Properties) {
    $map = @{}
    foreach ($entry in $net.Value.PSObject.Properties) { $map[[long]$entry.Name] = [string]$entry.Value }
    $current[$net.Name] = $map
}
if (-not $current.ContainsKey($Network)) { $current[$Network] = @{} }
$target = $current[$Network]

$added = 0
foreach ($cp in $data.checkpoints) {
    $height = [long]$cp.height
    $hash = ([string]$cp.hash).ToLowerInvariant()
    if ($hash -notmatch '^[0-9a-f]{64}$') { throw "Hash inválido en la altura ${height}: $hash" }
    if ($target.ContainsKey($height)) {
        if ($target[$height] -ne $hash) {
            throw "El nodo tiene el bloque $hash en la altura $height, pero el punto de control existente es $($target[$height]). " +
                  "Ese nodo está en otra cadena: no se actualiza nada."
        }
        continue
    }
    $target[$height] = $hash
    $added++
}

# JSON ordenado por red y altura, para que los cambios se revisen fácil en el control de versiones.
$lines = @("{")
$nets = @($current.Keys | Sort-Object)
for ($i = 0; $i -lt $nets.Count; $i++) {
    $net = $nets[$i]
    $heights = @($current[$net].Keys | Sort-Object)
    $comma = if ($i -lt $nets.Count - 1) { "," } else { "" }
    if ($heights.Count -eq 0) { $lines += "  `"$net`": {}$comma"; continue }
    $lines += "  `"$net`": {"
    for ($j = 0; $j -lt $heights.Count; $j++) {
        $sep = if ($j -lt $heights.Count - 1) { "," } else { "" }
        $lines += "    `"$($heights[$j])`": `"$($current[$net][$heights[$j]])`"$sep"
    }
    $lines += "  }$comma"
}
$lines += "}"
[IO.File]::WriteAllText($file, ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))

$last = if ($target.Count -gt 0) { ($target.Keys | Measure-Object -Maximum).Maximum } else { 0 }
Write-Host "Puntos de control de ${Network}: $added nuevo(s), $($target.Count) en total (último: bloque $last; altura del nodo: $($data.height))." -ForegroundColor Green
if ($data.checkpoints.Count -eq 0) {
    Write-Host "Todavía no hay bloques con más de $($data.minConfirmations) confirmaciones en múltiplos de 1000: no se agregó ninguno." -ForegroundColor Yellow
}
