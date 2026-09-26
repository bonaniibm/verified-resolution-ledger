@echo off
title Verified Resolution Ledger - solution contents (read-only)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Solution-Report.ps1"
echo.
pause
