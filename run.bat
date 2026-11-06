@echo off
setlocal
cd /d "%~dp0"
if exist "artifacts\publish\win-x64\Starlight.exe" (
    start "" "artifacts\publish\win-x64\Starlight.exe"
    exit /b 0
)
if exist ".tools\dotnet\dotnet.exe" (
    ".tools\dotnet\dotnet.exe" "src\Starlight.Desktop\bin\Release\net8.0\Starlight.dll"
    exit /b %ERRORLEVEL%
)
dotnet run --project src\Starlight.Desktop -c Release
