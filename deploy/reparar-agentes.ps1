<#
.SYNOPSIS
    Repara agentes que quedaron apuntando a localhost y sin codigo.

.DESCRIPTION
    El instalador preconfigurado escribia "localhost" y un EnrollmentCode vacio
    cuando la pagina de configuracion se saltaba. La PC queda con el agente
    instalado y el servicio corriendo, pero no contacta con nadie y no aparece
    en el dashboard.

    Esto lo arregla SIN reinstalar y sin ir a cada PC: reescribe los tres campos
    en appsettings.json por el recurso administrativo y reinicia el servicio.

    NO SE TOCA LA IDENTIDAD. El machineId vive en
    C:\ProgramData\ILSANSYSTEM\DeviceHub y no se roza: la PC conserva su
    historial si ya estaba dada de alta alguna vez.

.EXAMPLE
    .\reparar-agentes.ps1 -Equipos PC-01,PC-02,192.168.0.55 -Code ENROLL-VSWF-NJUD -Pin "oFhZ...="

.EXAMPLE
    # Comprobar sin cambiar nada
    .\reparar-agentes.ps1 -Equipos PC-01,PC-02 -Code X -Pin Y -SoloMirar
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Equipos,
    [Parameter(Mandatory)][string]$Code,
    [string]$Pin = '',
    [string]$Server = '192.168.1.10',
    [int]$Port = 5443,
    [switch]$SoloMirar
)

$ErrorActionPreference = 'Continue'
$ruta = 'Program Files\ILSAN\DeviceHub Agent\appsettings.json'
$resumen = @()

foreach ($equipo in $Equipos) {
    $config = "\\$equipo\C`$\$ruta"
    $estado = ''

    try {
        if (-not (Test-Path -LiteralPath $config)) {
            $resumen += [pscustomobject]@{ Equipo = $equipo; Estado = 'sin agente instalado' }
            continue
        }

        # SE LEE Y SE MODIFICAN TRES CAMPOS, no se escribe el archivo de cero.
        # Asi se conservan DataDirectory, UpdateShare y lo que traiga esa PC, y
        # no hay forma de equivocarse escapando las barras de una ruta UNC.
        $json = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json

        if ($json.DeviceHub.ServerHost -eq $Server -and $json.DeviceHub.EnrollmentCode) {
            $resumen += [pscustomobject]@{ Equipo = $equipo; Estado = 'ya estaba bien' }
            continue
        }

        $estado = "{0} -> {1}" -f $json.DeviceHub.ServerHost, $Server

        if ($SoloMirar) {
            $resumen += [pscustomobject]@{ Equipo = $equipo; Estado = "PENDIENTE  $estado" }
            continue
        }

        $json.DeviceHub.ServerHost = $Server
        $json.DeviceHub.ServerPort = $Port
        $json.DeviceHub.EnrollmentCode = $Code

        # PinnedKeys es un arreglo: vacio si no hay pin, o el pin dentro.
        $json.DeviceHub.PinnedKeys = if ($Pin) { @($Pin) } else { @() }

        $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $config -Encoding UTF8

        # El servicio relee la configuracion al arrancar, no en caliente.
        & sc.exe "\\$equipo" stop DeviceHubAgent | Out-Null
        Start-Sleep -Seconds 3
        & sc.exe "\\$equipo" start DeviceHubAgent | Out-Null

        $resumen += [pscustomobject]@{ Equipo = $equipo; Estado = "REPARADO  $estado" }
    }
    catch {
        $resumen += [pscustomobject]@{ Equipo = $equipo; Estado = "ERROR: $($_.Exception.Message.Split([char]10)[0])" }
    }
}

$resumen | Format-Table -AutoSize

Write-Host ""
Write-Host "Cada PC reparada consume UN uso del codigo al registrarse." -ForegroundColor Yellow
Write-Host "Comprueba en el dashboard dentro de un minuto: el agente reintenta al arrancar." -ForegroundColor Cyan
