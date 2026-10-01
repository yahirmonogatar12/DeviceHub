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

    DOS FORMAS DE LLEGAR AQUI, el mismo script:

      - A mano, desde el recurso: lee dashboard\update.json, que es lo que
        publica deploy\publish-dashboard.ps1. Ya no hay que editar el script en
        cada version.
      - Desde el boton "Actualizar" del propio dashboard: le pasa el zip que ya
        bajo y comprobo, -Esperar con su PID y -Desatendido. Sin preguntas, y al
        terminar vuelve a abrir el dashboard.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File "\\192.168.1.10\updates\Shared\DeviceHub\update-dashboard.ps1"
#>
[CmdletBinding()]
param(
    # Vacios = los del manifiesto que esta junto al script.
    [string]$Zip = '',
    [string]$Sha256 = '',
    [string]$InstallPath = 'C:\Program Files\ILSAN\DeviceHub\Dashboard',

    # El dashboard que lanzo esto: no se toca nada hasta que se cierre.
    [int]$Esperar = 0,

    # Lo lanza el dashboard: sin preguntas, y al final se vuelve a abrir.
    [switch]$Desatendido
)

$ErrorActionPreference = 'Stop'

# Desatendido corre con la ventana oculta: lo que pase tiene que quedar escrito
# en algun sitio, o un fallo seria un dashboard que no vuelve a abrirse sin mas.
if ($Desatendido) {
    Start-Transcript -Path (Join-Path $env:TEMP 'devicehub-dashboard-actualizacion.log') -Force | Out-Null
}

try {
    $yo = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $yo.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Ejecuta este script como Administrador.'
    }

    if (-not $Zip) {
        $manifiesto = Join-Path $PSScriptRoot 'dashboard\update.json'

        if (-not (Test-Path $manifiesto)) {
            throw "No se paso -Zip y no existe $manifiesto (lo crea deploy\publish-dashboard.ps1)."
        }

        $m = Get-Content $manifiesto -Raw | ConvertFrom-Json
        $Zip = Join-Path (Split-Path $manifiesto) $m.file
        $Sha256 = $m.sha256
        Write-Host "Version publicada: $($m.version)" -ForegroundColor Cyan
    }

    if (-not (Test-Path $Zip)) { throw "No existe el paquete: $Zip" }

    if (-not (Test-Path $InstallPath)) {
        throw "No existe $InstallPath. Si el dashboard esta en otra carpeta, pasala con -InstallPath."
    }

    # Se comprueba OTRA VEZ aunque el dashboard ya lo hiciera: esto corre como
    # administrador, y entre su comprobacion y este momento el archivo estuvo en
    # una carpeta del usuario.
    $hash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $Sha256.ToLowerInvariant()) {
        throw "El hash del paquete no coincide.`n  esperado $Sha256`n  obtenido $hash"
    }
    Write-Host "Paquete verificado." -ForegroundColor Green

    if ($Esperar -gt 0) {
        Wait-Process -Id $Esperar -Timeout 60 -ErrorAction SilentlyContinue
    }

    # NADIE LAS PUEDE TENER ABIERTAS. Un .exe en ejecucion no se puede sobrescribir,
    # y el visor puede estar corriendo aunque el dashboard este cerrado: se lanza
    # como proceso aparte y sobrevive a quien lo abrio.
    $abiertas = @(Get-Process DeviceHub.Dashboard, DeviceHub.RemoteViewer -ErrorAction SilentlyContinue)

    if ($abiertas.Count -gt 0) {
        # Desatendido: el tecnico ya lo acepto en el dashboard, que le aviso de
        # que se cerrarian las sesiones abiertas.
        if (-not $Desatendido) {
            Write-Host "Estan abiertas:" -ForegroundColor Yellow
            $abiertas | ForEach-Object { Write-Host "  PID $($_.Id)  $($_.ProcessName)" }
            Write-Host "Cierralas (o Ctrl+C aqui y cierra las sesiones remotas con calma)." -ForegroundColor Yellow

            $r = Read-Host 'Escribe SI para cerrarlas ahora'
            if ($r -ne 'SI') { throw 'Cancelado.' }
        }

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
            Write-Host ("  {0,-30} {1}" -f $exe, (Get-Item $ruta).VersionInfo.FileVersion)
        } else {
            Write-Host "  FALTA $exe" -ForegroundColor Red
        }
    }

    Write-Host ""
    Write-Host "Si algo va mal, lo de antes sigue en:" -ForegroundColor Yellow
    Write-Host "  Copy-Item '$respaldo\*' '$InstallPath' -Recurse -Force"
}
finally {
    if ($Desatendido) {
        # SE VUELVE A ABRIR, haya ido bien o mal: si fallo, lo de antes sigue
        # ahi (o se restauro), y un tecnico sin dashboard no puede ni ver el
        # error.
        #
        # Por explorer.exe y no directamente: este script corre ELEVADO, y lo que
        # lance saldria como administrador. Con el dashboard elevado, UIPI tira
        # en silencio el arrastrar y soltar desde el Explorador del usuario.
        $exe = Join-Path $InstallPath 'DeviceHub.Dashboard.exe'
        if (Test-Path $exe) { Start-Process explorer.exe "`"$exe`"" }

        Stop-Transcript | Out-Null
    }
}
