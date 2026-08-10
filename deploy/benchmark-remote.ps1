<#
.SYNOPSIS
    Muestrea el consumo de un motor de control remoto (Fase 18).

.DESCRIPTION
    Mide lo que SI se puede medir sin instrumentacion: CPU, memoria, trafico y
    estabilidad de los procesos del motor. La latencia de entrada y los FPS no se
    automatizan de forma honesta -- ver docs/benchmark.md para el metodo manual.

    Se ejecuta en LOS DOS extremos: en la PC controlada y en la del tecnico. Un
    motor que va fino en el host y funde la CPU del cliente no es "ligero".

.EXAMPLE
    .\benchmark-remote.ps1 -Engine rustdesk -Minutes 10 -Role host
    .\benchmark-remote.ps1 -Engine rustdesk -Minutes 10 -Role client
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Engine,
    [ValidateSet('host', 'client')][string]$Role = 'host',
    [int]$Minutes = 10,
    [string]$Output = "$PSScriptRoot\..\artifacts\benchmark"
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv = Join-Path $Output "$Engine-$Role-$stamp.csv"
$cores = [Environment]::ProcessorCount

Write-Host "Midiendo '$Engine' como $Role durante $Minutes min. Ctrl+C para cortar." -ForegroundColor Cyan
Write-Host "Deja la sesion remota ACTIVA y trabajando: medir una sesion parada no dice nada." -ForegroundColor Yellow

$muestras = [System.Collections.Generic.List[object]]::new()
$previo = @{}
$fin = (Get-Date).AddMinutes($Minutes)
$arranques = 0
$vistoAntes = $false

while ((Get-Date) -lt $fin) {
    $procesos = @(Get-Process -Name $Engine -ErrorAction SilentlyContinue)

    if ($procesos.Count -eq 0) {
        # Estabilidad: que el motor se caiga y vuelva es el dato mas importante
        # de una prueba de 8 h, y se perderia si solo se promediara CPU.
        if ($vistoAntes) { $arranques++; $vistoAntes = $false }
        Start-Sleep -Seconds 1
        continue
    }

    $vistoAntes = $true
    $ahora = Get-Date
    $cpu = 0.0
    $ram = 0
    $io = 0

    foreach ($p in $procesos) {
        $ram += $p.WorkingSet64
        $io += $p.ReadOperationCount + $p.WriteOperationCount

        if ($previo.ContainsKey($p.Id)) {
            $deltaCpu = $p.TotalProcessorTime.TotalSeconds - $previo[$p.Id].Cpu
            $deltaT = ($ahora - $previo[$p.Id].At).TotalSeconds

            # Normalizado por nucleos: sin esto, un proceso que satura un core en
            # una maquina de 16 marcaria 100% y pareceria peor de lo que es.
            if ($deltaT -gt 0) { $cpu += [Math]::Min(100, ($deltaCpu / ($deltaT * $cores)) * 100) }
        }

        $previo[$p.Id] = @{ Cpu = $p.TotalProcessorTime.TotalSeconds; At = $ahora }
    }

    $muestras.Add([pscustomobject]@{
            Timestamp = $ahora.ToUniversalTime().ToString('o')
            Engine    = $Engine
            Role      = $Role
            Processes = $procesos.Count
            CpuPct    = [Math]::Round($cpu, 2)
            RamMB     = [Math]::Round($ram / 1MB, 1)
            IoOps     = $io
        })

    Start-Sleep -Seconds 1
}

$muestras | Export-Csv $csv -NoTypeInformation -Encoding UTF8

# La primera muestra no tiene delta de CPU: incluirla hundiria el promedio.
$utiles = $muestras | Select-Object -Skip 1

if ($utiles.Count -eq 0) {
    Write-Host "Sin muestras: el proceso '$Engine' no estaba corriendo." -ForegroundColor Red
    return
}

$cpuStats = $utiles | Measure-Object CpuPct -Average -Maximum
$ramStats = $utiles | Measure-Object RamMB -Average -Maximum

Write-Host ""
Write-Host "=== $Engine / $Role ===" -ForegroundColor Green
Write-Host ("  CPU media   {0,6:N1} %" -f $cpuStats.Average)
Write-Host ("  CPU pico    {0,6:N1} %" -f $cpuStats.Maximum)
Write-Host ("  RAM media   {0,6:N0} MB" -f $ramStats.Average)
Write-Host ("  RAM pico    {0,6:N0} MB" -f $ramStats.Maximum)
Write-Host ("  Reinicios   {0,6}" -f $arranques)
Write-Host ("  Muestras    {0,6}" -f $utiles.Count)
Write-Host ""
Write-Host "CSV: $csv"
Write-Host "Falta medir a mano latencia y FPS: docs/benchmark.md" -ForegroundColor Yellow
