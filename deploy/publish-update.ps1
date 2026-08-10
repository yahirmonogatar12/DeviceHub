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

Write-Host "Publicando agente $Version..." -ForegroundColor Cyan
dotnet publish "$root\src\DeviceHub.Agent" `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:Version=$Version --output $staging --nologo

if ($LASTEXITCODE -ne 0) { throw 'Fallo la publicacion' }

# El agente escribe su propia configuracion al instalarse; incluir la del
# desarrollo sobrescribiria el servidor y el codigo de enrolamiento de cada PC.
Remove-Item (Join-Path $staging 'appsettings.json') -ErrorAction SilentlyContinue

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
