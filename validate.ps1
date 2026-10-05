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
      hosted-perf
                The full-stack benchmarks inside a disposable Rhino (rhino-mcp slot), sampled and compared
                against tests/perf-baselines/hosted-perf.json. Fails on a regression beyond -Margin and
                -FloorMs. -UpdateBaseline records this run as the new baseline instead of comparing.
      all       managed, warnings, then package.

.EXAMPLE
    ./validate.ps1 managed
.EXAMPLE
    ./validate.ps1 native -RhinoDir 'D:\Rhino 8'
.EXAMPLE
    ./validate.ps1 hosted-perf
.EXAMPLE
    ./validate.ps1 hosted-perf -Scenario analysis-heavy -Samples 7
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('managed', 'native', 'perf', 'warnings', 'package', 'hosted-perf', 'all')]
    [string]$Lane = 'managed',

    # Rhino install root (the folder containing System\ and Plug-ins\). Overrides the default
    # 'C:\Program Files\Rhino 8' for both the MSBuild references and the native runtime probe.
    [string]$RhinoDir,

    # Where TRX logs and reports land.
    [string]$ResultsDirectory = '.artifacts/validate',

    # hosted-perf: measured samples per scenario (one extra warm-up sample is always discarded).
    [ValidateRange(1, 50)]
    [int]$Samples = 5,

    # hosted-perf: run only these scenarios (geometry-heavy, analysis-heavy, interactive). Default: all.
    [string[]]$Scenario,

    # hosted-perf: a metric regresses when its median is this much slower relatively...
    [double]$Margin = 0.20,

    # hosted-perf: ...and this many milliseconds slower absolutely.
    [double]$FloorMs = 25,

    # hosted-perf: write this run to the baseline instead of comparing against it.
    [switch]$UpdateBaseline,

    # hosted-perf: judge an existing result instead of building and spawning. For a host whose shell may
    # not spawn Rhino (breakaway denied): run `tools/rhino-hosted-perf.py --print-script` in a slot you
    # drive, then pass the result file here.
    [string]$HostedResult
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
        # Compile from scratch before testing. On 2026-09-19 this lane reported green four times against
        # a MoleHill.Grasshopper.Tests.dll compiled before a Rhino auto-update, while the project could
        # no longer be compiled at all (CS1705, installed Grasshopper 8.35 against a pinned RhinoCommon
        # 8.34). A lane that can pass on a project that does not build is not a regression signal.
        # It has to be a separate build step: 'dotnet test' rejects --no-incremental.
        $buildArguments = @('build', $solution, '--configuration', 'Debug', '--no-incremental', '-p:SkipGrasshopperLibraryCopy=True')
        Write-Host "dotnet $($buildArguments -join ' ')"
        & dotnet @buildArguments | Tee-Object -FilePath (Join-Path $laneResults 'build.log') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Managed lane build failed (exit $LASTEXITCODE). See $laneResults." }

        $result = Invoke-TestLane 'Debug' @('--no-restore', '--no-build')
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
            if ($promoted.Count -gt 0) {
                throw "Owned-code compiler diagnostics: $($promoted.Count). See warnings.log; fix errors or scope warning suppression to the sources that need it."
            }
            $buildErrors = @($output | Where-Object { $_ -match ': error ' } | Select-Object -Unique)
            $buildErrors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
            throw "Warnings build failed (exit $exit) with no error CS diagnostics. See warnings.log for restore, file-access or other build errors."
        }

        Write-Host "Projects compiled: $($compiled.Count). Owned-code warning count is 0 (the ratchet)." -ForegroundColor Green
    }

    function Invoke-Package {
        Write-Banner 'Lane: package (build the Yak archive and check its contents; never -Push)'
        # Run the packaging script in its OWN process. yak writes an expected warning to stderr
        # ("Content name doesn't match manifest", documented in AGENTS.md as acceptable for the
        # combined package); piping or redirecting that inside this session turns it into an
        # ErrorRecord, which the packaging script's own ErrorActionPreference=Stop then treats as
        # fatal. A child process keeps its streams out of this pipeline, and the exit code - not a
        # stderr line - decides the verdict.
        $packageLog = Join-Path $laneResults 'package-build.log'
        $packageErrorLog = Join-Path $laneResults 'package-build.err.log'
        $packageProcess = Start-Process -FilePath 'powershell' -PassThru -Wait -NoNewWindow `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot 'build-yak-package.ps1')) `
            -RedirectStandardOutput $packageLog -RedirectStandardError $packageErrorLog
        Get-Content $packageLog -Tail 20 | Out-Host
        if ($packageProcess.ExitCode -ne 0) {
            Get-Content $packageErrorLog | Out-Host
            throw "build-yak-package.ps1 failed (exit $($packageProcess.ExitCode)). See $packageLog."
        }

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

        $required = @('MoleHill.Rhino.rhp', 'MoleHill.gha', 'MoleHill.Revit.gha', 'MoleHill.Core.dll', 'MoleHill.Interop.dll', 'manifest.yml')
        $missing = @($required | Where-Object { $name = $_; -not ($entries | Where-Object { $_ -like "*$name" }) })
        Write-Host "Archive: $($archive.FullName) ($($entries.Count) entries)"
        if ($missing.Count -gt 0) { throw "Package is missing: $($missing -join ', ')" }
        Write-Host 'Archive contains the Rhino plugin, both Grasshopper assemblies and Interop.' -ForegroundColor Green
    }

    function Invoke-HostedPerf {
        Write-Banner 'Lane: hosted-perf (full-stack benchmarks inside a disposable Rhino, against the baseline)'
        # Release and from scratch. The lane measures the test build of the linked sources plus its own
        # MoleHill.Core, loaded in isolation inside the slot; a stale incremental build would measure old
        # code and say nothing (see the managed lane's note on 2026-09-19).
        $baselinePath = Join-Path $repoRoot 'tests/perf-baselines/hosted-perf.json'
        if ($HostedResult) {
            $resultPath = (Resolve-Path $HostedResult).Path
            Write-Host "Judging an existing result: $resultPath"
            $commit = (Get-Content -Raw $resultPath | ConvertFrom-Json).Environment.Commit
        }
        else {
            $resultPath = Invoke-HostedRun $baselinePath | Select-Object -Last 1
            $commit = (Get-Content -Raw $resultPath | ConvertFrom-Json).Environment.Commit
        }
        Complete-HostedPerf $resultPath $baselinePath $commit
    }

    # Builds, spawns and runs; returns the path of the result file the slot wrote.
    function Invoke-HostedRun([string]$baselinePath) {
        $testProject = Join-Path $repoRoot 'tests/MoleHill.Rhino.Tests/MoleHill.Rhino.Tests.csproj'
        $buildArguments = @('build', $testProject, '--configuration', 'Release', '--no-incremental', '-p:SkipGrasshopperLibraryCopy=True')
        Write-Host "dotnet $($buildArguments -join ' ')"
        & dotnet @buildArguments | Tee-Object -FilePath (Join-Path $laneResults 'build.log') | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Release build of the Rhino tests failed (exit $LASTEXITCODE). A slot still holding the test DLL is the usual cause - see docs/rhino-live-testing.md section 2. See $laneResults."
        }

        $binDir = Join-Path $repoRoot 'tests/MoleHill.Rhino.Tests/bin/Release/net8.0'
        $laneFull = (Resolve-Path $laneResults).Path
        $resultPath = Join-Path $laneFull 'hosted-perf.json'
        $requestPath = Join-Path $laneFull 'request.json'
        $commit = (& git rev-parse --short HEAD) 2>$null
        if ((& git status --porcelain) 2>$null) { $commit = "$commit+dirty" }

        $request = [ordered]@{
            ResultPath   = $resultPath
            BaselinePath = $(if ($UpdateBaseline -or -not (Test-Path $baselinePath)) { $null } else { (Resolve-Path $baselinePath).Path })
            Samples      = $Samples
            Warmups      = 1
            Margin       = $Margin
            FloorMs      = $FloorMs
            Commit       = $commit
            Scenarios    = $(if ($Scenario) { @($Scenario) } else { $null })
        }
        ($request | ConvertTo-Json) | Set-Content -Encoding utf8 -Path $requestPath

        $python = Get-Command py -ErrorAction SilentlyContinue
        $pythonArgs = @()
        if ($python) { $pythonArgs = @('-3') } else { $python = Get-Command python -ErrorAction Stop }
        $pythonArgs += @((Join-Path $repoRoot 'tools/rhino-hosted-perf.py'), '--bin', $binDir, '--request', $requestPath)
        Write-Host "$($python.Name) $($pythonArgs -join ' ')"

        # The driver reports progress on stdout and errors on stderr. Under ErrorActionPreference=Stop,
        # Windows PowerShell turns a native stderr line into a terminating error, so let the exit code - not
        # a stderr line - decide.
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $python.Source @pythonArgs 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath (Join-Path $laneResults 'driver.log') | Out-Host
            $driverExit = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }
        if ($driverExit -ne 0 -or -not (Test-Path $resultPath)) {
            throw "The hosted run produced no result (driver exit $driverExit). See driver.log in $laneResults."
        }
        return $resultPath
    }

    # Reports a hosted result and judges it against the baseline, or records it as the baseline.
    function Complete-HostedPerf([string]$resultPath, [string]$baselinePath, [string]$commit) {
        $evidence = Join-Path (Resolve-Path $laneResults).Path 'hosted-perf.json'
        if ($resultPath -ne $evidence) { Copy-Item -Path $resultPath -Destination $evidence -Force }
        $result = Get-Content -Raw $resultPath | ConvertFrom-Json
        if ($result.PSObject.Properties['Error'] -and $result.Error) {
            throw "The hosted run failed inside Rhino: $($result.Error)"
        }

        Write-Host ''
        Write-Host "Measured in Rhino $($result.Environment.RhinoVersion), $($result.Environment.Runtime), $($result.SamplesPerScenario) samples per scenario"
        Write-Host "Core: $($result.Environment.CoreAssembly) (optimized: $($result.Environment.CoreOptimized))"
        $headline = $result.Metrics.PSObject.Properties | Where-Object {
            $_.Name -match '/wall$' -or
            $_.Name -match '/Analysis [^/]+$' -or
            $_.Name -match '^geometry-heavy/[^/]+/(Grade Pad|Grade Path|Remesh|Smooth|Triangulate)$'
        }
        foreach ($metric in $headline) {
            Write-Host ('  {0,10:N1} ms median  {1,10:N1} ms p95  {2}' -f $metric.Value.MedianMs, $metric.Value.P95Ms, $metric.Name)
        }

        if ($UpdateBaseline) {
            if ($result.PSObject.Properties['Comparison'] -and $result.Comparison) {
                throw 'This result was compared against a baseline, so it is not a clean baseline itself. Record from a run made without one (./validate.ps1 hosted-perf -UpdateBaseline, or a -HostedResult whose request had no BaselinePath).'
            }
            # A partial run must not replace a full baseline: the scenarios it skipped would vanish from
            # the baseline, read as "New" on every later run, and a "New" metric never fails the lane.
            if (Test-Path $baselinePath) {
                $existing = Get-Content -Raw -Path $baselinePath | ConvertFrom-Json
                $dropped = @($existing.Scenarios | Where-Object { @($result.Scenarios) -notcontains $_ })
                if ($dropped.Count -gt 0) {
                    throw "This result ran only $(@($result.Scenarios) -join ', '). Recording it would drop $($dropped -join ', ') from the baseline, and their regressions would then go unreported. Re-baseline from a run of every scenario (omit -Scenario)."
                }
            }

            # The request carried no baseline, so the result has no comparison: copy it byte for byte
            # rather than round-tripping it through ConvertTo-Json.
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $baselinePath) | Out-Null
            Copy-Item -Path $resultPath -Destination $baselinePath -Force
            Write-Host "Baseline written: $baselinePath (commit $commit). Commit it with the change it measures." -ForegroundColor Green
            return
        }

        if (-not ($result.PSObject.Properties['Comparison'] -and $result.Comparison)) {
            throw "No baseline at $baselinePath to compare against. Record one on this machine with: ./validate.ps1 hosted-perf -UpdateBaseline"
        }

        $comparison = $result.Comparison
        if ($comparison.PSObject.Properties['NotComparableReason'] -and $comparison.NotComparableReason) {
            throw "Baseline not comparable: $($comparison.NotComparableReason)"
        }

        Write-Host ''
        Write-Host "Against baseline $($comparison.BaselineCommit): margin $([math]::Round($comparison.Margin * 100))%, floor $($comparison.FloorMs) ms"
        foreach ($verdict in @('Regressed', 'Improved', 'Missing', 'New')) {
            $group = @($comparison.Metrics | Where-Object { $_.Verdict -eq $verdict })
            if ($group.Count -eq 0) { continue }
            $colour = switch ($verdict) { 'Regressed' { 'Red' } 'Improved' { 'Green' } default { 'Yellow' } }
            Write-Host "$verdict ($($group.Count)):" -ForegroundColor $colour
            foreach ($m in $group) {
                $ratio = if ($m.PSObject.Properties['Ratio'] -and $m.Ratio) { 'x{0:N2}' -f $m.Ratio } else { '' }
                $was = if ($m.PSObject.Properties['BaselineMedianMs']) { '{0:N1}' -f $m.BaselineMedianMs } else { '-' }
                $now = if ($m.PSObject.Properties['CurrentMedianMs']) { '{0:N1}' -f $m.CurrentMedianMs } else { '-' }
                Write-Host ('  {0,10} -> {1,10} ms  {2,6}  {3}' -f $was, $now, $ratio, $m.Metric) -ForegroundColor $colour
            }
        }

        $outputChanges = @()
        if ($comparison.PSObject.Properties['OutputChanges'] -and $comparison.OutputChanges) { $outputChanges = @($comparison.OutputChanges) }
        if ($outputChanges.Count -gt 0) {
            Write-Host "Output changed in $($outputChanges.Count) build phase(s) - a change meant only to be faster must leave the terrain identical:" -ForegroundColor Magenta
            $outputChanges | ForEach-Object { Write-Host "  $_" -ForegroundColor Magenta }
        }
        else {
            Write-Host 'Finished meshes identical to the baseline in every phase it recorded.' -ForegroundColor Green
        }

        $regressed = @($comparison.Metrics | Where-Object { $_.Verdict -eq 'Regressed' })
        if ($regressed.Count -gt 0) {
            throw "$($regressed.Count) metric(s) regressed beyond the margin. If the slowdown is intended, re-baseline with -UpdateBaseline and say why in the commit."
        }
        if (@($comparison.Metrics | Where-Object { $_.Verdict -eq 'Improved' }).Count -gt 0) {
            Write-Host 'Improvements beyond the margin: re-baseline with -UpdateBaseline to lock the gain in.' -ForegroundColor Green
        }
        Write-Host 'No regression beyond the margin.' -ForegroundColor Green
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
        'hosted-perf' { Invoke-HostedPerf }
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
