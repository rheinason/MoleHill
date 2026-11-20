$ErrorActionPreference = 'Stop'
$installDirectory = $PSScriptRoot
$environmentFile = Join-Path $installDirectory '.env'
if (-not (Test-Path -LiteralPath $environmentFile)) {
    $secretBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(64)
    $databaseBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $secret = [Convert]::ToBase64String($secretBytes)
    $databasePassword = [Convert]::ToHexString($databaseBytes)
    @("PENPOT_VERSION=2.18.6", "PENPOT_SECRET_KEY=$secret", "PENPOT_DATABASE_PASSWORD=$databasePassword") |
        Set-Content -LiteralPath $environmentFile -Encoding utf8
}

$upstream = Get-Content -LiteralPath (Join-Path $installDirectory 'compose.upstream.yaml') -Raw
$compose = $upstream.Replace('PENPOT_SECRET_KEY: change-this-insecure-key', 'PENPOT_SECRET_KEY: ${PENPOT_SECRET_KEY:?Run setup.ps1 first}')
$compose = $compose.Replace('PENPOT_DATABASE_PASSWORD: penpot', 'PENPOT_DATABASE_PASSWORD: ${PENPOT_DATABASE_PASSWORD:?Run setup.ps1 first}')
$compose = $compose.Replace('POSTGRES_PASSWORD=penpot', 'POSTGRES_PASSWORD=${PENPOT_DATABASE_PASSWORD:?Run setup.ps1 first}')
$compose = $compose.Replace('- 9001:8080', '- "127.0.0.1:9001:8080"')
$compose = $compose.Replace('"8025:8025"', '"127.0.0.1:8025:8025"')
$compose = $compose.Replace('PENPOT_TELEMETRY_ENABLED: "true"', 'PENPOT_TELEMETRY_ENABLED: "false"')
$compose = $compose.Replace('disable-secure-session-cookies enable-mcp', 'disable-secure-session-cookies enable-mcp enable-admin-console')
if ($compose -notmatch '127\.0\.0\.1:9001:8080') { throw 'Upstream port configuration changed; review before starting.' }
$compose | Set-Content -LiteralPath (Join-Path $installDirectory 'compose.yaml') -Encoding utf8
& docker compose --project-name molehill-penpot --project-directory $installDirectory -f (Join-Path $installDirectory 'compose.yaml') config --quiet
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose validation failed.' }
Write-Output 'Penpot configuration validated. Local secrets are stored in the ignored .env file.'
