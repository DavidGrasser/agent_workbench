@echo off
title Agent Workbench setup
cd /d "%~dp0"
powershell -NoProfile -NoExit -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
