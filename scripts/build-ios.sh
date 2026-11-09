#!/usr/bin/env bash
# Builds the Starlight iOS app. Must run on macOS with Xcode installed (Apple requirement).
#   ./scripts/build-ios.sh sim       -> simulator build (no signing needed)
#   ./scripts/build-ios.sh device    -> signed .ipa; needs CODESIGN_KEY ("Apple Development: ..."/"Apple Distribution: ...") and PROVISION (profile name)
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "$(uname)" == "Darwin" ]] || { echo "Сборка iOS возможна только на macOS (Xcode)."; exit 1; }
command -v dotnet >/dev/null || { echo "Нужен .NET 8 SDK"; exit 1; }
dotnet workload list | grep -q '^ios' || dotnet workload install ios

PROJECT=src/Starlight.iOS/Starlight.iOS.csproj
case "${1:-sim}" in
  sim)
    dotnet build "$PROJECT" -c Debug -f net8.0-ios -p:RuntimeIdentifier=iossimulator-arm64
    echo "Запуск: xcrun simctl boot 'iPhone 15' && xcrun simctl install booted src/Starlight.iOS/bin/Debug/net8.0-ios/iossimulator-arm64/Starlight.iOS.app"
    ;;
  device)
    : "${CODESIGN_KEY:?CODESIGN_KEY}" "${PROVISION:?PROVISION}"
    dotnet publish "$PROJECT" -c Release -f net8.0-ios -p:RuntimeIdentifier=ios-arm64 \
      -p:ArchiveOnBuild=true -p:BuildIpa=true -p:CodesignKey="$CODESIGN_KEY" -p:CodesignProvision="$PROVISION"
    find src/Starlight.iOS/bin -name '*.ipa'
    ;;
  *) echo "usage: $0 [sim|device]"; exit 2 ;;
esac
