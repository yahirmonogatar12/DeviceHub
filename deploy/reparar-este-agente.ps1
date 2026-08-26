<#
.SYNOPSIS
    Repara EN ESTA PC el agente que quedo apuntando a localhost.

.DESCRIPTION
    El instalador preconfigurado escribio "localhost" y un EnrollmentCode vacio.
    La PC tiene el agente instalado y el servicio corriendo, pero no contacta
    con nadie y no aparece en el dashboard.

    Esto lo arregla sin reinstalar: corrige los tres campos de appsettings.json
    y reinicia el servicio. Diez segundos por PC.

    NO SE TOCA LA IDENTIDAD. El machineId vive en
    C:\ProgramData\ILSANSYSTEM\DeviceHub y no se roza, asi que la PC conserva su
    historial si ya estaba dada de alta alguna vez.

.EXAMPLE
    Clic derecho sobre el archivo -> "Ejecutar con PowerShell" (como Administrador)

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\reparar-este-agente.ps1
#>
[CmdletBinding()]
param(
    [string]$Server = '192.168.1.10',
    [int]$Port = 5443,

    # El codigo caduca. Si este ya vencio, genera otro y pasalo con -Code.
    [string]$Code = 'ENROLL-VSWF-NJUD',
    [string]$Pin = 'oFhZInug+JnRvPJz7HLyOnQGmzyLunaoIHBHwqw5+uo='
)

$ErrorActionPreference = 'Stop'

$yo = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()

if (-not $yo.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host ""
    Write-Host "Ejecuta esto como Administrador." -ForegroundColor Red
    Write-Host "Clic derecho en PowerShell -> Ejecutar como administrador, y vuelve a lanzarlo."
    Write-Host ""
    exit 1
}

$config = Join-Path $env:ProgramFiles 'ILSAN\DeviceHub Agent\appsettings.json'

if (-not (Test-Path -LiteralPath $config)) {
    throw "No hay agente instalado aqui: falta $config"
}

# SE LEE Y SE MODIFICAN TRES CAMPOS, no se escribe el archivo de cero. Asi se
# conservan DataDirectory, UpdateShare y lo que traiga esta PC, y no hay forma de
# equivocarse escapando las barras de una ruta UNC -- que dejaria la
# configuracion ilegible y la PC peor que antes.
$json = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
$antes = $json.DeviceHub.ServerHost

if ($antes -eq $Server -and $json.DeviceHub.EnrollmentCode) {
    Write-Host "Esta PC ya estaba bien configurada ($antes)." -ForegroundColor Green
    Write-Host "Si aun asi no sale en el dashboard, el problema es otro."
    exit 0
}

$json.DeviceHub.ServerHost = $Server
$json.DeviceHub.ServerPort = $Port
$json.DeviceHub.EnrollmentCode = $Code
$json.DeviceHub.PinnedKeys = if ($Pin) { @($Pin) } else { @() }

$json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $config -Encoding UTF8

Write-Host "appsettings.json corregido: $antes -> $Server" -ForegroundColor Cyan

# El servicio relee la configuracion al ARRANCAR, no en caliente.
Write-Host "Reiniciando el servicio..." -ForegroundColor DarkGray

Stop-Service DeviceHubAgent -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
Start-Service DeviceHubAgent

Start-Sleep -Seconds 5
$servicio = Get-Service DeviceHubAgent

Write-Host ""
Write-Host "Servicio: $($servicio.Status)" -ForegroundColor $(if ($servicio.Status -eq 'Running') { 'Green' } else { 'Red' })
Write-Host "Equipo:   $env:COMPUTERNAME"
Write-Host ""
Write-Host "Aparecera en el dashboard en menos de un minuto." -ForegroundColor Cyan
Write-Host "Si no aparece, mira el Visor de eventos -> Aplicacion -> DeviceHub.Agent."
