# Genera el release para Windows 10/11 de 64 bits: no requiere instalar .NET ni nada más.
#   .\scripts\publish-windows.ps1
#   .\scripts\publish-windows.ps1 -CheckpointApiPort 37391   # API del nodo sincronizado de donde tomar los checkpoints
# Resultado: release\SHENT-<versión>-win-x64.zip (app de escritorio + nodo de consola + LEEME.txt).
param(
    [string]$Runtime = "win-x64",
    [string]$Dotnet = $(if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "$env:USERPROFILE\.dotnet\dotnet.exe" }),
    [int]$CheckpointApiPort = 37380,
    [switch]$SkipCheckpoints
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# Puntos de control: se toman de un nodo de mainnet sincronizado en esta PC antes de compilar.
if (-not $SkipCheckpoints) {
    try {
        & (Join-Path $PSScriptRoot "update-checkpoints.ps1") -Network mainnet -ApiPort $CheckpointApiPort
    } catch {
        Write-Warning "No se actualizaron los puntos de control: $($_.Exception.Message)"
        Write-Warning "El release se arma con los que ya están en checkpoints.json."
    }
}
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
$name = "SHENT-$version-$Runtime"
$out = Join-Path $root "release\$name"
$zip = Join-Path $root "release\$name.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

# Código administrado en un solo .exe precompilado (ReadyToRun: arranque rápido); las bibliotecas nativas
# quedan al lado del .exe para no tener que extraerlas en cada inicio.
$common = @(
    "-c", "Release", "-r", $Runtime, "--self-contained", "true",
    "-p:PublishSingleFile=true", "-p:PublishReadyToRun=true",
    "-p:IncludeNativeLibrariesForSelfExtract=false", "-p:DebugType=none", "-p:GenerateDocumentationFile=false",
    "-nologo", "-v", "q"
)
& $Dotnet publish (Join-Path $root "src\Shent.Desktop") @common -o $out
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación de la app de escritorio." }
& $Dotnet publish (Join-Path $root "src\Shent.Node") @common -o (Join-Path $out "nodo-consola")
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación del nodo de consola." }

# libsodium.dll depende de VCRUNTIME140.dll (runtime de Visual C++), que no viene en todas las instalaciones
# de Windows: se distribuye junto al .exe (despliegue local admitido por Microsoft).
$system = if ([Environment]::Is64BitProcess) { "System32" } else { "Sysnative" }
$vcrt = Join-Path $env:WINDIR "$system\vcruntime140.dll"
if (Test-Path $vcrt) {
    Copy-Item $vcrt $out
    Copy-Item $vcrt (Join-Path $out "nodo-consola")
} else {
    Write-Warning "No se encontró vcruntime140.dll (x64). Instalá el Visual C++ Redistributable en esta PC y repetí la publicación."
}

Copy-Item (Join-Path $root "release-files\LEEME.txt") $out
Get-ChildItem $out -Recurse -Include *.pdb, *.xml | Remove-Item -Force

$sums = Get-ChildItem $out -Recurse -File | Sort-Object FullName | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring($out.Length + 1)
}
Set-Content (Join-Path $out "SHA256SUMS.txt") $sums -Encoding ascii

Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content "$zip.sha256" "$zipHash  $name.zip" -Encoding ascii

Write-Host ""
Write-Host "Release listo:" -ForegroundColor Green
Write-Host "  Carpeta: $out"
Write-Host ("  Zip:     $zip ({0:N1} MB)" -f ((Get-Item $zip).Length / 1MB))
Write-Host "  SHA-256: $zipHash"
