@echo off
cd /d "%~dp0"
dotnet run --project QuestTool.csproj
if errorlevel 1 pause
