[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$Push,
    [string]$Source = "https://yak.rhino3d.com/",
    [string]$YakExecutable = "C:\Program Files\Rhino 8\System\Yak.exe"
)

$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $FilePath $($ArgumentList -join ' ')"
    }
}

function Invoke-StepWithRetry {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [int]$MaxAttempts = 3,
        [int]$DelaySeconds = 2
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Invoke-Step $FilePath $ArgumentList
            return
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                throw
            }

            Start-Sleep -Seconds $DelaySeconds
        }
    }
}

function Copy-RequiredFile {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path $Source)) {
        throw "Missing required file '$Source'."
    }

    Copy-Item -Path $Source -Destination $Destination -Force
}

function Copy-RuntimeAssemblies {
    param(
        [string[]]$SourceDirectories,
        [string]$DestinationDirectory,
        [string[]]$ExcludedNames = @()
    )

    $copiedAssemblies = @{}

    foreach ($sourceDirectory in $SourceDirectories) {
        if (-not (Test-Path $sourceDirectory)) {
            throw "Runtime assembly source directory is missing: '$sourceDirectory'."
        }

        Get-ChildItem -Path $sourceDirectory -Filter "*.dll" -File |
            Where-Object { $ExcludedNames -notcontains $_.Name } |
            ForEach-Object {
                $destination = Join-Path $DestinationDirectory $_.Name
                if ($copiedAssemblies.ContainsKey($_.Name)) {
                    $existing = Get-Item -Path $destination
                    if ($existing.Length -ne $_.Length) {
                        throw "Conflicting runtime assembly '$($_.Name)' found in '$($copiedAssemblies[$_.Name])' and '$($_.FullName)'."
                    }

                    return
                }

                Copy-Item -Path $_.FullName -Destination $destination -Force
                $copiedAssemblies[$_.Name] = $_.FullName
            }
    }
}

function Remove-DirectoryWithRetry {
    param(
        [string]$Path,
        [int]$MaxAttempts = 3,
        [int]$DelaySeconds = 2
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Remove-Item -Path $Path -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                throw
            }

            Start-Sleep -Seconds $DelaySeconds
        }
    }
}

$repoRoot = $PSScriptRoot
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$dotnetCliHome = Join-Path $repoRoot ".dotnet-home"
$nugetPackages = Join-Path $repoRoot ".dotnet\.nuget\packages"

if (-not (Test-Path $YakExecutable)) {
    throw "Yak executable not found at '$YakExecutable'."
}

if (-not (Test-Path $propsPath)) {
    throw "Version file not found at '$propsPath'."
}

[xml]$props = Get-Content -Path $propsPath
# SelectSingleNode rather than $props.Project.PropertyGroup.MoleHillVersion: with more than one
# PropertyGroup that dotted access returns an array and throws, which once failed packaging for a
# reason that had nothing to do with the version.
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/MoleHillVersion')
$version = if ($versionNode) { $versionNode.InnerText } else { $null }

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "MoleHillVersion is missing from '$propsPath'."
}

New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"

if (Test-Path $nugetPackages) {
    $env:NUGET_PACKAGES = $nugetPackages
}

$rhinoProject = Join-Path $repoRoot "src\MoleHill.Rhino\MoleHill.Rhino.csproj"
$grasshopperProject = Join-Path $repoRoot "src\MoleHill.Grasshopper\MoleHill.Grasshopper.csproj"

$buildRoot = Join-Path $repoRoot ".artifacts\yak-build\$version-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$rhinoOutput = Join-Path $buildRoot "rhino"
$grasshopperOutput = Join-Path $buildRoot "grasshopper"
$stageRoot = Join-Path $repoRoot ".artifacts\yak\MoleHill-$version"
$packageContentRoot = Join-Path $stageRoot "net7.0"
$miscDirectory = Join-Path $packageContentRoot "misc"
$miscLicensesDirectory = Join-Path $miscDirectory "licenses"

Invoke-Step "dotnet" @(
    "build",
    $rhinoProject,
    "-c", $Configuration,
    "--no-restore",
    "-p:OutputPath=$rhinoOutput\",
    "-p:AppendTargetFrameworkToOutputPath=false"
)
Invoke-StepWithRetry "dotnet" @(
    "build",
    $grasshopperProject,
    "-c", $Configuration,
    "-f", "net7.0-windows",
    "--no-restore",
    "-p:OutputPath=$grasshopperOutput\",
    "-p:AppendTargetFrameworkToOutputPath=false",
    "-p:BuildYakPackage=false",
    "-p:SkipGrasshopperLibraryCopy=True"
)

if (Test-Path $stageRoot) {
    Remove-DirectoryWithRetry $stageRoot
}

New-Item -ItemType Directory -Path $miscDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $miscLicensesDirectory -Force | Out-Null

