<#
.SYNOPSIS
    Genera un instalador del agente con TODO ya configurado dentro.

.DESCRIPTION
    Ejecutado EN EL SERVIDOR, no hay que teclear nada: lee el pin de pin.txt y
    genera el codigo de enrolamiento llamando al propio servidor.

    El instalador resultante no pregunta nada. Se copia a la PC de planta, doble
    clic, siguiente, listo. O silencioso, sin un solo parametro.

    Esto es lo que evita el error tipico de escribir el nombre del equipo o la IP
    del MySQL en la casilla del servidor: nadie las teclea.

.EXAMPLE
    # En el servidor: se resuelve solo
    .\build-agent-preconfigured.ps1 -Machines 5

.EXAMPLE
    # Desde otra PC: hay que darle los valores
    .\build-agent-preconfigured.ps1 -Server 192.168.1.20 -Pin "abc...=" -Code ENROLL-8K2F-A91X
#>
[CmdletBinding()]
param(
    # El servidor de ILSAN. Antes se adivinaba por la puerta de enlace y en una
    # maquina con varias tarjetas -- la de WSL, sin ir mas lejos -- eso elige la
    # equivocada y el instalador sale apuntando a una IP que no atiende.
    [string]$Server = '192.168.1.10',
    [string]$Pin,
    [string]$Code,
    [int]$Port = 5443,
    [int]$Machines = 5,
    [int]$ValidMinutes = 480,
    [string]$Version = '1.0.1',
    [string]$UpdateShare = '\\192.168.1.10\updates\Shared\DeviceHub\production',
    [string]$Thumbprint = '',
    [string]$Output = "$env:LOCALAPPDATA\ILSAN\DeviceHub-installers"
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$datosServidor = 'C:\ProgramData\ILSANSYSTEM\DeviceHubServer'
$exeServidor = 'C:\Program Files\ILSAN\DeviceHub\Server\DeviceHub.Server.exe'

# --- Pin: del archivo que el servidor escribe al arrancar ---
if (-not $Pin) {
    $archivoPin = Join-Path $datosServidor 'pin.txt'

    if (Test-Path $archivoPin) {
        $Pin = (Get-Content $archivoPin -Raw).Trim()
        Write-Host "Pin leido de pin.txt" -ForegroundColor DarkGray
    }
    else {
        throw @"
No se encontro $archivoPin

Ejecuta este script EN EL SERVIDOR, o pasa -Pin con el valor de ese archivo.
Sin pin los agentes confiarian en el primer certificado que vean.
"@
    }
}

# --- Codigo: se genera llamando al servidor ---
if (-not $Code) {
    if (-not (Test-Path $exeServidor)) {
        throw "No se encontro $exeServidor. Pasa -Code con un codigo generado desde el dashboard."
    }

    # LA CADENA DE CONEXION NO SE HEREDA SOLA, y no esta donde parecia.
    #
    # El sintoma es "Falta la cadena de conexion" lanzado por un servidor que,
    # como servicio, lleva meses funcionando -- que es justo lo que despista.
    #
    # Y no basta con mirar el entorno de maquina: comprobado en ILSANSERVER, ahi
    # NO esta. Vive en el bloque Environment del propio servicio, en el registro,
    # que es un entorno privado que solo ve ese servicio al arrancar. Ni una
    # consola nueva ni una tarea programada lo heredan.
    if (-not $env:DEVICEHUB_DB_CONNECTION) {
        try {
            $clave = Get-ItemProperty `
                -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\DeviceHubServer' `
                -Name Environment -ErrorAction Stop

            foreach ($linea in $clave.Environment) {
                if ($linea -like 'DEVICEHUB_DB_CONNECTION=*') {
                    $env:DEVICEHUB_DB_CONNECTION =
                        $linea.Substring('DEVICEHUB_DB_CONNECTION='.Length)

                    Write-Host "Cadena de conexion leida del servicio" -ForegroundColor DarkGray
                    break
                }
            }
        }
        catch {
            # Sin servicio aqui, o sin permiso para leer su clave. Quedan las
            # otras dos fuentes.
        }
    }

    if (-not $env:DEVICEHUB_DB_CONNECTION) {
        $env:DEVICEHUB_DB_CONNECTION =
            [Environment]::GetEnvironmentVariable('DEVICEHUB_DB_CONNECTION', 'Machine')

        if ($env:DEVICEHUB_DB_CONNECTION) {
            Write-Host "Cadena de conexion leida del entorno de la maquina" -ForegroundColor DarkGray
        }
    }

    # Y si tampoco esta ahi, la del appsettings.json del propio servidor.
    if (-not $env:DEVICEHUB_DB_CONNECTION) {
        $configServidor = Join-Path (Split-Path $exeServidor) 'appsettings.json'

        if (Test-Path $configServidor) {
            $cadena = (Get-Content $configServidor -Raw | ConvertFrom-Json).DeviceHub.ConnectionString

            if ($cadena) {
                $env:DEVICEHUB_DB_CONNECTION = $cadena
                Write-Host "Cadena de conexion leida de appsettings.json" -ForegroundColor DarkGray
            }
        }
    }

    if (-not $env:DEVICEHUB_DB_CONNECTION) {
        throw @'
No hay cadena de conexion a MySQL en este entorno.

El servicio la tiene, pero esta consola no. Abre una PowerShell NUEVA como
Administrador y vuelve a intentarlo, o genera el codigo desde el dashboard
(Maquinas -> Codigo de enrolamiento) y pasalo con -Code.
'@
    }

    Write-Host "Generando codigo para $Machines maquina(s)..." -ForegroundColor DarkGray

    $Code = (& $exeServidor --enrollment-code --uses $Machines --minutes $ValidMinutes 2>$null |
        Select-String -Pattern '^ENROLL-' | Select-Object -First 1).Line

    if (-not $Code) { throw 'No se pudo generar el codigo. Revisa que el servidor arranque bien.' }
}

$Code = $Code.Trim()

# --- Compilar ---
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw 'Falta Inno Setup 6: winget install JRSoftware.InnoSetup' }

