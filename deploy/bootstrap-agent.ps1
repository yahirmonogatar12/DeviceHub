<#
.SYNOPSIS
    Lleva un agente viejo a la version actual, bajandola del servidor por HTTPS.

.DESCRIPTION
    EL SALTO QUE HAY QUE DAR UNA VEZ POR PC.

    Los agentes anteriores a 1.77 solo saben buscar actualizaciones en el recurso
    SMB, que entre subredes no llega. Los de 1.77 en adelante se las piden al
    servidor por el mismo 5443 que ya usan para todo, asi que en cuanto una PC
    llega a 1.77 se actualiza sola para siempre.

    Este script da ese salto. Se ejecuta EN LA PC, como SYSTEM, desde la terminal
    del dashboard -- que es la unica via que llega a las PCs que no se pueden
    actualizar, porque va por el mismo canal gRPC que si funciona.

    No hace falta en las PCs que ya estan en 1.77 o mas.

.NOTES
    Sin verificacion de firma, igual que el actualizador del agente: con
    UpdatePublisherThumbprint vacio la unica proteccion es quien puede escribir
    en la carpeta de despliegue. El hash del manifiesto protege contra un
    paquete corrupto, no contra uno cambiado a proposito.
#>
[CmdletBinding()]
param(
    [string]$Servidor = '192.168.1.10',
    [int]$Puerto = 5443,
    [string]$Anillo = 'production',
    [string]$ServiceName = 'DeviceHubAgent'
)

$ErrorActionPreference = 'Stop'

# La pila TLS de .NET Framework necesita que se le diga. Y el certificado del
# servidor es propio: se acepta a ciegas y se restaura al salir, porque este
# script corre como SYSTEM y dejar la validacion apagada seria dejarla apagada
# para todo lo que venga despues en la misma sesion.
$antes = [Net.ServicePointManager]::ServerCertificateValidationCallback
[Net.ServicePointManager]::SecurityProtocol =
    [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11
[Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

try {
    $base = "https://${Servidor}:${Puerto}/updates/$Anillo"

    # DONDE ESTA INSTALADO lo dice el servicio, no una ruta escrita a mano: son
    # cinco PCs configuradas en momentos distintos y no tienen por que coincidir.
    $img = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").PathName.Trim('"')
    $InstallPath = Split-Path $img -Parent

    $actual = (Get-Item $img).VersionInfo.FileVersion
    Write-Host "Instalado en $InstallPath (version $actual)"

    $manifiesto = (Invoke-WebRequest "$base/update.json" -UseBasicParsing -TimeoutSec 30).Content |
        ConvertFrom-Json

    Write-Host "El servidor ofrece $($manifiesto.version)"

    if ([version]$manifiesto.version -le [version]$actual) {
        Write-Host "Ya esta al dia. No hay nada que hacer." -ForegroundColor Green
        return
    }

    $trabajo = Join-Path $env:TEMP "devicehub-bootstrap-$($manifiesto.version)"
    if (Test-Path $trabajo) { Remove-Item $trabajo -Recurse -Force }
    New-Item -ItemType Directory -Path $trabajo -Force | Out-Null

    $zip = Join-Path $trabajo $manifiesto.file
    Write-Host "Bajando $($manifiesto.file)..."
    Invoke-WebRequest "$base/$($manifiesto.file)" -OutFile $zip -UseBasicParsing -TimeoutSec 600

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $manifiesto.sha256.ToLowerInvariant()) {
        throw "Hash del paquete no coincide.`n  esperado $($manifiesto.sha256)`n  obtenido $hash"
    }
    Write-Host "Paquete verificado." -ForegroundColor Green

    $nuevo = Join-Path $trabajo 'payload'
    Expand-Archive -Path $zip -DestinationPath $nuevo -Force

    # La configuracion es de ESTA PC y el paquete no la trae, a proposito. Como
    # el intercambio reemplaza la carpeta, no rescatarla seria perderla -- y con
    # ella el servidor al que reporta.
    $config = Join-Path $InstallPath 'appsettings.json'
    $aSalvo = Join-Path $trabajo 'appsettings.rescatado.json'
    if (Test-Path $config) { Copy-Item $config $aSalvo -Force }

    Write-Host "Deteniendo $ServiceName..."
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue

    # Se espera al PROCESO, no al servicio: Stop-Service vuelve cuando el SCM da
    # el servicio por parado y el .exe puede seguir mapeado unos segundos mas.
    # RemoteHost es otro proceso y tampoco muere solo.
    $limite = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $limite) {
        if (-not (Get-Process DeviceHub.Agent -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 2
    }

    Get-Process DeviceHub.Agent, DeviceHub.RemoteHost -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    # Respaldo por COPIA y sobrescritura en sitio. Mover la carpeta falla entera
    # si un solo archivo sigue abierto, y no dice cual.
    $respaldo = $InstallPath + '.anterior'
    if (Test-Path $respaldo) { Remove-Item $respaldo -Recurse -Force }
    Copy-Item $InstallPath $respaldo -Recurse -Force

    try {
        Copy-Item (Join-Path $nuevo '*') $InstallPath -Recurse -Force
        if (Test-Path $aSalvo) { Copy-Item $aSalvo $config -Force }
    }
    catch {
        Write-Host "Fallo la copia, se restaura lo anterior: $_" -ForegroundColor Red
        Copy-Item (Join-Path $respaldo '*') $InstallPath -Recurse -Force
        Start-Service $ServiceName
        throw
    }

    Start-Service $ServiceName
    Start-Sleep -Seconds 5

    $estado = (Get-Service $ServiceName).Status
    $ahora  = (Get-Item $img).VersionInfo.FileVersion

    Write-Host ""
    Write-Host "Servicio: $estado   version: $ahora" -ForegroundColor $(
        if ($estado -eq 'Running') { 'Green' } else { 'Red' })

    if ($estado -ne 'Running') {
        Write-Host "Para volver atras:" -ForegroundColor Yellow
        Write-Host "  Copy-Item '$respaldo\*' '$InstallPath' -Recurse -Force; Start-Service $ServiceName"
    }
}
finally {
    [Net.ServicePointManager]::ServerCertificateValidationCallback = $antes
}
