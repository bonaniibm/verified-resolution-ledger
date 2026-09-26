@echo off
title VRL test portal - AI agent chat
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0testchat\Serve-TestChat.ps1" -Page test-bot-chat.html -Port 8081 -Widget bot
echo.
pause
