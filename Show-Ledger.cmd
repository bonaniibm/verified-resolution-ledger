@echo off
title Verified Resolution Ledger - ledger
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Show-Ledger.ps1" %*
echo.
pause
