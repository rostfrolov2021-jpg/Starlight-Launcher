param([int]$HttpPort = 5098, [int]$NatPort = 5099, [string]$Runner = 'tests/Starlight.Tests/bin/Release/net8.0/Starlight.Tests.dll')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$dotnetExe = if (Test-Path '.tools/dotnet/dotnet.exe') { (Resolve-Path '.tools/dotnet/dotnet.exe').Path } else { (Get-Command dotnet).Source }
$env:STARLIGHT_DATA = Join-Path $PWD ('artifacts/integration-server/' + [Guid]::NewGuid().ToString('N') + '/users.json')
$env:STARLIGHT_NAT_PORT = [string]$NatPort
$env:STARLIGHT_TEST_NAT_PORT = [string]$NatPort
$env:STARLIGHT_TEST_SOCIAL_STATE = Join-Path $PWD ('artifacts/integration-server/' + [Guid]::NewGuid().ToString('N') + '.txt')
$env:Logging__LogLevel__Default = 'Warning'
$serverDll = (Resolve-Path 'src/Starlight.Server/bin/Release/net8.0/Starlight.Server.dll').Path
$address = 'http://127.0.0.1:' + $HttpPort
$serverProcess = Start-Process -FilePath $dotnetExe -ArgumentList @('"' + $serverDll + '"', '--urls', $address) -WindowStyle Hidden -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($serverProcess.HasExited) { throw 'Test service exited; check that test ports are available.' }
        try { $response = Invoke-RestMethod ($address + '/health'); if ($response.status -eq 'ok') { $ready = $true; break } } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'Integration server did not start.' }
    & $dotnetExe $Runner $address
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
    Stop-Process -Id $serverProcess.Id
    $serverProcess.WaitForExit()
    $serverProcess = Start-Process -FilePath $dotnetExe -ArgumentList @('"' + $serverDll + '"', '--urls', $address) -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($serverProcess.HasExited) { throw 'Restarted test service exited.' }
        try { $response = Invoke-RestMethod ($address + '/health'); if ($response.status -eq 'ok') { $ready = $true; break } } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'Restarted service did not become ready.' }
    & $dotnetExe $Runner '--social-resume' $address $env:STARLIGHT_TEST_SOCIAL_STATE
    if ($LASTEXITCODE -ne 0) { throw 'Social persistence test failed.' }
} finally {
    if (-not $serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id }
}
