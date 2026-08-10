<#
.SYNOPSIS
    Publica los tres componentes y compila los instaladores de Inno Setup.

.EXAMPLE
    .\build-installers.ps1
    .\build-installers.ps1 -Version 1.1.0

.NOTES
    Salen DOS instaladores, no uno:

      DeviceHubAgent-setup-X.Y.Z.exe    ~80 MB   va a decenas de PCs
      DeviceHubServer-setup-X.Y.Z.exe  ~250 MB   servidor y dashboard

    Meterlo todo en un archivo obligaria a copiar 250 MB a cada PC de planta para
    instalar un agente de 80.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',

    # FUERA del arbol de OneDrive a proposito. Compilando dentro de la carpeta
    # sincronizada, su filtro retiene el .exe recien creado e Inno falla con "el
    # proceso no tiene acceso al archivo" -- un error que no menciona OneDrive por
    # ningun lado y manda a buscar el problema al antivirus. De paso evita subir
    # 100 MB a la nube en cada compilacion.
    [string]$Output = "$env:LOCALAPPDATA\ILSAN\DeviceHub-installers",

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "La version debe ser X.Y.Z (recibido: $Version)"
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw @'
No se encontro Inno Setup 6. Instalalo con:

  winget install JRSoftware.InnoSetup
'@
}

if (-not $SkipPublish) {
    Write-Host "Publicando componentes $Version..." -ForegroundColor Cyan

    $proyectos = @{
        'server'    = 'src\DeviceHub.Server'
        'agent'     = 'src\DeviceHub.Agent'
        'dashboard' = 'src\DeviceHub.Dashboard'
    }

    foreach ($nombre in $proyectos.Keys) {
        Write-Host "  $nombre" -ForegroundColor DarkGray

        dotnet publish (Join-Path $root $proyectos[$nombre]) `
            --configuration Release --runtime win-x64 --self-contained true `
            -p:Version=$Version --output (Join-Path $root "artifacts\$nombre") --nologo -v q

        if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion de $nombre" }
    }
}

$salida = $Output
New-Item -ItemType Directory -Force -Path $salida | Out-Null

foreach ($script in 'DeviceHubAgent', 'DeviceHubServer') {
    Write-Host "Compilando $script..." -ForegroundColor Cyan

    # Se borra el .exe anterior antes de compilar. Windows o el antivirus pueden
    # tenerlo abierto un rato despues de generarlo, y entonces ISCC falla con
    # "el archivo esta siendo utilizado por otro proceso" -- que no dice nada
    # sobre el instalador y manda a buscar el problema donde no esta.
    $previo = Join-Path $salida "$script-setup-$Version.exe"

    for ($intento = 1; $intento -le 5 -and (Test-Path $previo); $intento++) {
        try { Remove-Item $previo -Force -ErrorAction Stop }
        catch { Start-Sleep -Seconds 2 }
    }

    if (Test-Path $previo) {
        throw "No se pudo liberar $previo. Cierralo (o revisa el antivirus) y reintenta."
    }

    & $iscc "/DAppVersion=$Version" "/O$salida" (Join-Path $root "installer\$script.iss") | Out-Null

    if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion de $script" }
}

Write-Host ""
Write-Host "En $salida" -ForegroundColor Green
Get-ChildItem $salida -Filter "*$Version*.exe" |
    ForEach-Object { "  {0,-40} {1,7:N0} MB" -f $_.Name, ($_.Length / 1MB) }

Write-Host ""
Write-Host "Listo. Paso a paso: docs\instalacion.md" -ForegroundColor Green
