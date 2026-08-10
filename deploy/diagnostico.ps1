<#
.SYNOPSIS
    Revisa una instalacion de DeviceHub y dice que falta.

.DESCRIPTION
    Ejecutalo en la PC que da problemas -- servidor o de planta, se adapta.
    Responde de una vez lo que si no exige ir probando comandos sueltos.

.EXAMPLE
    .\diagnostico.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$problemas = [System.Collections.Generic.List[string]]::new()

function Titulo($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok($t) { Write-Host "  [ok]    $t" -ForegroundColor Green }
function Mal($t) { Write-Host "  [FALTA] $t" -ForegroundColor Red; $problemas.Add($t) }
function Info($t) { Write-Host "  $t" -ForegroundColor DarkGray }

$rutaServidor = 'C:\Program Files\ILSAN\DeviceHub\Server'
$rutaDashboard = 'C:\Program Files\ILSAN\DeviceHub\Dashboard'
$rutaAgente = 'C:\Program Files\ILSAN\DeviceHub Agent'
$datosServidor = 'C:\ProgramData\ILSANSYSTEM\DeviceHubServer'

Titulo 'Que hay instalado'

$hayServidor = Test-Path "$rutaServidor\DeviceHub.Server.exe"
$hayDashboard = Test-Path "$rutaDashboard\DeviceHub.Dashboard.exe"
$hayAgente = Test-Path "$rutaAgente\DeviceHub.Agent.exe"

if ($hayServidor) { Ok 'Servidor' } else { Info 'Servidor: no instalado' }
if ($hayDashboard) { Ok 'Dashboard' } else { Info 'Dashboard: no instalado' }
if ($hayAgente) { Ok 'Agente' } else { Info 'Agente: no instalado' }

if (-not ($hayServidor -or $hayDashboard -or $hayAgente)) {
    Mal 'No hay ningun componente de DeviceHub instalado en esta PC'
}

# --------------------------------------------------------------- SERVIDOR
if ($hayServidor) {
    Titulo 'Servidor'

    $svc = Get-Service DeviceHubServer -ErrorAction SilentlyContinue
    if (-not $svc) { Mal 'El servicio DeviceHubServer no existe' }
    elseif ($svc.Status -ne 'Running') { Mal "El servicio esta $($svc.Status), no Running" }
    else { Ok 'Servicio corriendo' }

    $env = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\DeviceHubServer' -Name Environment -ErrorAction SilentlyContinue).Environment
    if ($env -and ($env -join '') -match 'DEVICEHUB_DB_CONNECTION=.+Pwd=.+') {
        Ok 'Cadena de conexion configurada'
    }
    else {
        Mal 'Falta la cadena de conexion en el entorno del servicio (reinstala el servidor)'
    }

    if (Test-Path "$datosServidor\pin.txt") {
        $pin = (Get-Content "$datosServidor\pin.txt" -Raw).Trim()
        Ok "Pin SPKI: $pin"
    }
    else {
        Mal "No existe $datosServidor\pin.txt (el servidor nunca llego a arrancar del todo)"
    }

    $escucha = Get-NetTCPConnection -LocalPort 5443 -State Listen -ErrorAction SilentlyContinue
    if ($escucha) { Ok 'Escuchando en el puerto 5443' }
    else { Mal 'Nada escuchando en el 5443: el servicio no arranco o fallo al iniciar' }

    $fw = Get-NetFirewallRule -DisplayName 'ILSAN DeviceHub Server' -ErrorAction SilentlyContinue
    if ($fw) { Ok 'Regla de firewall presente' } else { Mal 'Falta la regla de firewall del 5443' }
}

# -------------------------------------------------------------- DASHBOARD
if ($hayDashboard) {
    Titulo 'Dashboard'

    $cfg = "$rutaDashboard\appsettings.json"
    if (-not (Test-Path $cfg)) {
        Mal 'Falta appsettings.json del dashboard'
    }
    else {
        try {
            $j = Get-Content $cfg -Raw | ConvertFrom-Json
            Ok "appsettings.json valido -> $($j.DeviceHub.ServerHost):$($j.DeviceHub.ServerPort)"

            if (-not $j.DeviceHub.ServerPin) {
                Info 'Sin pin: confiara en el primer certificado que vea (TOFU)'
            }
        }
        catch {
            Mal "appsettings.json del dashboard esta mal formado: $($_.Exception.Message)"
        }
    }
}
elseif ($hayServidor) {
    Titulo 'Dashboard'
    Mal 'El dashboard NO se instalo. Vuelve a ejecutar el instalador del servidor y elige "Servidor y dashboard"'
}

# ----------------------------------------------------------------- AGENTE
if ($hayAgente) {
    Titulo 'Agente'

    $svc = Get-Service DeviceHubAgent -ErrorAction SilentlyContinue
    if (-not $svc) { Mal 'El servicio DeviceHubAgent no existe' }
    elseif ($svc.Status -ne 'Running') { Mal "El servicio esta $($svc.Status)" }
    else { Ok 'Servicio corriendo' }

    $cfg = "$rutaAgente\appsettings.json"
    if (-not (Test-Path $cfg)) {
        Mal 'Falta appsettings.json del agente'
    }
    else {
        try {
            $j = Get-Content $cfg -Raw | ConvertFrom-Json
            $destino = "$($j.DeviceHub.ServerHost):$($j.DeviceHub.ServerPort)"
            Ok "Apunta a $destino"

            if (-not $j.DeviceHub.EnrollmentCode) {
                Info 'Sin codigo de enrolamiento (normal si ya se registro)'
            }
            elseif ($j.DeviceHub.EnrollmentCode -notmatch '^ENROLL-') {
                Mal "El codigo de enrolamiento no tiene formato valido: '$($j.DeviceHub.EnrollmentCode)' (debe ser ENROLL-XXXX-XXXX)"
            }
            else { Ok 'Codigo de enrolamiento con formato valido' }

            $prueba = Test-NetConnection -ComputerName $j.DeviceHub.ServerHost -Port $j.DeviceHub.ServerPort `
                -InformationLevel Quiet -WarningAction SilentlyContinue

            if ($prueba) { Ok "Alcanza $destino" }
            else { Mal "NO alcanza $destino (servidor apagado, firewall, o IP equivocada)" }
        }
        catch {
            Mal "appsettings.json del agente esta mal formado: $($_.Exception.Message)"
        }
    }

    $identidad = 'C:\ProgramData\ILSANSYSTEM\DeviceHub\machine.json'
    if (Test-Path $identidad) {
        $m = Get-Content $identidad -Raw | ConvertFrom-Json
        if ($m.ProtectedToken) { Ok "Registrado como $($m.MachineCode)" }
        else { Mal "Tiene identidad ($($m.MachineId)) pero NO esta registrado: el codigo de enrolamiento fallo" }
    }
    else {
        Mal 'El agente no ha creado su identidad todavia'
    }
}

# ------------------------------------------------------------ CONCLUSION
Titulo 'Resultado'

if ($problemas.Count -eq 0) {
    Write-Host "  Todo en orden." -ForegroundColor Green
}
else {
    Write-Host "  $($problemas.Count) problema(s):" -ForegroundColor Yellow
    $problemas | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "Ultimos errores en el registro de eventos:" -ForegroundColor DarkGray
Get-EventLog -LogName Application -Newest 200 -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'DeviceHub' -and $_.EntryType -in 'Error', 'Warning' } |
    Select-Object -First 5 |
    ForEach-Object {
        $m = ($_.Message -split "`n" | Select-Object -Skip 3) -join ' '
        "  {0:HH:mm:ss}  {1}" -f $_.TimeGenerated, $m.Trim().Substring(0, [Math]::Min(130, $m.Trim().Length))
    }