# SE PUBLICA SI LA VERSION NO COINCIDE, no solo si falta el archivo.
#
# Antes bastaba con que el .exe existiera. Como artifacts\ es una carpeta de
# salida que nadie limpia, el instalador salia con lo ultimo que alguien hubiera
# compilado ahi -- se encontro con 1.71.0 cuando la flota iba por 1.112.0 -- y
# el numero del instalador decia otra cosa.
$exeAgente = Join-Path $root 'artifacts\agent\DeviceHub.Agent.exe'
$hay = if (Test-Path $exeAgente) { (Get-Item $exeAgente).VersionInfo.FileVersion } else { '' }

if ($hay -notlike "$Version*") {
    if ($hay) { Write-Host "artifacts\agent tiene $hay; se rehace a $Version" -ForegroundColor DarkGray }

    # LOS DOS, igual que publish-update.ps1. RemoteHost es quien captura la
    # pantalla: publicando solo el agente, un instalador nuevo salia con el
    # motor remoto que hubiera quedado ahi de otra compilacion.
    foreach ($proyecto in @('DeviceHub.Agent', 'DeviceHub.RemoteHost')) {
        Write-Host "Publicando $proyecto $Version..." -ForegroundColor Cyan

        dotnet publish (Join-Path $root "src\$proyecto") `
            --configuration Release --runtime win-x64 --self-contained true `
            -p:Version=$Version --output (Join-Path $root 'artifacts\agent') --nologo -v q

        if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion de $proyecto" }
    }
}

# El driver de pantalla virtual, para que una PC recien instalada lo tenga sin
# esperar a la primera actualizacion.
$driver = Join-Path $root 'vendor\usbmmidd_v2'

if (Test-Path (Join-Path $driver 'deviceinstaller64.exe')) {
    Copy-Item $driver (Join-Path $root 'artifacts\agent\usbmmidd_v2') -Recurse -Force
    Write-Host "Driver de pantalla virtual incluido." -ForegroundColor DarkGray
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$destino = Join-Path $Output "DeviceHubAgent-setup-$Version.exe"
Remove-Item $destino -Force -ErrorAction SilentlyContinue

& $iscc `
    "/DAppVersion=$Version" `
    "/DDefaultServer=$Server" `
    "/DDefaultPort=$Port" `
    "/DDefaultCode=$Code" `
    "/DDefaultPin=$Pin" `
    "/DDefaultUpdateShare=$UpdateShare" `
    "/DDefaultThumbprint=$Thumbprint" `
    "/O$Output" `
    (Join-Path $root 'installer\DeviceHubAgent.iss') | Out-Null

if ($LASTEXITCODE -ne 0) { throw 'Fallo la compilacion del instalador' }

$vence = (Get-Date).AddMinutes($ValidMinutes)

Write-Host ""
Write-Host "Instalador listo: $destino" -ForegroundColor Green
Write-Host ("  servidor  {0}:{1}" -f $Server, $Port)
Write-Host ("  codigo    {0}   ({1} PCs, vence {2:HH:mm})" -f $Code, $Machines, $vence)
Write-Host ("  pin       {0}" -f $Pin)
Write-Host ""
Write-Host "En cada PC de planta: copiar y doble clic. No pregunta nada." -ForegroundColor Cyan
Write-Host "  o en silencio:  .\DeviceHubAgent-setup-$Version.exe /VERYSILENT"
Write-Host ""
Write-Host "El codigo vale para $Machines instalaciones y caduca a las $($vence.ToString('HH:mm'))." -ForegroundColor Yellow
Write-Host "Pasado eso, vuelve a ejecutar este script: el instalador viejo ya no sirve." -ForegroundColor Yellow
