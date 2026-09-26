@echo off
title Verified Resolution Ledger - re-ingest live conversations
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Reingest.ps1"
echo.
pause
