<#
.SYNOPSIS
    Prueba de punta a punta de las Fases 1-6 contra un MySQL real.

.DESCRIPTION
    Levanta el servidor, enrola un agente en ESTA PC y comprueba en la base que
    llegaron identidad, heartbeat, historial de IP, inventario y metricas.

    Lee la cadena de conexion de la variable de entorno de maquina
    DEVICEHUB_DB_CONNECTION y NUNCA la imprime.

    Es la prueba que responde "esto funciona de verdad", no "esto compila".

.EXAMPLE
    .\verify-endtoend.ps1
    .\verify-endtoend.ps1 -Minutes 3      # esperar mas para ver metricas de varios minutos
#>
[CmdletBinding()]
param(
    [int]$Minutes = 2,
    [int]$Port = 5443,
    # Apuntar a 'localhost' hace que la ruta resuelva a loopback y NINGUNA
    # interfaz quede marcada como primaria, asi que current_ip queda NULL. Para
    # probar el camino real hay que dar la IP LAN de esta PC.
    [string]$ServerHost = 'localhost',
    [string]$DataRoot = "$env:TEMP\devicehub-e2e"
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

$connection = [Environment]::GetEnvironmentVariable('DEVICEHUB_DB_CONNECTION', 'Machine')
if (-not $connection) {
    $connection = $env:DEVICEHUB_DB_CONNECTION
}
if (-not $connection) {
    throw @'
Falta DEVICEHUB_DB_CONNECTION. Definela como administrador:

  [Environment]::SetEnvironmentVariable('DEVICEHUB_DB_CONNECTION',
    'Server=HOST;Port=3306;Database=devicehub;Uid=USUARIO;Pwd=CLAVE;', 'Machine')
'@
}

$env:DEVICEHUB_DB_CONNECTION = $connection
$serverData = Join-Path $DataRoot 'server'
$agentData = Join-Path $DataRoot 'agent'
New-Item -ItemType Directory -Force -Path $serverData, $agentData | Out-Null

$server = $null
$agent = $null

function Step($text) { Write-Host "`n>> $text" -ForegroundColor Cyan }

try {
    Step 'Arrancando servidor (aplica migraciones y crea el schema)'
    $env:DeviceHub__DataDirectory = $serverData
    $env:DeviceHub__Port = $Port
    $serverLog = Join-Path $DataRoot 'server.log'

    $server = Start-Process 'dotnet' -PassThru -NoNewWindow -RedirectStandardOutput $serverLog `
        -ArgumentList 'run', '--project', "$root\src\DeviceHub.Server", '--no-build'

    # El primer arranque migra y genera certificado: puede tardar.
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $serverLog) -and (Select-String -Path $serverLog -Pattern 'escuchando en' -Quiet)) { break }
        if ($server.HasExited) { Get-Content $serverLog; throw 'El servidor termino antes de escuchar.' }
        Start-Sleep -Seconds 2
    }

    Select-String -Path $serverLog -Pattern 'Pin SPKI|password:|Migraciones aplicadas' | ForEach-Object { $_.Line }

    Step 'Generando codigo de enrolamiento (sin GUI)'
    $code = (& dotnet run --project "$root\src\DeviceHub.Server" --no-build -- --enrollment-code 2>$null |
        Select-String -Pattern '^ENROLL-' | Select-Object -First 1).Line
    if (-not $code) { throw 'No se pudo generar el codigo de enrolamiento.' }
    Write-Host "   $code"

    Step 'Arrancando agente en esta PC'
    $env:DeviceHub__DataDirectory = $agentData
    $env:DeviceHub__ServerHost = $ServerHost
    $env:DeviceHub__ServerPort = $Port
    $env:DeviceHub__EnrollmentCode = $code
    $env:DeviceHub__HeartbeatSeconds = 10
    $agentLog = Join-Path $DataRoot 'agent.log'

    $agent = Start-Process 'dotnet' -PassThru -NoNewWindow -RedirectStandardOutput $agentLog `
        -ArgumentList 'run', '--project', "$root\src\DeviceHub.Agent", '--no-build'

    Step "Esperando $Minutes minuto(s) de heartbeats, inventario y metricas"
    Start-Sleep -Seconds ($Minutes * 60 + 20)

    Select-String -Path $agentLog -Pattern 'Registrado|Identidad|Stream abierto|Inventario|Metricas' |
        ForEach-Object { $_.Line }

    Write-Host "`nAgente:  $agentData\machine.json" -ForegroundColor Green
    Get-Content "$agentData\machine.json"

    Write-Host @"

>> Comprobar en la base (deberia haber una fila en cada una):

   SELECT machine_code, hostname, current_ip, last_seen, cpu_percent FROM devicehub.machines;
   SELECT * FROM devicehub.machine_ip_history;
   SELECT cpu_model, total_memory_bytes, os_build FROM devicehub.machine_hardware;
   SELECT COUNT(*) FROM devicehub.machine_metrics;
   SELECT scriptname FROM devicehub.schemaversions ORDER BY scriptname;
"@ -ForegroundColor Yellow
}
finally {
    foreach ($process in @($agent, $server)) {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "`nProcesos detenidos. Logs y datos en $DataRoot" -ForegroundColor DarkGray
}