$filesToCopy = @(
    @{ Source = Join-Path $rhinoOutput "MoleHill.Rhino.rhp"; Destination = Join-Path $packageContentRoot "MoleHill.Rhino.rhp" }
    @{ Source = Join-Path $rhinoOutput "MoleHill.Core.dll"; Destination = Join-Path $packageContentRoot "MoleHill.Core.dll" }
    @{ Source = Join-Path $rhinoOutput "MoleHill.Interop.dll"; Destination = Join-Path $packageContentRoot "MoleHill.Interop.dll" }
    @{ Source = Join-Path $rhinoOutput "MoleHill.Rhino.deps.json"; Destination = Join-Path $packageContentRoot "MoleHill.Rhino.deps.json" }
    @{ Source = Join-Path $rhinoOutput "MoleHill.Rhino.runtimeconfig.json"; Destination = Join-Path $packageContentRoot "MoleHill.Rhino.runtimeconfig.json" }
    @{ Source = Join-Path $grasshopperOutput "MoleHill.gha"; Destination = Join-Path $packageContentRoot "MoleHill.gha" }
    @{ Source = Join-Path $grasshopperOutput "MoleHill.deps.json"; Destination = Join-Path $packageContentRoot "MoleHill.deps.json" }
    @{ Source = Join-Path $grasshopperOutput "MoleHill.runtimeconfig.json"; Destination = Join-Path $packageContentRoot "MoleHill.runtimeconfig.json" }
    @{ Source = Join-Path $repoRoot "README.md"; Destination = Join-Path $miscDirectory "README.md" }
    @{ Source = Join-Path $repoRoot "LICENSE"; Destination = Join-Path $miscDirectory "LICENSE.txt" }
    @{ Source = Join-Path $repoRoot "src\MoleHill.Grasshopper\Resources\MoleHill.png"; Destination = Join-Path $stageRoot "icon.png" }
)

foreach ($file in $filesToCopy) {
    Copy-RequiredFile $file.Source $file.Destination
}

Copy-RuntimeAssemblies @($rhinoOutput, $grasshopperOutput) $packageContentRoot @("MoleHill.Core.dll", "MoleHill.Interop.dll")

$rhinoToolbarDirectory = Join-Path $rhinoOutput "Toolbars"
if (Test-Path $rhinoToolbarDirectory) {
    Copy-Item -Path $rhinoToolbarDirectory -Destination (Join-Path $packageContentRoot "Toolbars") -Recurse -Force
}

Copy-Item -Path (Join-Path $repoRoot "LICENSES\*") -Destination $miscLicensesDirectory -Recurse -Force

$manifest = @"
---
name: MoleHill
version: $version
authors:
- rheinason
description: Rhino terrain modeling plugin with optional Grasshopper components for TIN creation, grading, and analysis workflows.
url: https://github.com/rheinason/MoleHill
icon: icon.png
keywords:
- molehill
- rhino
- grasshopper
- terrain
- grading
- tin
- mesh
"@

Set-Content -Path (Join-Path $stageRoot "manifest.yml") -Value $manifest -Encoding ascii

Push-Location $stageRoot
try {
    Invoke-Step $YakExecutable @("build", "--platform", "win")

    $package = Get-ChildItem -Path $stageRoot -Filter "*.yak" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $package) {
        throw "Yak build did not produce a package."
    }

    # Inspect what is actually inside the archive before it can be pushed.
    #
    # This exists because 0.14.3-beta shipped to the production server carrying only MoleHill.gha: it
    # had been built by the csproj target, which ran `yak spec --input MoleHill.gha` and therefore
    # described the Grasshopper assembly. It installed cleanly, gave Grasshopper its components, and
    # left Rhino's PlugInManager empty, because the package held no .rhp at all. A Yak version can
    # never be overwritten, so that mistake is permanent and had to be fixed by publishing again.
    #
    # Staging the right files is not evidence the archive holds them, so assert on the archive.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = $archive.Entries | ForEach-Object { $_.FullName }
    }
    finally {
        $archive.Dispose()
    }

    $requiredEntries = @(
        "MoleHill.Rhino.rhp",
        "MoleHill.gha",
        "MoleHill.Core.dll",
        "MoleHill.Interop.dll",
        "manifest.yml"
    )
    $missing = $requiredEntries | Where-Object { $name = $_; -not ($entries | Where-Object { $_ -like "*$name" }) }
    if ($missing) {
        throw ("Package '{0}' is missing: {1}. Refusing to publish a package that would install but not appear in Rhino's PlugInManager." -f $package.Name, ($missing -join ', '))
    }

    Write-Host ("Verified {0} entries, including {1}" -f $entries.Count, ($requiredEntries -join ', '))

    if ($Push) {
        Invoke-Step $YakExecutable @("push", "--source", $Source, $package.FullName)
    }

    Write-Host "Yak package ready: $($package.FullName)"
}
finally {
    Pop-Location
}
