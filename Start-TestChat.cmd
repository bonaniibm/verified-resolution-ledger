@echo off
title VRL test portal - representative chat
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0testchat\Serve-TestChat.ps1" -Widget human
echo.
pause
