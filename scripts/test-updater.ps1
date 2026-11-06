$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7.' }
$workspace = (Resolve-Path '.').Path
$testRoot = Join-Path $workspace ('artifacts/updater-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $testRoot | Out-Null
$rsa = [Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem([IO.File]::ReadAllText((Join-Path $workspace '.tools/release-private.pem')))
$dotnetExe = Join-Path $workspace '.tools/dotnet/dotnet.exe'
$helper = Join-Path $workspace 'src/Starlight.Updater/bin/Release/net8.0/Starlight.Updater.dll'
try {
    foreach ($scenario in @('success', 'locked-file', 'tampered-package', 'traversal')) {
        $directory = Join-Path $testRoot $scenario
        $target = Join-Path $directory 'app'
        $packageSource = Join-Path $directory 'source'
        New-Item -ItemType Directory -Force $target,$packageSource | Out-Null
        [IO.File]::WriteAllText((Join-Path $target 'Starlight.exe'), 'old launcher')
        [IO.File]::WriteAllText((Join-Path $target 'last.dll'), 'old dependency')
        [IO.File]::WriteAllText((Join-Path $target 'user-data.txt'), 'keep this')
        $package = Join-Path $directory 'package.zip'
        $zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in @('Starlight.exe', 'last.dll')) {
                $writer = [IO.StreamWriter]::new($zip.CreateEntry($file).Open())
                try { $writer.Write('new ' + $file) } finally { $writer.Dispose() }
            }
            if ($scenario -eq 'traversal') { $writer=[IO.StreamWriter]::new($zip.CreateEntry('../escape.txt').Open()); $writer.Write('bad'); $writer.Dispose() }
        } finally { $zip.Dispose() }
        $manifest = @{ version='9.0.0'; notes='isolated integration test'; publishedAt=[DateTimeOffset]::UtcNow.ToString('O'); artifacts=@(@{ runtime='win-x64'; url='https://test.invalid/package.zip'; sha256=(Get-FileHash -LiteralPath $package).Hash; size=(Get-Item -LiteralPath $package).Length }) }
        $payload = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 8 -Compress))
        $signature = $rsa.SignData($payload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
        $envelope = Join-Path $directory 'release.json'
        [IO.File]::WriteAllText($envelope, (@{ payload=[Convert]::ToBase64String($payload); signature=[Convert]::ToBase64String($signature) } | ConvertTo-Json))
        if ($scenario -eq 'tampered-package') { [IO.File]::AppendAllText($package,'tamper') }
        $request = Join-Path $directory 'request.json'
        [IO.File]::WriteAllText($request, (@{ package=$package; envelope=$envelope; target=$target; parentPid=2147483646; runtime='win-x64'; restart=$false } | ConvertTo-Json))
        $lock = $null
        try {
            if ($scenario -eq 'locked-file') { $lock=[IO.File]::Open((Join-Path $target 'last.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None) }
            & $dotnetExe $helper $request
            $code = $LASTEXITCODE
        } finally { if ($lock) { $lock.Dispose() } }
        $expected = if ($scenario -eq 'success') { 'new Starlight.exe' } else { 'old launcher' }
        if ([IO.File]::ReadAllText((Join-Path $target 'Starlight.exe')) -ne $expected) { throw "${scenario}: launcher incorrect" }
        if ([IO.File]::ReadAllText((Join-Path $target 'user-data.txt')) -ne 'keep this') { throw "${scenario}: user data changed" }
        if (($scenario -eq 'success' -and $code -ne 0) -or ($scenario -ne 'success' -and $code -ne 1)) { throw "${scenario}: unexpected exit $code" }
        if (Test-Path (Join-Path $directory 'escape.txt')) { throw 'Traversal escaped archive' }
        Write-Output "PASS updater $scenario; user data preserved"
    }
} finally { $rsa.Dispose() }
