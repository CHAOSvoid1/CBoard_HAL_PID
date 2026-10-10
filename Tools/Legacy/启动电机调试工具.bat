@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "%~dp0Tools\CBoardMotorWeb.exe" (
  start "" "%~dp0Tools\CBoardMotorWeb.exe"
  exit /b 0
)
if exist "%~dp0Tools\CBoardMotorTool.exe" (
  start "" "%~dp0Tools\CBoardMotorTool.exe"
  exit /b 0
)
powershell.exe -NoProfile -Sta -ExecutionPolicy Bypass -File "%~dp0Tools\CBoard_Motor_GUI.ps1"
if errorlevel 1 pause
