@echo off
title Verified Resolution Ledger - full deployment
cd /d "%~dp0"
rem First-time deployment of every step (sign-in, Dataverse, Azure, code, synthetic seed, web resources).
rem Pass options through, e.g.:  Deploy.cmd -Steps code,webres      Uses deploy\config.json (or %VRL_CONFIG%).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Deploy-Ledger.ps1" %*
echo.
pause
