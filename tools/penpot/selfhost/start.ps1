$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.env'))) { & (Join-Path $PSScriptRoot 'setup.ps1') }
& docker compose --project-name molehill-penpot --project-directory $PSScriptRoot -f (Join-Path $PSScriptRoot 'compose.yaml') up -d --quiet-pull
if ($LASTEXITCODE -ne 0) { throw 'Penpot did not start. Check that Docker Desktop is running.' }
Write-Output 'Penpot: http://localhost:9001'
