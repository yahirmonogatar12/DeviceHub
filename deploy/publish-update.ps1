<#
.SYNOPSIS
    Empaqueta el agente y lo publica en el recurso de actualizaciones (Fase 16).

.EXAMPLE
    .\publish-update.ps1 -Version 1.1.0
    .\publish-update.ps1 -Version 1.1.0 -Ring canary

.NOTES
    ANILLOS DE DESPLIEGUE. Publicar directo a `production` empuja la version a
    todas las PCs a la vez, y un agente roto deja la planta ciega. Cada anillo es
    una carpeta con su propio update.json: se publica en canary, se deja un dia,
    y solo entonces se copia a production.

    LA FIRMA IMPORTA. El SHA256 vive en el mismo recurso que el paquete, asi que
    solo protege contra corrupcion: quien pueda escribir ahi cambia los dos. El
    control real es firmar DeviceHub.Agent.exe y configurar su thumbprint en el
    agente (UpdatePublisherThumbprint).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('canary', 'pilot', 'production')][string]$Ring = 'canary',
    [string]$Share = '\\192.168.1.10\updates\Shared\DeviceHub',
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "La version debe ser X.Y.Z (recibido: $Version)"
}

$target = Join-Path $Share $Ring
if (-not (Test-Path $target)) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
}

$staging = Join-Path $env:TEMP "devicehub-pack-$Version"
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

# LOS DOS, EN LA MISMA CARPETA. RemoteHost es quien captura la pantalla y
# codifica: sin el aqui, el zip actualizaba el agente y dejaba el motor remoto
# en la version que hubiera instalado el instalador. Un arreglo de captura o de
# codificacion no llegaba NUNCA por esta via, y el sintoma era que la PC decia
# tener la version nueva y se comportaba como la vieja.
#
# Mismo RID, mismo framework y mismo self-contained para los dos, que es la
# invariante de deploy\publish.ps1: comparten los archivos del runtime, y
# publicar uno con otra configuracion deja una carpeta con dos apps y un runtime
# que solo le sirve a una.
Write-Host "Publicando agente $Version..." -ForegroundColor Cyan

foreach ($proyecto in @('DeviceHub.Agent', 'DeviceHub.RemoteHost')) {
    dotnet publish "$root\src\$proyecto" `
        --configuration Release --runtime win-x64 --self-contained true `
        -p:Version=$Version --output $staging --nologo

    if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion de $proyecto" }
}

foreach ($exe in @('DeviceHub.Agent.exe', 'DeviceHub.RemoteHost.exe')) {
    if (-not (Test-Path (Join-Path $staging $exe))) {
        throw "$exe no quedo en el paquete"
    }
}

# El agente escribe su propia configuracion al instalarse; incluir la del
# desarrollo sobrescribiria el servidor y el codigo de enrolamiento de cada PC.
Remove-Item (Join-Path $staging 'appsettings.json') -ErrorAction SilentlyContinue

# EL DRIVER DE PANTALLA VIRTUAL VIAJA CON EL PAQUETE.
#
# Es de un tercero (Amyuni usbmmidd_v2, gratuito y firmado por WHQL), asi que no
# lo compila nadie: se deja en vendor y se copia tal cual. Yendo dentro del zip,
# que la actualizacion reemplace la carpeta de instalacion deja de importar --
# la version nueva lo trae otra vez.
$driver = Join-Path $root 'vendor\usbmmidd_v2'

if (Test-Path (Join-Path $driver 'deviceinstaller64.exe')) {
    Copy-Item $driver (Join-Path $staging 'usbmmidd_v2') -Recurse -Force
    Write-Host "Driver de pantalla virtual incluido." -ForegroundColor DarkGray
}
else {
    Write-Host ""
    Write-Host "AVISO: falta el driver de pantalla virtual en vendor\usbmmidd_v2." -ForegroundColor Yellow
    Write-Host "       El paquete sale igual, pero 'Anadir pantalla virtual' contestara"
    Write-Host "       que no hay driver hasta que se copie ahi y se vuelva a publicar."
    Write-Host ""
}

$package = "DeviceHub.Agent-$Version.zip"
$zip = Join-Path $env:TEMP $package
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$staging\*" -DestinationPath $zip

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()

$firma = Get-AuthenticodeSignature (Join-Path $staging 'DeviceHub.Agent.exe')
if ($firma.Status -ne 'Valid') {
    Write-Host ""
    Write-Host "AVISO: DeviceHub.Agent.exe NO esta firmado ($($firma.Status))." -ForegroundColor Yellow
    Write-Host "       Sin firma, la unica proteccion de la flota es la ACL de $Share." -ForegroundColor Yellow
    Write-Host "       Quien pueda escribir ahi ejecuta codigo como SYSTEM en cada PC." -ForegroundColor Yellow
    Write-Host ""
}
else {
    Write-Host "Firmado por: $($firma.SignerCertificate.Subject)" -ForegroundColor Green
    Write-Host "Thumbprint : $($firma.SignerCertificate.Thumbprint)" -ForegroundColor Green
    Write-Host "  -> este es el valor de UpdatePublisherThumbprint en el agente"
}

Copy-Item $zip (Join-Path $target $package) -Force

# El manifiesto se escribe DESPUES del paquete: si se hiciera al reves, un agente
# comprobando en ese instante encontraria una version anunciada que aun no existe.
@{
    version = $Version
    file    = $package
    sha256  = $hash
    notes   = $Notes
} | ConvertTo-Json | Set-Content (Join-Path $target 'update.json') -Encoding UTF8

Write-Host ""
Write-Host "Publicado en $target" -ForegroundColor Green
Write-Host "  $package"
Write-Host "  sha256 $hash"
Write-Host ""

if ($Ring -ne 'production') {
    Write-Host "Anillo '$Ring'. Cuando lleve un dia estable, promociona:" -ForegroundColor Cyan
    Write-Host "  .\publish-update.ps1 -Version $Version -Ring production"
}
else {
    Write-Host "PRODUCTION: esto llega a todas las PCs en las proximas horas." -ForegroundColor Yellow
}
