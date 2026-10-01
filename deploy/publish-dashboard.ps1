<#
.SYNOPSIS
    Publica una version del dashboard: los dashboards abiertos la ven y se
    actualizan solos con el boton "Actualizar".

.DESCRIPTION
    Lo mismo que publish-update.ps1 hace con el agente: el paquete y un
    manifiesto en una carpeta del recurso de actualizaciones, que el servidor
    sirve por el 5443 (/updates/dashboard/...). El dashboard lo lee con el mismo
    pin que usa para todo lo demas.

    En dashboard\ quedan:
      DeviceHub.Dashboard-X.Y.Z.zip   dashboard + visor, sin appsettings.json
      update-dashboard.ps1            quien instala; el dashboard lo baja y lo
                                      lanza elevado
      update.json                     version, paquete y los dos sha256

    Y de paso el instalador para PCs nuevas, DeviceHubDashboard-setup-X.Y.Z.exe,
    en la raiz del recurso: asi nunca se queda atras de lo publicado.

.EXAMPLE
    .\publish-dashboard.ps1 -Version 1.142.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Share = '\\192.168.1.10\updates\Shared\DeviceHub',

    # El pin del certificado de ILSANSERVER, para el instalador. No es secreto:
    # es el hash de una clave publica.
    [string]$Pin = 'oFhZInug+JnRvPJz7HLyOnQGmzyLunaoIHBHwqw5+uo='
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "La version debe ser X.Y.Z (recibido: $Version)"
}

# LOS DOS, EN LA MISMA CARPETA y con los mismos parametros: comparten los
# archivos del runtime (ver update-dashboard.ps1).
$staging = Join-Path $env:TEMP "devicehub-dashboard-pack-$Version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

foreach ($proyecto in @('DeviceHub.Dashboard', 'DeviceHub.RemoteViewer')) {
    Write-Host "Publicando $proyecto $Version..." -ForegroundColor Cyan

    # El .csproj explicito: un build de WPF interrumpido deja un *_wpftmp.csproj
    # al lado, y con dos proyectos en la carpeta dotnet se niega a elegir.
    dotnet publish "$root\src\$proyecto\$proyecto.csproj" `
        --configuration Release --runtime win-x64 --self-contained true `
        -p:Version=$Version --output $staging --nologo -v q

    if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion de $proyecto" }
}

# La configuracion es de cada PC -- a que servidor apunta --: el paquete no la
# trae para no pisarla.
Remove-Item (Join-Path $staging 'appsettings.json') -ErrorAction SilentlyContinue

$paquete = "DeviceHub.Dashboard-$Version.zip"
$zip = Join-Path $env:TEMP $paquete
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$staging\*" -DestinationPath $zip -CompressionLevel Optimal

$script = Join-Path $root 'deploy\update-dashboard.ps1'
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$hashScript = (Get-FileHash $script -Algorithm SHA256).Hash.ToLowerInvariant()

$destino = Join-Path $Share 'dashboard'
New-Item -ItemType Directory -Force -Path $destino | Out-Null

Copy-Item $zip (Join-Path $destino $paquete) -Force
Copy-Item $script (Join-Path $destino 'update-dashboard.ps1') -Force

# Y en la raiz, para quien lo ejecute a mano: lee el manifiesto de dashboard\.
Copy-Item $script (Join-Path $Share 'update-dashboard.ps1') -Force

# El manifiesto, DESPUES de todo lo demas: un dashboard que mirase en este
# instante encontraria una version anunciada que todavia no existe.
@{
    version      = $Version
    file         = $paquete
    sha256       = $hash
    scriptSha256 = $hashScript
} | ConvertTo-Json | Set-Content (Join-Path $destino 'update.json') -Encoding UTF8

Write-Host ""
Write-Host "Publicado en $destino" -ForegroundColor Green
Write-Host "  $paquete"
Write-Host "  sha256 $hash"

# --- El instalador para PCs nuevas, con lo mismo que se acaba de publicar ---
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host ""
    Write-Host "Sin Inno Setup no se genera el instalador (winget install JRSoftware.InnoSetup)." -ForegroundColor Yellow
    return
}

# El .iss empaqueta artifacts\dashboard con comodin: se deja exactamente lo
# publicado.
$artefactos = Join-Path $root 'artifacts\dashboard'
if (Test-Path $artefactos) { Remove-Item $artefactos -Recurse -Force }
Copy-Item $staging $artefactos -Recurse

$salida = Join-Path $env:LOCALAPPDATA 'ILSAN\DeviceHub-installers'
New-Item -ItemType Directory -Force -Path $salida | Out-Null

& $iscc "/DAppVersion=$Version" "/DDefaultPin=$Pin" "/O$salida" (Join-Path $root 'installer\DeviceHubDashboard.iss') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fallo la compilacion del instalador' }

$instalador = "DeviceHubDashboard-setup-$Version.exe"
Copy-Item (Join-Path $salida $instalador) (Join-Path $Share $instalador) -Force

Write-Host "  $instalador (en $Share)"
Write-Host ""
Write-Host "Los dashboards lo veran al entrar: el boton dira 'Actualizar a $Version'." -ForegroundColor Cyan
