<#
.SYNOPSIS
    Desinstala el agente o el servidor de DeviceHub.

.EXAMPLE
    .\uninstall.ps1 -Component agent
    .\uninstall.ps1 -Component agent -RemoveIdentity   # borra tambien el machineId

.NOTES
    Por defecto CONSERVA la identidad de la maquina y el certificado del servidor.
    Reinstalar sobre una PC ya conocida mantiene su machineId y su historial.
    -RemoveIdentity solo tiene sentido al retirar el equipo definitivamente.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('agent', 'server')][string]$Component,
    [switch]$RemoveIdentity,
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecuta este script como Administrador.'
}

$config = @{
    agent  = @{ Service = 'DeviceHubAgent';  Path = 'C:\Program Files\ILSAN\DeviceHub Agent';  Data = 'C:\ProgramData\ILSANSYSTEM\DeviceHub' }
    server = @{ Service = 'DeviceHubServer'; Path = 'C:\Program Files\ILSAN\DeviceHub Server'; Data = 'C:\ProgramData\ILSANSYSTEM\DeviceHubServer' }
}[$Component]

if (Get-Service $config.Service -ErrorAction SilentlyContinue) {
    Stop-Service $config.Service -Force -ErrorAction SilentlyContinue
    sc.exe delete $config.Service | Out-Null
    Write-Host "Servicio $($config.Service) eliminado." -ForegroundColor Green
    Start-Sleep -Seconds 2
}
else {
    Write-Host "El servicio $($config.Service) no estaba instalado."
}

if ($RemoveFiles -and (Test-Path $config.Path)) {
    Remove-Item $config.Path -Recurse -Force
    Write-Host "Archivos de programa eliminados."
}

if ($RemoveIdentity -and (Test-Path $config.Data)) {
    Write-Host ""
    Write-Host "Esto borra la identidad permanente en $($config.Data)." -ForegroundColor Red
    if ($Component -eq 'agent') {
        Write-Host 'Al reinstalar, la PC recibira un machineId NUEVO y perdera su historial.' -ForegroundColor Red
    }
    else {
        Write-Host 'Esto borra el certificado: TODOS los agentes con pin fijado dejaran de conectar.' -ForegroundColor Red
    }

    if ((Read-Host 'Escribe BORRAR para confirmar') -ne 'BORRAR') {
        Write-Host 'Cancelado. La identidad se conserva.' -ForegroundColor Yellow
        return
    }

    Remove-Item $config.Data -Recurse -Force
    Write-Host 'Identidad eliminada.' -ForegroundColor Yellow
}

if ($Component -eq 'server') {
    [Environment]::SetEnvironmentVariable('DEVICEHUB_DB_CONNECTION', $null, 'Machine')
    Get-NetFirewallRule -DisplayName 'ILSAN DeviceHub Server' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
