@echo off
REM Builds a self-contained GAM Workbench for 64-bit Windows.
dotnet publish "%~dp0src\GamGui\GamGui.csproj" -c Release -r win-x64 --self-contained true
if errorlevel 1 exit /b 1
echo.
echo Built: %~dp0src\GamGui\bin\Release\net8.0-windows\win-x64\publish\GamWorkbench.exe
