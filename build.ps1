param([string]$Runtime = 'win-x64', [switch]$Installer)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$localDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\home'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE)" }
}
Invoke-Dotnet @('restore', 'Starlight.sln')
Invoke-Dotnet @('build', 'Starlight.sln', '-c', 'Release', '--no-restore', '-m:1')
Invoke-Dotnet @('tests/Starlight.Tests/bin/Release/net8.0/Starlight.Tests.dll')
Invoke-Dotnet @('publish', 'src/Starlight.Desktop', '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', "artifacts/publish/$Runtime", '-m:1')
Invoke-Dotnet @('publish', 'src/Starlight.Server', '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-o', "artifacts/server/$Runtime", '-m:1')
Invoke-Dotnet @('publish', 'src/Starlight.Updater', '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-o', "artifacts/publish/$Runtime/updater", '-m:1')
New-Item -ItemType Directory -Force "artifacts/publish/$Runtime/service" | Out-Null
Copy-Item -Path "artifacts/server/$Runtime/*" -Destination "artifacts/publish/$Runtime/service" -Recurse -Force
if ($Installer) {
    if ($Runtime -ne 'win-x64') { throw 'The Inno Setup installer targets win-x64.' }
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $localInno = Join-Path $PSScriptRoot '.tools\inno\ISCC.exe'
    $iscc = if (Test-Path $localInno) { $localInno } elseif ($compiler) { $compiler.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
    if (-not (Test-Path $iscc)) { throw 'Install Inno Setup 6 or put ISCC.exe on PATH.' }
    $releaseVersion = ([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
    & $iscc "/DAppVersion=$releaseVersion" 'installer.iss'
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
