param(
    [string]$Project = (Join-Path $PSScriptRoot '..\src\GitHubTray.App\GitHubTray.App.csproj')
)

$ErrorActionPreference = 'Stop'
$cases = @(
    @{ Platform = 'x86'; Expected = 'win-x86' },
    @{ Platform = 'x64'; Expected = 'win-x64' },
    @{ Platform = 'ARM64'; Expected = 'win-arm64' },
    @{ Platform = 'arm64'; Expected = 'win-arm64' },
    @{ Platform = 'ARM64'; Runtime = 'win-x64'; Expected = 'win-x64' },
    @{ Platform = 'x64'; Runtime = 'win-arm64'; Expected = 'win-arm64' }
)

$runtimeIdentifiers = (& dotnet msbuild $Project -nologo -getProperty:RuntimeIdentifiers | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'RuntimeIdentifiers evaluation failed.'
}
if ($runtimeIdentifiers -cne 'win-x86;win-x64;win-arm64') {
    throw "Expected restore support for all package architectures, received '$runtimeIdentifiers'."
}
Write-Output "PASS: Restore includes all package runtime identifiers."

foreach ($case in $cases) {
    # Check the project's RID defaults independently of publish profiles.
    $arguments = @(
        'msbuild', $Project, '-nologo', '-p:PublishProfile=',
        "-p:Platform=$($case.Platform)", '-getProperty:RuntimeIdentifier'
    )
    if ($case.Runtime) {
        $arguments += "-p:RuntimeIdentifier=$($case.Runtime)"
    }
    $actual = (& dotnet @arguments | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime evaluation failed for Platform=$($case.Platform)."
    }
    if ($actual -cne $case.Expected) {
        throw "Platform=$($case.Platform): expected '$($case.Expected)', received '$actual'."
    }
    Write-Output "PASS: Platform=$($case.Platform), explicit RID=$($case.Runtime), resolved RID=$actual"
}

foreach ($architecture in @('x64', 'x86', 'arm64')) {
    $json = & dotnet msbuild $Project -nologo "-p:PublishProfile=win-$architecture" `
        -getProperty:RuntimeIdentifier,Platform,Configuration,PublishAot,SelfContained,PublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "Publish profile evaluation failed for win-$architecture."
    }
    $properties = ($json | Out-String | ConvertFrom-Json).Properties
    if ($properties.RuntimeIdentifier -cne "win-$architecture" -or
        $properties.Platform -ine $architecture -or
        $properties.Configuration -ne 'Release' -or
        $properties.PublishAot -ne 'true' -or
        $properties.SelfContained -ne 'true' -or
        -not $properties.PublishDir.EndsWith("\win-$architecture\")) {
        throw "Incorrect NativeAOT publish profile settings: $($properties | ConvertTo-Json -Compress)"
    }
    Write-Output "PASS: win-$architecture profile selects Release NativeAOT and its own output directory."
}

$debug = & dotnet msbuild $Project -nologo -p:Configuration=Debug -p:Platform=x64 -getProperty:PublishProfile,PublishAot
if ($LASTEXITCODE -ne 0) {
    throw 'Debug configuration evaluation failed.'
}
$properties = ($debug | Out-String | ConvertFrom-Json).Properties
if ($properties.PublishProfile -or $properties.PublishAot -eq 'true') {
    throw 'A release publish profile unexpectedly changed the Debug workflow.'
}
Write-Output 'PASS: Debug does not automatically load a release publish profile or enable NativeAOT.'
