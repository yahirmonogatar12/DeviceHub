<#
.SYNOPSIS
    Instala DeviceHub Server como servicio de Windows.

.EXAMPLE
    .\install-server.ps1 -ConnectionString "Server=mysql-host;Port=3306;Database=devicehub;Uid=devicehub;Pwd=secreto;"

.NOTES
    Tras el primer arranque, mira el log del servicio: imprime el PIN SPKI y la
    password inicial de admin. Ninguno de los dos se vuelve a mostrar.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConnectionString,
    [string]$Source = "$PSScriptRoot\..\artifacts\server",
    [string]$InstallPath = 'C:\Program Files\ILSAN\DeviceHub Server',
    [int]$Port = 5443,
    [string]$ServiceName = 'DeviceHubServer'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecuta este script como Administrador.'
}

if (-not (Test-Path $Source)) { throw "No existe $Source. Corre primero deploy\publish.ps1" }

# Servicio previo: detener y borrar antes de tocar archivos en uso.
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Deteniendo servicio existente..." -ForegroundColor Yellow
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Copy-Item "$Source\*" -Destination $InstallPath -Recurse -Force

# La cadena de conexion NO va en un archivo versionado (regla 4).
[Environment]::SetEnvironmentVariable('DEVICEHUB_DB_CONNECTION', $ConnectionString, 'Machine')

$settingsPath = Join-Path $InstallPath 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$settings.DeviceHub.Port = $Port
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8

$exe = Join-Path $InstallPath 'DeviceHub.Server.exe'
sc.exe create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= 'ILSAN DeviceHub Server' | Out-Null
sc.exe description $ServiceName 'Servidor central de ILSAN DeviceHub (gRPC + MySQL).' | Out-Null

# Reinicio automatico: un servidor caido deja ciega a toda la planta.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

if (-not (Get-NetFirewallRule -DisplayName 'ILSAN DeviceHub Server' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'ILSAN DeviceHub Server' -Direction Inbound `
        -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
}

Start-Service $ServiceName

Write-Host ""
Write-Host "DeviceHub Server instalado y arrancado en el puerto $Port." -ForegroundColor Green
Write-Host ""
Write-Host "SIGUIENTE PASO - anota del log lo que solo se muestra una vez:" -ForegroundColor Yellow
Write-Host "  * el PIN SPKI (va en el instalador del agente y en el dashboard)"
Write-Host "  * la password inicial del usuario admin"
Write-Host ""
Write-Host "  Get-EventLog -LogName Application -Source $ServiceName -Newest 20"
