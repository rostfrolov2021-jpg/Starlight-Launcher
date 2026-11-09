#!/usr/bin/env bash
# Builds the Starlight Android APK (arm64).
#   ./scripts/build-android.sh            -> debug-signed APK, installable for testing
#   KEYSTORE=release.jks KEYSTORE_PASS=... KEY_ALIAS=starlight KEY_PASS=... ./scripts/build-android.sh   -> release-signed APK
# Requirements: .NET 8 SDK, JDK 17, Android SDK (set ANDROID_HOME or let the workload download it with -p:AndroidSdkDirectory).
set -euo pipefail
cd "$(dirname "$0")/.."

command -v dotnet >/dev/null || { echo "Нужен .NET 8 SDK: https://dot.net"; exit 1; }
dotnet workload list | grep -q '^android' || dotnet workload install android

PROJECT=src/Starlight.Android/Starlight.Android.csproj
OUT=artifacts/android
mkdir -p "$OUT"

if ! ls src/Starlight.Android/Assets/jre/*.zip >/dev/null 2>&1; then
  echo "! В Assets/jre нет JRE-архивов: приложение соберётся, но игра не запустится (см. docs/MOBILE_PORT.md)."
fi
if ! ls src/Starlight.Android/Native/arm64-v8a/*.so >/dev/null 2>&1; then
  echo "! Нет libstarlight_jvm.so: запуск игры будет недоступен, остальное работает."
fi

ARGS=(-c Release -f net8.0-android -p:AndroidPackageFormat=apk -o "$OUT")
if [[ -n "${KEYSTORE:-}" ]]; then
  ARGS+=(-p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$KEYSTORE" -p:AndroidSigningStorePass="env:KEYSTORE_PASS"
         -p:AndroidSigningKeyAlias="${KEY_ALIAS:?KEY_ALIAS}" -p:AndroidSigningKeyPass="env:KEY_PASS")
  export KEYSTORE_PASS KEY_PASS
fi

dotnet publish "$PROJECT" "${ARGS[@]}"
echo "Готово:"; ls -lh "$OUT"/*.apk
