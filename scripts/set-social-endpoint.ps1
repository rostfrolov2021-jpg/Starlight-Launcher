param([Parameter(Mandatory)][string]$Url)
$ErrorActionPreference = 'Stop'
$address = [Uri]($Url.Trim().TrimEnd('/') + '/')
if (-not $address.IsAbsoluteUri -or $address.Scheme -ne 'https' -or $address.IsLoopback -or $address.UserInfo -or $address.Query -or $address.Fragment) {
    throw 'Use the public HTTPS service URL, without credentials, query or fragment.'
}
$health = Invoke-RestMethod -Uri ([Uri]::new($address, 'health')) -TimeoutSec 15
$config = Invoke-RestMethod -Uri ([Uri]::new($address, 'api/client-config')) -TimeoutSec 15
if ($health.status -ne 'ok' -or $health.service -ne 'Starlight' -or $config.service -ne 'Starlight') { throw 'This address is not a healthy Starlight service.' }
$target = Join-Path (Split-Path $PSScriptRoot -Parent) 'website/social.json'
$json = @{ schema = 1; url = $address.AbsoluteUri } | ConvertTo-Json
[IO.File]::WriteAllText($target, $json + [Environment]::NewLine)
Write-Output 'Verified service. Commit and push website/social.json to publish its address to launchers.'
