@echo off
setlocal
title Mesure consommation PC
set "pwsh=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not exist "%pwsh%" set "pwsh=pwsh.exe"
echo Mesure de 5 minutes apres 30 secondes de stabilisation.
echo Pour mesurer la decharge, utilisez le PC sur batterie.
echo.
"%pwsh%" -NoProfile -File "%~dp0Measure-PCPower.ps1" %*
set "measure_exit=%ERRORLEVEL%"
echo.
echo Code de fin : %measure_exit%
echo.
pause
exit /b %measure_exit%
