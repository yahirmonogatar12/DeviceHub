<#
.SYNOPSIS
    Actualiza el dashboard (y el visor, que va con el) desde el zip.

.DESCRIPTION
    Se ejecuta EN LA PC DEL TECNICO, como Administrador.

    Dashboard y visor viven en la MISMA carpeta a proposito: comparten los
    archivos del runtime de .NET, y separarlos duplicaria unos 80 MB por PC. Por
    eso el zip trae los dos y se despliegan juntos: actualizar uno solo dejaria
    una carpeta con dos aplicaciones y un runtime que solo le sirve a una, y el
    sintoma no apunta a nada -- la otra falla al arrancar por un ensamblado que
    "esta ahi".

    Aqui no hay servicio que parar. Lo que hay que asegurar es que nadie las
    tenga abiertas, y este script lo comprueba en vez de fallar a medias.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File "\\192.168.1.10\updates\Shared\DeviceHub\update-dashboard.ps1"
#>
[CmdletBinding()]
param(
    [string]$Zip = '\\192.168.1.10\updates\Shared\DeviceHub\DeviceHub.Dashboard-1.122.0.zip',
    [string]$Sha256 = '108c70378cd08e4059a89502b9910fe34b28e2c0b67b42f9652fdde34ffdba79',
    [string]$InstallPath = 'C:\Program Files\ILSAN\DeviceHub\Dashboard'
)

$ErrorActionPreference = 'Stop'

$yo = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $yo.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecuta este script como Administrador.'
}

if (-not (Test-Path $Zip)) { throw "No existe el paquete: $Zip" }

if (-not (Test-Path $InstallPath)) {
    throw "No existe $InstallPath. Si el dashboard esta en otra carpeta, pasala con -InstallPath."
}

$hash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $Sha256.ToLowerInvariant()) {
    throw "El hash del paquete no coincide.`n  esperado $Sha256`n  obtenido $hash"
}
Write-Host "Paquete verificado." -ForegroundColor Green

# NADIE LAS PUEDE TENER ABIERTAS. Un .exe en ejecucion no se puede sobrescribir,
# y el visor puede estar corriendo aunque el dashboard este cerrado: se lanza
# como proceso aparte y sobrevive a quien lo abrio.
$abiertas = @(Get-Process DeviceHub.Dashboard, DeviceHub.RemoteViewer -ErrorAction SilentlyContinue)

if ($abiertas.Count -gt 0) {
    Write-Host "Estan abiertas:" -ForegroundColor Yellow
    $abiertas | ForEach-Object { Write-Host "  PID $($_.Id)  $($_.ProcessName)" }
    Write-Host "Cierralas (o Ctrl+C aqui y cierra las sesiones remotas con calma)." -ForegroundColor Yellow

    $r = Read-Host 'Escribe SI para cerrarlas ahora'
    if ($r -ne 'SI') { throw 'Cancelado.' }

    $abiertas | Stop-Process -Force
    Start-Sleep -Seconds 3
}

$trabajo = Join-Path $env:TEMP 'devicehub-dashboard-nuevo'
if (Test-Path $trabajo) { Remove-Item $trabajo -Recurse -Force }
Expand-Archive -Path $Zip -DestinationPath $trabajo -Force

# La configuracion es de ESTA PC -- a que servidor apunta -- y el zip no la trae
# para no pisarla. Se rescata antes de escribir nada.
$config = Join-Path $InstallPath 'appsettings.json'
$aSalvo = Join-Path $env:TEMP 'appsettings.dashboard.rescatado.json'
if (Test-Path $config) { Copy-Item $config $aSalvo -Force }

# Respaldo por COPIA y sobrescritura en sitio: mover la carpeta falla entera si
# un solo archivo sigue abierto, y no dice cual.
$respaldo = $InstallPath + '.anterior'
if (Test-Path $respaldo) { Remove-Item $respaldo -Recurse -Force }

Write-Host "Respaldando en $respaldo..." -ForegroundColor Yellow
Copy-Item $InstallPath $respaldo -Recurse -Force

try {
    Copy-Item (Join-Path $trabajo '*') $InstallPath -Recurse -Force
    if (Test-Path $aSalvo) { Copy-Item $aSalvo $config -Force }
}
catch {
    Write-Host "Fallo la copia, se restaura lo anterior: $_" -ForegroundColor Red
    Copy-Item (Join-Path $respaldo '*') $InstallPath -Recurse -Force
    throw
}

Write-Host ""
Write-Host "Listo. Los dos, que van juntos:" -ForegroundColor Green

foreach ($exe in @('DeviceHub.Dashboard.exe', 'DeviceHub.RemoteViewer.exe')) {
    $ruta = Join-Path $InstallPath $exe

    if (Test-Path $ruta) {
        Write-Host ("  {0,-30} {1}" -f $exe, (Get-Item $ruta).LastWriteTime)
    } else {
        Write-Host "  FALTA $exe" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Si algo va mal, lo de antes sigue en:" -ForegroundColor Yellow
Write-Host "  Copy-Item '$respaldo\*' '$InstallPath' -Recurse -Force"
