@echo off
title Verified Resolution Ledger - build solution package
cd /d "%~dp0"
rem Usage: Package.cmd [version]   default 1.0.0.0. Uses deploy\config.json (or %VRL_CONFIG%).
set "VRL_VERSION=%~1"
if "%VRL_VERSION%"=="" set "VRL_VERSION=1.0.0.0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Package-Solution.ps1" -Version %VRL_VERSION%
echo.
pause
