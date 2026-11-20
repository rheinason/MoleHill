$ErrorActionPreference = 'Stop'
& docker compose --project-name molehill-penpot --project-directory $PSScriptRoot -f (Join-Path $PSScriptRoot 'compose.yaml') stop
if ($LASTEXITCODE -ne 0) { throw 'Penpot containers could not be stopped.' }
Write-Output 'Penpot stopped. Its database and assets remain in Docker volumes.'
