$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$workspace=(Resolve-Path '.').Path
$directory=Join-Path $workspace ('artifacts/update-feed-tests/'+[Guid]::NewGuid().ToString('N'))
$target=Join-Path $directory 'app'
$releases=Join-Path $directory 'releases'
New-Item -ItemType Directory -Force $target,$releases | Out-Null
[IO.File]::WriteAllText((Join-Path $target 'Starlight.exe'),'old launcher')
Copy-Item -LiteralPath (Join-Path $workspace 'artifacts/publish/win-x64/updater') -Destination $target -Recurse
$package=Join-Path $releases 'Starlight-9.0.0-win-x64.zip'
$zip=[IO.Compression.ZipFile]::Open($package,[IO.Compression.ZipArchiveMode]::Create)
$writer=[IO.StreamWriter]::new($zip.CreateEntry('Starlight.exe').Open())
$writer.Write('new Starlight.exe'); $writer.Dispose(); $zip.Dispose()
$manifest=@{version='9.0.0'; notes='isolated feed test'; publishedAt=[DateTimeOffset]::UtcNow.ToString('O'); artifacts=@(@{runtime='win-x64'; url='http://localhost:5098/api/updates/files/Starlight-9.0.0-win-x64.zip';sha256=(Get-FileHash -LiteralPath $package).Hash;size=(Get-Item -LiteralPath $package).Length})}
$payload=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 8 -Compress))
$rsa=[Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText((Join-Path $workspace '.tools/release-private.pem')))
    $signature=$rsa.SignData($payload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
    [IO.File]::WriteAllText((Join-Path $releases 'latest.json'),(@{payload=[Convert]::ToBase64String($payload);signature=[Convert]::ToBase64String($signature)} | ConvertTo-Json))
} finally {$rsa.Dispose()}
$env:STARLIGHT_RELEASES=$releases
$env:STARLIGHT_DATA=Join-Path $directory 'users.json'
$env:STARLIGHT_NAT_PORT='5099'
$env:ASPNETCORE_URLS='http://127.0.0.1:5098'
$env:Logging__LogLevel__Default='Warning'
$serverProcess=Start-Process -FilePath (Join-Path $workspace 'artifacts/server/win-x64/Starlight.Server.exe') -WorkingDirectory $directory -WindowStyle Hidden -PassThru
try {
    $ready=$false
    for($attempt=0;$attempt -lt 30;$attempt++) {try { $health=Invoke-RestMethod 'http://127.0.0.1:5098/health'; if($health.status -eq 'ok') {$ready=$true;break} } catch {Start-Sleep -Milliseconds 200} }
    if(-not $ready) {throw 'Feed server did not start'}
    & ./.tools/dotnet/dotnet.exe tests/Starlight.Tests/bin/Release/net8.0/Starlight.Tests.dll --update-feed 'http://localhost:5098/api/updates/latest' $directory
    if($LASTEXITCODE -ne 0) {throw 'Update feed integration failed'}
} finally {if(-not $serverProcess.HasExited){Stop-Process -Id $serverProcess.Id}}
