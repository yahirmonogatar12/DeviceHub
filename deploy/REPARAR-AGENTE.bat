@echo off
REM ---------------------------------------------------------------------------
REM  Repara el agente de DeviceHub en ESTA PC.
REM
REM  Doble clic y ya. Se eleva solo si hace falta y no se cierra al terminar.
REM
REM  Existe porque "clic derecho -> Ejecutar con PowerShell" IGNORA el
REM  -ExecutionPolicy Bypass y pregunta por la directiva de ejecucion; y como el
REM  archivo llega por una ruta de red, Windows ademas lo marca como no
REM  confiable. Las dos cosas juntas dejan al tecnico delante de un aviso en el
REM  que la respuesta por defecto es "no".
REM
REM  BUSCA EL .ps1 EN DOS SITIOS. Copiar solo el .bat es lo que cualquiera hace,
REM  y con un unico sitio el resultado era un error en coreano sobre el
REM  parametro -File. Si no esta al lado, se coge del recurso de la red.
REM ---------------------------------------------------------------------------

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo  Elevando a Administrador...
    echo.
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

REM  cmd no admite una UNC como directorio actual; pushd le da una letra.
pushd "%~dp0" 2>nul

set "SCRIPT=%~dp0reparar-este-agente.ps1"
if not exist "%SCRIPT%" set "SCRIPT=\\192.168.1.10\updates\Shared\DeviceHub\reparar-este-agente.ps1"

echo.
echo  ==========================================
echo   DeviceHub - reparar agente de esta PC
echo  ==========================================
echo.

if not exist "%SCRIPT%" (
    echo  No se encontro reparar-este-agente.ps1
    echo.
    echo  Ni al lado de este archivo, ni en:
    echo    \\192.168.1.10\updates\Shared\DeviceHub\
    echo.
    echo  Comprueba que esta PC llega al servidor, o copia LOS DOS archivos
    echo  juntos a la misma carpeta.
    goto :fin
)

echo  Usando: %SCRIPT%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"

:fin
popd 2>nul

echo.
echo  Pulsa una tecla para cerrar.
pause >nul
