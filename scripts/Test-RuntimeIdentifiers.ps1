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

foreach ($case in $cases) {
    # Ignored local publish profiles must not mask the behavior of a clean checkout.
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
