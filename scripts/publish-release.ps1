param([string]$Version = '', [string]$Runtime = 'win-x64', [string]$BaseUrl = 'http://localhost:5088', [string]$Notes = 'Обновление Starlight', [string]$Output = 'artifacts/releases', [string]$GitHubRepository = '', [string]$SigningKeyPath = '.tools/release-private.pem')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run with PowerShell 7 (pwsh), which supports RSA-PSS PEM signing.' }
if (-not $Version) { $Version = ([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version }
if ($GitHubRepository -and $GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub owner/repository.' }
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Runtime -notmatch '^(win|linux|osx)-(x64|arm64)$') { throw 'Invalid version or runtime.' }
$uri = [Uri]$BaseUrl
if ($uri.Scheme -ne 'https' -and -not ($uri.Scheme -eq 'http' -and $uri.IsLoopback)) { throw 'Use HTTPS, or loopback HTTP for local testing.' }
$directory = (Resolve-Path "artifacts/publish/$Runtime").Path
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $directory 'Starlight.Infrastructure.dll')).Version.ToString(3) -ne $Version) { throw 'Release version differs from the built application. Set Directory.Build.props Version and rebuild first.' }
if (-not (Test-Path -LiteralPath $SigningKeyPath)) { throw 'Restore your original private signing key before publishing.' }
$updaterName = if ($Runtime.StartsWith('win-')) { 'Starlight.Updater.exe' } else { 'Starlight.Updater' }
if (-not (Test-Path (Join-Path $directory "updater/$updaterName"))) { throw 'Publish the updater into the application folder first.' }
New-Item -ItemType Directory -Force $Output | Out-Null
$name = "Starlight-$Version-$Runtime.zip"
$zip = Join-Path (Resolve-Path $Output).Path $name
if (Test-Path -LiteralPath $zip) { throw "Release already exists: $zip. Choose a new version; do not overwrite published artifacts." }
[System.IO.Compression.ZipFile]::CreateFromDirectory($directory, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$packageUrl = if ($GitHubRepository) { "https://github.com/$GitHubRepository/releases/download/v$Version/$name" } else { $BaseUrl.TrimEnd('/') + '/api/updates/files/' + $name }
$payloadObject = [ordered]@{ version = $Version; notes = $Notes; publishedAt = [DateTimeOffset]::UtcNow.ToString('O'); artifacts = @(@{ runtime = $Runtime; url = $packageUrl; sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash; size = (Get-Item -LiteralPath $zip).Length }) }
$payload = [Text.Encoding]::UTF8.GetBytes(($payloadObject | ConvertTo-Json -Depth 8 -Compress))
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $SigningKeyPath).Path))
    $publicKey = [Security.Cryptography.RSA]::Create()
    try {
        $publicKey.ImportFromPem([IO.File]::ReadAllText((Join-Path $PWD 'src/Starlight.Domain/release-public.pem')))
        if ([Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()) -ne [Convert]::ToBase64String($publicKey.ExportSubjectPublicKeyInfo())) { throw 'Signing key does not match the public key trusted by installed launchers.' }
    } finally { $publicKey.Dispose() }
    $signature = $rsa.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
    $envelope = @{ payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($signature) } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path (Resolve-Path $Output).Path 'latest.json.tmp'), $envelope)
    Move-Item -LiteralPath (Join-Path (Resolve-Path $Output).Path 'latest.json.tmp') -Destination (Join-Path (Resolve-Path $Output).Path 'latest.json') -Force
} finally { $rsa.Dispose() }
Write-Output "Published $Version to $zip. Keep .tools/release-private.pem private and backed up."
