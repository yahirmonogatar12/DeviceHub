<#
.SYNOPSIS
    Instala DeviceHub Agent como servicio de Windows en una PC de planta.

.EXAMPLE
    .\install-agent.ps1 -Server devicehub-host -EnrollmentCode ENROLL-8K2F-A91X -Pin "XPg0rerx92cm..."

.NOTES
    El codigo de enrolamiento es de UN SOLO USO y vence en 30 minutos: genera uno
    por PC desde el dashboard, o uno con varios usos para una tanda.

    Sin -Pin, el agente confia en el primer certificado que ve (TOFU). Pasarlo es
    mejor: cierra esa ventana por completo.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$EnrollmentCode,
    [string]$Pin = '',
    [int]$Port = 5443,
    [string]$Source = "$PSScriptRoot\..\artifacts\agent",
    [string]$InstallPath = 'C:\Program Files\ILSAN\DeviceHub Agent',
    [string]$ServiceName = 'DeviceHubAgent'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecuta este script como Administrador.'
}

if (-not (Test-Path $Source)) { throw "No existe $Source. Corre primero deploy\publish.ps1" }

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host 'Deteniendo servicio existente...' -ForegroundColor Yellow
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Copy-Item "$Source\*" -Destination $InstallPath -Recurse -Force

$settingsPath = Join-Path $InstallPath 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$settings.DeviceHub.ServerHost = $Server
$settings.DeviceHub.ServerPort = $Port
$settings.DeviceHub.EnrollmentCode = $EnrollmentCode
$settings.DeviceHub.PinnedKeys = @($Pin | Where-Object { $_ })
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8

# LocalSystem: hace falta para leer WMI y para la ACL de ProgramData, y garantiza
# que el agente corra aunque nadie inicie sesion (regla 12).
$exe = Join-Path $InstallPath 'DeviceHub.Agent.exe'
sc.exe create $ServiceName binPath= "`"$exe`"" start= auto obj= LocalSystem DisplayName= 'ILSAN DeviceHub Agent' | Out-Null
sc.exe description $ServiceName 'Agente de inventario y monitoreo de ILSAN DeviceHub.' | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

Start-Service $ServiceName
Start-Sleep -Seconds 3

$service = Get-Service $ServiceName
Write-Host ""
Write-Host "DeviceHub Agent: $($service.Status)" -ForegroundColor Green

$machineFile = 'C:\ProgramData\ILSANSYSTEM\DeviceHub\machine.json'
if (Test-Path $machineFile) {
    $machine = Get-Content $machineFile -Raw | ConvertFrom-Json
    Write-Host "  machineId   : $($machine.machineId)"
    Write-Host "  machineCode : $($machine.machineCode)"
    Write-Host ""
    Write-Host 'Ese machineId es permanente: sobrevive a cambios de IP, de nombre y de estacion.' -ForegroundColor Cyan
}
else {
    Write-Host 'El agente aun no escribio su identidad. Revisa el log:' -ForegroundColor Yellow
    Write-Host "  Get-EventLog -LogName Application -Source $ServiceName -Newest 20"
}

if (-not $Pin) {
    Write-Host ""
    Write-Host 'AVISO: sin -Pin, esta instalacion confio en el primer certificado visto (TOFU).' -ForegroundColor Yellow
}
