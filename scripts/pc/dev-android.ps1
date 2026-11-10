# Build the Starlight Android app, install it on the connected phone, start it and save the log.
#   powershell -ExecutionPolicy Bypass -File scripts\pc\dev-android.ps1            # build + install + start
#   powershell -ExecutionPolicy Bypass -File scripts\pc\dev-android.ps1 -Collect   # only save the phone log to artifacts\logcat.txt
#   powershell -ExecutionPolicy Bypass -File scripts\pc\dev-android.ps1 -SkipBuild # install the last built APK
param([switch]$Collect, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path "$PSScriptRoot\..\..")
$package = 'company.starlight.launcher'
$out = 'artifacts\android'
New-Item -ItemType Directory -Force -Path artifacts | Out-Null

function Test-Phone {
    if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { throw "adb не найден. Сначала запустите scripts\pc\setup-windows.ps1" }
    $devices = @(adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\tdevice$' })
    if ($devices.Count -eq 0) { throw "Телефон не найден. Включите 'Отладку по USB', подключите кабель и подтвердите запрос на экране телефона." }
    if ($devices.Count -gt 1) { Write-Warning "Подключено несколько устройств, будет использовано первое: $($devices[0])" }
}

if ($Collect) {
    Test-Phone
    adb logcat -d -v threadtime *> artifacts\logcat.txt
    Write-Host "Лог сохранён: artifacts\logcat.txt ($((Get-Item artifacts\logcat.txt).Length / 1KB -as [int]) КБ). Пришлите его мне."
    return
}

if (-not $SkipBuild) {
    $sdk8 = (dotnet --list-sdks | Where-Object { $_ -match '^8\.' } | Select-Object -Last 1) -replace ' \[.*$',''
    if (-not $sdk8) { throw "Нужен .NET SDK 8 (scripts\pc\setup-windows.ps1)." }
    $pin = Join-Path (Get-Location) 'global.json'
    $hadPin = Test-Path $pin
    if (-not $hadPin) { Set-Content -Path $pin -Value ('{"sdk":{"version":"' + $sdk8 + '","rollForward":"disable"}}') }
    try {
        Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
        dotnet publish src\Starlight.Android\Starlight.Android.csproj -c Release -f net8.0-android -p:AndroidPackageFormat=apk -o $out
        if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась (код $LASTEXITCODE)." }
    } finally { if (-not $hadPin) { Remove-Item $pin -ErrorAction SilentlyContinue } }
}

$apk = Get-ChildItem $out -Filter *.apk -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
if (-not $apk) { throw "APK не найден в $out" }
Write-Host "APK: $($apk.FullName) ($([int]($apk.Length / 1MB)) МБ)"

Test-Phone
adb logcat -c
adb install -r $apk.FullName
if ($LASTEXITCODE -ne 0) { throw "Установка не удалась. Если приложение уже стоит с другой подписью: adb uninstall $package" }
adb shell monkey -p $package -c android.intent.category.LAUNCHER 1 | Out-Null

Write-Host ""
Write-Host "Приложение запущено. Воспроизведите проблему на телефоне, затем:" -ForegroundColor Green
Write-Host "  powershell -ExecutionPolicy Bypass -File scripts\pc\dev-android.ps1 -Collect"
Write-Host "и пришлите artifacts\logcat.txt."
