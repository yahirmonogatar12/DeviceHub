<#
.SYNOPSIS
    Publica servidor, agente y dashboard listos para copiar a las PCs.

.DESCRIPTION
    Por defecto self-contained: las PCs de planta no necesitan tener instalado el
    runtime de .NET. Pesa mas, pero convierte el despliegue en "copiar carpeta"
    en lugar de "instalar el runtime en 80 equipos".
#>
[CmdletBinding()]
param(
    [string]$Output = "$PSScriptRoot\..\artifacts",
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [bool]$SelfContained = $true,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

$projects = @{
    'server'    = 'src\DeviceHub.Server\DeviceHub.Server.csproj'
    'agent'     = 'src\DeviceHub.Agent\DeviceHub.Agent.csproj'
    'dashboard' = 'src\DeviceHub.Dashboard\DeviceHub.Dashboard.csproj'
}

foreach ($name in $projects.Keys) {
    $target = Join-Path $Output $name
    Write-Host "Publicando $name -> $target" -ForegroundColor Cyan

    dotnet publish (Join-Path $root $projects[$name]) `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained $SelfContained `
        --output $target `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion de $name" }
}

Write-Host ""
Write-Host "Listo. Artefactos en $Output" -ForegroundColor Green
Write-Host "  server\    -> instalar con deploy\install-server.ps1"
Write-Host "  agent\     -> copiar a cada PC e instalar con deploy\install-agent.ps1"
Write-Host "  dashboard\ -> copiar a la PC del tecnico, editar appsettings.json y ejecutar"
