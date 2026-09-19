<#
.SYNOPSIS
    Runs one MoleHill validation lane and reports precisely what it exercised.

.DESCRIPTION
    R09 of docs/codebase-review-and-implementation-plan-2026-09-19.md. The lanes are deliberately
    separate because a green managed run says nothing about native behaviour, packaged artifacts, or
    performance - and a report that blurs them invites reading a skip as a pass.

      managed   Fast source-linked regressions. Native tests skip here, and the summary says how many.
      native    The same suites with MOLEHILL_REQUIRE_NATIVE=1: a missing Rhino runtime FAILS preflight
                instead of skipping. Needs an installed Rhino (see -RhinoDir).
      perf      Opt-in benchmarks with MOLEHILL_PERF=1 and MOLEHILL_REQUIRE_PERF=1, so a benchmark that
                did not execute its body fails rather than passing quietly. Release, serialized.
      warnings  Rebuilds Core with the project-wide nullability suppressions lifted and fails on any
                warning outside vendored src/TriangleNet. This is the owned-code warning ratchet.
      package   Builds the Yak package (never -Push) and checks the archive's contents.
      all       managed, warnings, then package.

.EXAMPLE
    ./validate.ps1 managed
.EXAMPLE
    ./validate.ps1 native -RhinoDir 'D:\Rhino 8'
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('managed', 'native', 'perf', 'warnings', 'package', 'all')]
    [string]$Lane = 'managed',

    # Rhino install root (the folder containing System\ and Plug-ins\). Overrides the default
    # 'C:\Program Files\Rhino 8' for both the MSBuild references and the native runtime probe.
    [string]$RhinoDir,

    # Where TRX logs and reports land.
    [string]$ResultsDirectory = '.artifacts/validate'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:LaneSucceeded = $false
