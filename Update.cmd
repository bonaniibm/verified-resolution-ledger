@echo off
title Verified Resolution Ledger - update code and web resources
cd /d "%~dp0"
rem Redeploys the Function App code and the agent pane / dashboard web resources. No data is seeded.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Deploy-Ledger.ps1" -Steps code,webres
echo.
pause
