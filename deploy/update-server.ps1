<#
.SYNOPSIS
    Actualiza DeviceHub Server en sitio, desde el zip, conservando su configuracion.

.DESCRIPTION
    Se ejecuta EN EL SERVIDOR, como Administrador.

    No usa install-server.ps1 a proposito: ese script BORRA el servicio y lo
    vuelve a crear, y con el se iria el bloque de entorno donde vive
    DEVICEHUB_DB_CONNECTION. El sintoma seria el de siempre --
    "Falta la cadena de conexion" -- y con el servidor ya parado.

    Aqui solo se cambian los archivos.

.EXAMPLE
    .\update-server.ps1
    .\update-server.ps1 -Zip 'D:\descargas\DeviceHub.Server-1.77.0.zip'
#>
[CmdletBinding()]
param(
    [string]$Zip = '\\192.168.1.10\updates\Shared\DeviceHub\DeviceHub.Server-1.77.0.zip',
    [string]$Sha256 = '78fce34252898f57fdf392dcf151d90dfd21e323ff50112926bb50c13ffd7871',
    [string]$InstallPath = 'C:\Program Files\ILSAN\DeviceHub\Server',
    [string]$UpdatesPath = 'C:\Users\Administrator\Documents\ILSANMES\UPDATES\Shared\DeviceHub',
    [string]$ServiceName = 'DeviceHubServer'
)

$ErrorActionPreference = 'Stop'

$yo = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $yo.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecuta este script como Administrador.'
}

if (-not (Test-Path $Zip))         { throw "No existe el paquete: $Zip" }
if (-not (Test-Path $InstallPath)) { throw "No existe la instalacion: $InstallPath" }

$hash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $Sha256.ToLowerInvariant()) {
    throw "El hash del paquete no coincide.`n  esperado $Sha256`n  obtenido $hash"
}
Write-Host "Paquete verificado." -ForegroundColor Green

# La configuracion se guarda APARTE, no se confia en que el zip no la traiga.
$config  = Join-Path $InstallPath 'appsettings.json'
$aSalvo  = Join-Path $env:TEMP    'appsettings.servidor.rescatado.json'
if (Test-Path $config) { Copy-Item $config $aSalvo -Force }

$respaldo = $InstallPath + '.anterior'
$nuevo    = Join-Path $env:TEMP 'devicehub-server-nuevo'

if (Test-Path $nuevo)    { Remove-Item $nuevo -Recurse -Force }
if (Test-Path $respaldo) { Remove-Item $respaldo -Recurse -Force }

Expand-Archive -Path $Zip -DestinationPath $nuevo -Force

Write-Host "Deteniendo $ServiceName..." -ForegroundColor Yellow
Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 5

try {
    Move-Item $InstallPath $respaldo
    Move-Item $nuevo $InstallPath

    # Vuelve la configuracion de ESTA maquina. El zip no la trae para no pisarla,
    # y como el intercambio reemplaza la carpeta entera, no devolverla seria
    # perderla -- que es exactamente como el agente se dejo sin poder
    # actualizarse durante dos versiones.
    if (Test-Path $aSalvo) { Copy-Item $aSalvo $config -Force }
}
catch {
    Write-Host "Fallo el intercambio: $_" -ForegroundColor Red
    if (-not (Test-Path $InstallPath) -and (Test-Path $respaldo)) { Move-Item $respaldo $InstallPath }
    Start-Service $ServiceName
    throw
}

# Y la ruta desde la que se sirven las actualizaciones del agente. Sin esto el
# servidor arranca igual, pero /updates/... contesta 404 y cada agente vuelve a
# depender de SMB, que es lo que no funciona entre subredes.
if (Test-Path $config) {
    $ajustes = Get-Content $config -Raw | ConvertFrom-Json

    if ($null -eq $ajustes.DeviceHub.PSObject.Properties['UpdatesPath']) {
        $ajustes.DeviceHub | Add-Member -NotePropertyName UpdatesPath -NotePropertyValue $UpdatesPath
    } else {
        $ajustes.DeviceHub.UpdatesPath = $UpdatesPath
    }

    $ajustes | ConvertTo-Json -Depth 10 | Set-Content $config -Encoding UTF8
    Write-Host "UpdatesPath = $UpdatesPath" -ForegroundColor Green
}

Start-Service $ServiceName
Start-Sleep -Seconds 5

$estado = (Get-Service $ServiceName).Status
Write-Host "Servicio: $estado" -ForegroundColor $(if ($estado -eq 'Running') { 'Green' } else { 'Red' })

if ($estado -ne 'Running') {
    Write-Host "Mira el visor de eventos. Para volver atras:" -ForegroundColor Yellow
    Write-Host "  Stop-Service $ServiceName -Force"
    Write-Host "  Remove-Item '$InstallPath' -Recurse -Force"
    Write-Host "  Move-Item '$respaldo' '$InstallPath'"
    Write-Host "  Start-Service $ServiceName"
    exit 1
}

# La prueba de que el arreglo de la flota esta en pie.
try {
    $r = Invoke-WebRequest "https://localhost:5443/updates/production/update.json" `
        -SkipCertificateCheck -TimeoutSec 10
    Write-Host ""
    Write-Host "Actualizaciones servidas por el 5443:" -ForegroundColor Green
    Write-Host $r.Content
}
catch {
    Write-Host "El endpoint de actualizaciones no contesta: $_" -ForegroundColor Red
    Write-Host "Revisa que UpdatesPath apunte a la carpeta que contiene production\update.json"
}
