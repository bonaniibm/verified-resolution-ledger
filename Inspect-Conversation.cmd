@echo off
title Verified Resolution Ledger - inspect a conversation (read-only)
cd /d "%~dp0"
set "VRL_CONV=%~1"
if "%VRL_CONV%"=="" set /p VRL_CONV=Conversation id: 
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Inspect-Conversation.ps1" -Id "%VRL_CONV%"
echo.
pause
