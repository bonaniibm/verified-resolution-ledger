@echo off
title Verified Resolution Ledger - diagnostics
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Diagnose.ps1"
echo.
pause
