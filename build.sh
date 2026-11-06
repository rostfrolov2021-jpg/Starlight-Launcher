#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")"
runtime="${1:-linux-x64}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore Starlight.sln
dotnet build Starlight.sln -c Release --no-restore -m:1
dotnet tests/Starlight.Tests/bin/Release/net8.0/Starlight.Tests.dll
dotnet publish src/Starlight.Desktop -c Release -r "$runtime" --self-contained true -p:PublishSingleFile=false -o "artifacts/publish/$runtime" -m:1
dotnet publish src/Starlight.Server -c Release -r "$runtime" --self-contained true -o "artifacts/server/$runtime" -m:1
dotnet publish src/Starlight.Updater -c Release -r "$runtime" --self-contained true -o "artifacts/publish/$runtime/updater" -m:1
mkdir -p "artifacts/publish/$runtime/service"
cp -R "artifacts/server/$runtime/." "artifacts/publish/$runtime/service/"
