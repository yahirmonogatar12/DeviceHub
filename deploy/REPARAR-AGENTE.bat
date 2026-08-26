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
REM  El pushd es lo que hace que esto funcione DESDE LA RUTA DE RED: cmd no
REM  admite una UNC como directorio actual, y pushd le asigna una letra temporal.
REM ---------------------------------------------------------------------------

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo  Elevando a Administrador...
    echo.
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

pushd "%~dp0"

echo.
echo  ==========================================
echo   DeviceHub - reparar agente de esta PC
echo  ==========================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0reparar-este-agente.ps1"

popd

echo.
echo  Pulsa una tecla para cerrar.
pause >nul