Push-Location $repoRoot
try {
    $solution = Join-Path $repoRoot 'MoleHill.sln'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $laneResults = Join-Path $ResultsDirectory "$Lane-$stamp"
    New-Item -ItemType Directory -Force -Path $laneResults | Out-Null

    if ($RhinoDir) { $env:MOLEHILL_RHINO_DIR = $RhinoDir }

    function Write-Banner([string]$text) {
        Write-Host ''
        Write-Host "=== $text" -ForegroundColor Cyan
    }

    function Get-Environment {
        $sdk = (& dotnet --version) 2>$null
        $commit = (& git rev-parse --short HEAD) 2>$null
        [pscustomobject]@{
            Commit    = $commit
            Sdk       = $sdk
            Machine   = $env:COMPUTERNAME
            Processors = [Environment]::ProcessorCount
            RhinoDir  = $(if ($env:MOLEHILL_RHINO_DIR) { $env:MOLEHILL_RHINO_DIR } else { 'C:\Program Files\Rhino 8' })
            Timestamp = (Get-Date).ToString('o')
        }
    }

    # Runs dotnet test and returns the per-suite pass/fail/skip lines, so the lane report can state
    # skips explicitly instead of leaving them inside a single green total.
    function Invoke-TestLane([string]$configuration, [string[]]$extraArgs) {
        $arguments = @(
            'test', $solution,
            '--configuration', $configuration,
            '-p:SkipGrasshopperLibraryCopy=True',
            # One TRX per test project: a shared LogFileName makes the suites overwrite each other,
            # leaving a report that describes only whichever finished last.
            '--logger', 'trx',
            '--results-directory', $laneResults
        ) + $extraArgs

        Write-Host "dotnet $($arguments -join ' ')"
        $output = & dotnet @arguments 2>&1
        $exit = $LASTEXITCODE
        $output | Tee-Object -FilePath (Join-Path $laneResults 'console.log') | Out-Host
        [pscustomobject]@{
            ExitCode = $exit
            Summary  = @($output | Where-Object { $_ -match '^(Passed!|Failed!|Skipped!)' })
            Lines    = $output
        }
    }

    function Assert-Success($result, [string]$what) {
        if ($result.ExitCode -ne 0) { throw "$what failed (exit $($result.ExitCode)). See $laneResults." }
    }

    function Invoke-Managed {
        Write-Banner 'Lane: managed (fast source-linked regressions; native tests skip)'
        $result = Invoke-TestLane 'Debug' @('--no-restore')
        Assert-Success $result 'Managed lane'
        $result.Summary | ForEach-Object { Write-Host $_ }
        Write-Host 'Managed lane is NOT native acceptance: the skipped counts above are native tests.' -ForegroundColor Yellow
        return $result
    }

    function Invoke-Native {
        Write-Banner 'Lane: native (Rhino runtime required; a missing runtime fails preflight)'
        $env:MOLEHILL_REQUIRE_NATIVE = '1'
        try {
            $result = Invoke-TestLane 'Debug' @('--no-restore')
            Assert-Success $result 'Native lane'
            $result.Summary | ForEach-Object { Write-Host $_ }
            return $result
        }
        finally {
            Remove-Item Env:MOLEHILL_REQUIRE_NATIVE -ErrorAction SilentlyContinue
        }
    }

    function Invoke-Perf {
        Write-Banner 'Lane: perf (benchmark bodies must execute; Release, serialized)'
        $env:MOLEHILL_PERF = '1'
        $env:MOLEHILL_REQUIRE_PERF = '1'
        try {
            $result = Invoke-TestLane 'Release' @(
                '--filter', 'FullyQualifiedName~Benchmark',
                '--logger', 'console;verbosity=detailed',
                '--', 'xUnit.parallelizeTestCollections=false')
            Assert-Success $result 'Performance lane'

            $ran = @($result.Lines | Where-Object { $_ -match 'BENCHMARK RAN: ' })
            $notRun = @($result.Lines | Where-Object { $_ -match 'BENCHMARK NOT RUN: ' })
            Write-Host "Benchmark bodies executed: $($ran.Count); not executed: $($notRun.Count)"
            $notRun | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
            if ($ran.Count -eq 0) { throw 'Performance lane executed no benchmark bodies.' }
            return $result
        }
        finally {
            Remove-Item Env:MOLEHILL_PERF -ErrorAction SilentlyContinue
            Remove-Item Env:MOLEHILL_REQUIRE_PERF -ErrorAction SilentlyContinue
        }
    }

    function Invoke-Warnings {
        Write-Banner 'Lane: warnings (owned-code ratchet; vendored TriangleNet exempt)'
        # The vendor exemption is scoped in .editorconfig ([src/TriangleNet/**.cs] generated_code = true),
        # not by a project-wide NoWarn, so owned code is analysed normally and this can simply demand
        # zero. -warnaserror makes that a build failure rather than a number someone has to read.
        $editorConfig = Get-Content -Raw (Join-Path $repoRoot '.editorconfig')
        if ($editorConfig -notmatch 'generated_code\s*=\s*true') {
            throw 'The vendored-source exemption is missing from .editorconfig; the sweep would drown in TriangleNet warnings.'
        }

        $arguments = @(
            'build', $solution,
            '--no-incremental',
            '-warnaserror',
            '-p:SkipGrasshopperLibraryCopy=True'
        )
        Write-Host "dotnet $($arguments -join ' ')"
        $output = & dotnet @arguments 2>&1
        $exit = $LASTEXITCODE
        $output | Set-Content -Encoding utf8 -Path (Join-Path $laneResults 'warnings.log')

        $compiled = @($output | Where-Object { $_ -match '->\s.+\.(dll|rhp|gha)$' })
        if ($compiled.Count -eq 0) {
            throw 'The sweep produced no compiler output, so it measured nothing. See warnings.log.'
        }

        $promoted = @($output | Where-Object { $_ -match 'error CS' })
        $promoted | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        if ($exit -ne 0) {
            throw "Owned-code warnings: $($promoted.Count). Fix them, or scope the suppression to the sources that need it."
        }

        Write-Host "Projects compiled: $($compiled.Count). Owned-code warning count is 0 (the ratchet)." -ForegroundColor Green
    }

    function Invoke-Package {
        Write-Banner 'Lane: package (build the Yak archive and check its contents; never -Push)'
        & (Join-Path $repoRoot 'build-yak-package.ps1')
        if ($LASTEXITCODE -ne 0) { throw "build-yak-package.ps1 failed (exit $LASTEXITCODE)." }

        $archive = Get-ChildItem -Path (Join-Path $repoRoot '.artifacts/yak') -Filter '*.yak' -Recurse |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $archive) { throw 'No .yak archive was produced under .artifacts/yak.' }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archive.FullName)
        try {
            $entries = $zip.Entries | ForEach-Object { $_.FullName }
        }
        finally {
            $zip.Dispose()
        }
        $entries | Set-Content -Encoding utf8 -Path (Join-Path $laneResults 'package-contents.txt')

        $required = @('MoleHill.Rhino.rhp', 'MoleHill.gha', 'MoleHill.Core.dll', 'MoleHill.Interop.dll', 'manifest.yml')
        $missing = @($required | Where-Object { $name = $_; -not ($entries | Where-Object { $_ -like "*$name" }) })
        Write-Host "Archive: $($archive.FullName) ($($entries.Count) entries)"
        if ($missing.Count -gt 0) { throw "Package is missing: $($missing -join ', ')" }
        Write-Host 'Archive contains the Rhino plugin, the merged Grasshopper assembly and Interop.' -ForegroundColor Green
    }

    Write-Banner "MoleHill validation - lane '$Lane'"
    Get-Environment | Format-List | Out-Host
    (Get-Environment | ConvertTo-Json) | Set-Content -Encoding utf8 -Path (Join-Path $laneResults 'environment.json')

    switch ($Lane) {
        'managed'  { Invoke-Managed | Out-Null }
        'native'   { Invoke-Native | Out-Null }
        'perf'     { Invoke-Perf | Out-Null }
        'warnings' { Invoke-Warnings }
        'package'  { Invoke-Package }
        'all' {
            Invoke-Managed | Out-Null
            Invoke-Warnings
            Invoke-Package
        }
    }

    Write-Banner "Lane '$Lane' completed. Evidence: $laneResults"
    $script:LaneSucceeded = $true
}
catch {
    Write-Host ''
    Write-Host "Lane '$Lane' FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Evidence: $laneResults"
}
finally {
    Pop-Location
}

# dotnet leaves $LASTEXITCODE behind even on a lane that then passed its own checks, so the exit code
# is set from the lane's verdict rather than inherited.
if ($script:LaneSucceeded) { exit 0 } else { exit 1 }
