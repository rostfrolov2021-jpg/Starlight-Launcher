param(
    [string]$Repository = 'rostfrolov2021-jpg/Starlight-Launcher',
    [switch]$SkipBuild,
    [switch]$Draft,
    [string]$SigningKeyPath = '.tools/release-private.pem'
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this script with PowerShell 7 (pwsh).' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
$gh = if (Test-Path '.tools/gh/bin/gh.exe') { (Resolve-Path '.tools/gh/bin/gh.exe').Path } else { 'gh' }
function Invoke-Gh([string[]]$Arguments) {
    & $gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed: $($Arguments[0])" }
}
$version = ([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Set a stable X.Y.Z version in Directory.Build.props.' }
$head = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Commit and push the source before publishing.' }
if (git status --porcelain) { throw 'Commit all source changes before publishing.' }
$remoteHead = Invoke-Gh @('api', "repos/$Repository/commits/main", '--jq', '.sha')
if ($remoteHead.Trim() -ne $head.Trim()) { throw 'Push the current commit to main before publishing.' }
& $gh release view "v$version" --repo $Repository *> $null
if ($LASTEXITCODE -eq 0) { throw "Release v$version already exists. Use a new version; published packages are immutable." }
if (-not $SkipBuild) { & ./build.ps1 -Installer }
$installer = "artifacts/installer/Starlight-Setup-$version-win-x64.exe"
if (-not (Test-Path $installer)) { throw 'Installer is missing. Build with -Installer first.' }
$output = "artifacts/github-releases/v$version"
& ./scripts/publish-release.ps1 -Version $version -GitHubRepository $Repository -SigningKeyPath $SigningKeyPath -Output $output -Notes (Get-Content 'RELEASE_NOTES.md' -Raw)
$zip = "$output/Starlight-$version-win-x64.zip"
Copy-Item -LiteralPath $installer -Destination $output
$installer = Join-Path $output (Split-Path $installer -Leaf)
$directInstaller = Join-Path $output 'Starlight-Setup-win-x64.exe'
Copy-Item -LiteralPath $installer -Destination $directInstaller
$checksums = @($zip, $installer, $directInstaller, "$output/latest.json") | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $_ -Leaf) }
[IO.File]::WriteAllLines((Join-Path $PWD "$output/SHA256SUMS.txt"), $checksums)
Invoke-Gh @('release', 'create', "v$version", $zip, $installer, $directInstaller, "$output/latest.json", "$output/SHA256SUMS.txt", '--repo', $Repository, '--target', $head.Trim(), '--draft', '--title', "Starlight $version", '--notes-file', 'RELEASE_NOTES.md')
$assets = (Invoke-Gh @('release', 'view', "v$version", '--repo', $Repository, '--json', 'assets') | ConvertFrom-Json).assets
foreach ($file in @($zip, $installer, $directInstaller, "$output/latest.json", "$output/SHA256SUMS.txt")) {
    $local = Get-Item -LiteralPath $file
    if (-not ($assets | Where-Object { $_.name -eq $local.Name -and $_.size -eq $local.Length })) { throw 'Uploaded assets are incomplete; release remains a draft.' }
}
if (-not $Draft) { Invoke-Gh @('release', 'edit', "v$version", '--repo', $Repository, '--draft=false', '--latest') }
Write-Output "https://github.com/$Repository/releases/tag/v$version"
