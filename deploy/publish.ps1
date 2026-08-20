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

# Varios proyectos por carpeta a proposito. RemoteHost acompana al agente y
# RemoteViewer al dashboard, y publicados EN LA MISMA carpeta comparten los
# archivos del runtime: si cada uno fuera a la suya, el instalador del agente
# pasaria de ~80 MB a ~160 MB por duplicar .NET entero.
#
# De regalo, RemoteHost entra solo en el zip de actualizacion (publish-update.ps1
# empaqueta la carpeta del agente completa) y en el instalador (el .iss copia
# artifacts\agent\* de forma recursiva).
#
# INVARIANTE: los proyectos que comparten carpeta se publican con el MISMO RID,
# el mismo TargetFramework y el mismo valor de self-contained. Este bucle lo
# garantiza por construccion, porque aplica los mismos parametros a todos.
#
# Si alguna vez se publica uno por separado con otra configuracion, el segundo
# publish sobreescribe los archivos del runtime del primero y queda una carpeta
# con dos apps y un runtime que solo le sirve a una. El sintoma no apunta a
# nada: la app falla al arrancar por un ensamblado que "esta ahi".
$projects = @{
    'server'    = @('src\DeviceHub.Server\DeviceHub.Server.csproj')
    'agent'     = @('src\DeviceHub.Agent\DeviceHub.Agent.csproj',
                    'src\DeviceHub.RemoteHost\DeviceHub.RemoteHost.csproj')
    'dashboard' = @('src\DeviceHub.Dashboard\DeviceHub.Dashboard.csproj',
                    'src\DeviceHub.RemoteViewer\DeviceHub.RemoteViewer.csproj')
}

foreach ($name in $projects.Keys) {
    $target = Join-Path $Output $name

    foreach ($project in $projects[$name]) {
        Write-Host "Publicando $project -> $target" -ForegroundColor Cyan

        dotnet publish (Join-Path $root $project) `
            --configuration $Configuration `
            --runtime $Runtime `
            --self-contained $SelfContained `
            --output $target `
            --nologo

        if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion de $project" }
    }
}

Write-Host ""
Write-Host "Listo. Artefactos en $Output" -ForegroundColor Green
Write-Host "  server\    -> instalar con deploy\install-server.ps1"
Write-Host "  agent\     -> copiar a cada PC e instalar con deploy\install-agent.ps1"
Write-Host "  dashboard\ -> copiar a la PC del tecnico, editar appsettings.json y ejecutar"
