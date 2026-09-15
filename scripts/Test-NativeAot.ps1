param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$executable = Join-Path $directory 'GitHubTray.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable"
}
foreach ($file in @('AppxManifest.xml', 'resources.pri', 'Assets\AppIcon.ico', 'Assets\StoreLogo.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $directory $file) -PathType Leaf)) {
        throw "Missing package resource '$file' in the NativeAOT publish output."
    }
}

foreach ($file in @('GitHubTray.App.dll', 'GitHubTray.App.runtimeconfig.json', 'coreclr.dll', 'clrjit.dll')) {
    if (Test-Path -LiteralPath (Join-Path $directory $file)) {
        throw "Unexpected managed-runtime payload '$file'. Use a clean NativeAOT publish directory."
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'NativeAOT verification requires Visual Studio Build Tools with Desktop development with C++.'
}
$dumpbin = & $vswhere -latest -prerelease -products * -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe'
if ($LASTEXITCODE -ne 0 -or -not $dumpbin) {
    throw 'Could not locate dumpbin.exe in the installed C++ build tools.'
}
$dumpbin = @($dumpbin)[0]
$exports = & $dumpbin /nologo /exports $executable
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect native exports in $executable."
}
# An ordinary .NET apphost is also a native PE; require the NativeAOT runtime export.
if (-not ($exports -match '\sDotNetRuntimeDebugHeader(?:\s|$)')) {
    throw 'The published executable is not a NativeAOT image (DotNetRuntimeDebugHeader export is missing).'
}

Write-Output "PASS: NativeAOT executable with no managed app or JIT runtime payload: $executable"
