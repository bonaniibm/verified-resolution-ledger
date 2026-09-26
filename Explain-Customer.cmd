@echo off
title Verified Resolution Ledger - explain scores (read-only)
cd /d "%~dp0"
set "VRL_CUSTOMER=%~1"
if "%VRL_CUSTOMER%"=="" set /p VRL_CUSTOMER=Contact id or customer key: 
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Explain-Customer.ps1" -Customer "%VRL_CUSTOMER%"
echo.
pause
