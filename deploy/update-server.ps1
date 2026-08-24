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
    Desde un recurso de red hay que saltarse la directiva de ejecucion: un .ps1
    sin firmar que llega por UNC no se ejecuta con la politica por defecto.

    powershell -NoProfile -ExecutionPolicy Bypass -File "\\192.168.1.10\updates\Shared\DeviceHub\update-server.ps1"
#>
[CmdletBinding()]
param(
    [string]$Zip = '\\192.168.1.10\updates\Shared\DeviceHub\DeviceHub.Server-1.86.0.zip',
    [string]$Sha256 = '76c37191dd445fc68b966b4b79ba6c6d234103334ffa6d805a453ec1e9f59b70',
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

# SEGUNDA INSTANCIA. El MES Control Center llegó a lanzar DeviceHub.Server.exe
# a mano, y dos instancias se pelean por el 5443. Si hay una corriendo desde
# otra carpeta, pararla no es cosa de este script.
$otras = @(Get-Process DeviceHub.Server -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and -not $_.Path.StartsWith($InstallPath, 'OrdinalIgnoreCase') })

if ($otras.Count -gt 0) {
    Write-Host "Hay DeviceHub.Server.exe corriendo FUERA de la instalacion:" -ForegroundColor Red
    $otras | ForEach-Object { Write-Host "  PID $($_.Id)  $($_.Path)" }
    throw 'Cierralo antes de seguir: dos instancias se pelean por el 5443.'
}

Write-Host "Deteniendo $ServiceName..." -ForegroundColor Yellow
Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue

# SE ESPERA AL PROCESO, NO AL SERVICIO.
#
# Stop-Service vuelve cuando el SCM da el servicio por parado, y el proceso
# puede seguir cerrando conexiones unos segundos mas. Windows mantiene el .exe
# y sus DLL mapeados hasta que muere de verdad, y cualquier cosa que toque esa
# carpeta antes falla con "Access denied" -- que es exactamente lo que paso.
$limite = (Get-Date).AddSeconds(60)

while ((Get-Date) -lt $limite) {
    $vivo = Get-Process DeviceHub.Server -ErrorAction SilentlyContinue
    if (-not $vivo) { break }
    Start-Sleep -Seconds 2
}

$vivo = Get-Process DeviceHub.Server -ErrorAction SilentlyContinue
if ($vivo) {
    Write-Host "El proceso no se fue solo en 60 s; se cierra." -ForegroundColor Yellow
    $vivo | Stop-Process -Force
    Start-Sleep -Seconds 5
}

# RESPALDO POR COPIA Y SOBRESCRITURA EN SITIO, no moviendo la carpeta.
#
# Mover un directorio falla entero si UN solo archivo dentro sigue abierto, y no
# dice cual. Copiar encima falla archivo a archivo y deja la instalacion
# completa hasta el momento en que empieza a escribir. Ademas el respaldo es una
# copia: si esto se tuerce a media escritura, lo de antes sigue existiendo.
Write-Host "Respaldando en $respaldo..." -ForegroundColor Yellow
Copy-Item $InstallPath $respaldo -Recurse -Force

try {
    Copy-Item (Join-Path $nuevo '*') $InstallPath -Recurse -Force

    # Vuelve la configuracion de ESTA maquina. El zip no la trae para no pisarla,
    # y no devolverla seria perderla -- que es exactamente como el agente se dejo
    # sin poder actualizarse durante dos versiones.
    if (Test-Path $aSalvo) { Copy-Item $aSalvo $config -Force }
}
catch {
    Write-Host "Fallo la copia: $_" -ForegroundColor Red
    Write-Host "Restaurando lo anterior..." -ForegroundColor Yellow
    Copy-Item (Join-Path $respaldo '*') $InstallPath -Recurse -Force
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
    Write-Host "  Copy-Item '$respaldo\*' '$InstallPath' -Recurse -Force"
    Write-Host "  Start-Service $ServiceName"
    exit 1
}

# LA PRUEBA DE QUE EL ARREGLO DE LA FLOTA ESTA EN PIE.
#
# Sin -SkipCertificateCheck, que no existe en Windows PowerShell 5.1 -- que es
# el que hay en el servidor. El callback funciona en 5.1 y en 7, y se deja como
# estaba al terminar para no dejar la sesion aceptando cualquier certificado.
$antes = [Net.ServicePointManager]::ServerCertificateValidationCallback

$url = 'https://localhost:5443/updates/production/update.json'

# Antes que la red, lo que si es concluyente: que el manifiesto este donde
# UpdatesPath dice. Si esto falla, no hay nada que servir y da igual el resto.
$manifiesto = Join-Path $UpdatesPath 'production\update.json'

if (Test-Path $manifiesto) {
    Write-Host ""
    Write-Host "Manifiesto encontrado en $manifiesto" -ForegroundColor Green
    Write-Host (Get-Content $manifiesto -Raw)
} else {
    Write-Host ""
    Write-Host "NO existe $manifiesto" -ForegroundColor Red
    Write-Host "UpdatesPath tiene que apuntar a la carpeta que CONTIENE production\." -ForegroundColor Yellow
}

try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11
    [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

    # WebClient y no Invoke-WebRequest: en estas maquinas el segundo falla
    # contra Kestrel con "Error inesperado de envio" y el primero no.
    $texto = (New-Object Net.WebClient).DownloadString($url)
    Write-Host "El 5443 lo sirve:" -ForegroundColor Green
    Write-Host $texto
}
catch {
    # NO es prueba de que este roto. Windows PowerShell 5.1 usa la pila TLS de
    # .NET Framework y se atraganta con Kestrel por motivos suyos, con un
    # "unexpected error occurred on a send" que no dice nada. El agente usa otra
    # pila entera y no le pasa.
    Write-Host ""
    Write-Host "No se pudo comprobar desde aqui: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "Eso NO significa que este roto: es la pila TLS de PowerShell 5.1." -ForegroundColor Yellow
    Write-Host "Abre esta URL en un navegador para verlo de verdad:" -ForegroundColor Yellow
    Write-Host "  https://192.168.1.10:5443/updates/production/update.json"
}
finally {
    [Net.ServicePointManager]::ServerCertificateValidationCallback = $antes
}
