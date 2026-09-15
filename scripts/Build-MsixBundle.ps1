param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\artifacts\packages\$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')")
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\src\GitHubTray.App\GitHubTray.App.csproj')).Path
$projectDirectory = Split-Path $project
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Output directory already exists. Choose a new directory to avoid stale package payloads: $output"
}
Get-Command dotnet, winapp -ErrorAction Stop | Out-Null

$installerDirectory = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $installerDirectory 'vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Install Visual Studio Build Tools with Desktop development with C++ and the ARM64 C++ tools before publishing.'
}
foreach ($component in @('x86.x64', 'ARM64')) {
    $installation = & $vswhere -latest -prerelease -products * -requires "Microsoft.VisualStudio.Component.VC.Tools.$component" -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $installation) {
        throw "Missing Visual Studio C++ tools: Microsoft.VisualStudio.Component.VC.Tools.$component"
    }
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $projectDirectory 'Package.appxmanifest')
$version = $manifest.Package.Identity.Version
$bundle = Join-Path $output "GitHubTray_${version}_x64_x86_arm64.msixbundle"
$layouts = @()
$previousPath = $env:PATH
try {
    # vcvarsall may invoke vswhere by name; keep discovery local to this build.
    $env:PATH = "$installerDirectory;$previousPath"
    foreach ($architecture in @('x64', 'x86', 'arm64')) {
        $layout = Join-Path $output "win-$architecture"
        & dotnet publish $project -c Release "-p:PublishProfile=win-$architecture" --output $layout -warnaserror
        if ($LASTEXITCODE -ne 0) {
            throw "NativeAOT publishing failed for $architecture."
        }
        & (Join-Path $PSScriptRoot 'Test-NativeAot.ps1') -PublishDirectory $layout
        $layouts += $layout
    }

    Push-Location $projectDirectory
    try {
        # The SDK's PRI already embeds compiled XAML; rebuilding it would discard that data.
        & winapp package @layouts --skip-pri --exe GitHubTray.App.exe --output $bundle
        if ($LASTEXITCODE -ne 0) {
            throw 'MSIX bundle creation failed.'
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:PATH = $previousPath
}

if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
    throw "Packaging did not produce the expected bundle: $bundle"
}
Write-Output "Unsigned MSIX bundle: $bundle"
