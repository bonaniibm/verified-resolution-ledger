@echo off
title Verified Resolution Ledger - smoke test
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Test-Ledger.ps1"
echo.
pause
