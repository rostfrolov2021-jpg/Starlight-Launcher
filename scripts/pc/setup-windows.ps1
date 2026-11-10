# One-time setup of a Windows PC for building and debugging the Starlight Android app.
# Run in PowerShell:   powershell -ExecutionPolicy Bypass -File scripts\pc\setup-windows.ps1
# Installs (via winget): .NET SDK 8, Microsoft OpenJDK 17, Android platform-tools (adb); then the .NET Android workload and the Android SDK.
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path "$PSScriptRoot\..\..")

function Need-Winget {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "winget не найден. Установите 'Установщик приложений' из Microsoft Store или поставьте .NET 8 SDK, JDK 17 и Android platform-tools вручную."
    }
}
function Ensure($id, $check) {
    if (& $check) { Write-Host "OK: $id уже установлен"; return }
    Need-Winget
    Write-Host "Устанавливаю $id ..."
    winget install --id $id -e --accept-package-agreements --accept-source-agreements
}

Ensure 'Microsoft.DotNet.SDK.8'   { (dotnet --list-sdks 2>$null) -match '^8\.' }
Ensure 'Microsoft.OpenJDK.17'     { (Get-Command java -ErrorAction SilentlyContinue) -and ((java -version 2>&1 | Out-String) -match '17\.') }
Ensure 'Google.PlatformTools'     { [bool](Get-Command adb -ErrorAction SilentlyContinue) }

# winget changes PATH for new shells only: refresh it for this session.
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')

# Pin SDK 8 for this repo checkout (SDK 10 cannot build the net8.0-android projects).
$sdk8 = (dotnet --list-sdks | Where-Object { $_ -match '^8\.' } | Select-Object -Last 1) -replace ' \[.*$',''
if (-not $sdk8) { throw "Не найден .NET SDK 8 после установки. Перезапустите PowerShell и повторите." }
Write-Host "Использую .NET SDK $sdk8"

Write-Host "Ставлю workload android ..."
dotnet workload install android

$sdkDir = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
Write-Host "Ставлю Android SDK в $sdkDir ..."
$tmp = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP 'starlight-pin')
Set-Content -Path (Join-Path $tmp 'global.json') -Value ('{"sdk":{"version":"' + $sdk8 + '","rollForward":"disable"}}')
Copy-Item (Join-Path $tmp 'global.json') -Destination (Join-Path (Get-Location) 'global.json') -Force
try {
    dotnet build src\Starlight.Android\Starlight.Android.csproj -f net8.0-android -t:InstallAndroidDependencies `
        "-p:AndroidSdkDirectory=$sdkDir" -p:AcceptAndroidSDKLicenses=True
} finally { Remove-Item (Join-Path (Get-Location) 'global.json') -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "Готово. Дальше:" -ForegroundColor Green
Write-Host "  1) На телефоне: Настройки > О телефоне > 7 раз нажать 'Номер сборки', затем 'Для разработчиков' > 'Отладка по USB'."
Write-Host "  2) Подключите телефон кабелем и разрешите отладку на экране телефона."
Write-Host "  3) powershell -ExecutionPolicy Bypass -File scripts\pc\dev-android.ps1"
