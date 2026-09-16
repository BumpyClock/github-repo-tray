param(
    [Parameter(Mandatory)][int]$AppPid,
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) "github-tray-viewport-$AppPid"),
    [string]$SettingsPath
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this script with powershell.exe to use the Windows UI Automation assemblies.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase

function Invoke-UI {
    param([string[]]$Arguments)
    $output = & winapp ui @Arguments -w "$windowHandle" 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
}

function Find-Control {
    param([string]$Id)
    $idCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $deadline = [datetime]::UtcNow.AddSeconds(5)
    do {
        $control = $windowRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCondition)
        if ($null -ne $control) { return $control }
        Start-Sleep -Milliseconds 100
    } while ([datetime]::UtcNow -lt $deadline)
    throw "Missing control: $Id. The panel must be open."
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Set-CellSize {
    param([string]$Size)
    Invoke-UI -Arguments @('invoke', 'SettingsButton')
    $null = Find-Control 'ContributionCellSizeSelector'
    $keys = switch ($Size) {
        'Small' { 'home' }
        'Medium' { 'home down' }
        'Large' { 'end' }
        default { throw "Unexpected cell size: $Size" }
    }
    Invoke-UI -Arguments @('send-keys', $keys, '--target', 'ContributionCellSizeSelector', '--via', 'send-input')
    Invoke-UI -Arguments @('wait-for', 'ContributionCellSizeSelector', '--value', $Size, '-t', '5000')
    Invoke-UI -Arguments @('invoke', 'SettingsBackButton')
    $null = Find-Control 'ContributionViewport'
    Start-Sleep -Milliseconds 350
}

$results = [Collections.Generic.List[object]]::new()
function Test-Viewport {
    param([string]$Name, [scriptblock]$Action)
    try {
        & $Action
        $results.Add([pscustomobject]@{ name = $Name; status = 'PASS' })
    }
    catch {
        $results.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; detail = $_.Exception.Message })
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$windowHandle = (Get-Process -Id $AppPid).MainWindowHandle
if ($windowHandle -eq 0) { throw 'Open the tray panel before running these checks.' }
$windowRoot = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
$graph = Find-Control 'ContributionGraph'
$viewport = Find-Control 'ContributionViewport'
$originalBounds = $viewport.Current.BoundingRectangle
Invoke-UI -Arguments @('invoke', 'SettingsButton')
$selector = Find-Control 'ContributionCellSizeSelector'
$originalSize = $selector.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name
Invoke-UI -Arguments @('invoke', 'SettingsBackButton')
$originalInterval = if ($SettingsPath -and (Test-Path -LiteralPath $SettingsPath)) {
    (Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json).RefreshMinutes
} else { $null }
$heights = @{}

try {
    foreach ($size in @('Small', 'Medium', 'Large')) {
        Test-Viewport "$size has square cell bounds within UIA rounding and seven visible rows" {
            Set-CellSize $size
            Invoke-UI -Arguments @('screenshot', 'ContributionGraph', '-o', (Join-Path $OutputDirectory "$size.png"))
            $bounds = $viewport.Current.BoundingRectangle
            $heights[$size] = $bounds.Height
            Assert-True ($bounds.Width -eq $originalBounds.Width) 'Changing size changed the available width.'
            $days = @($graph.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.AutomationId -match '^ContributionDay_\d{4}-\d{2}-\d{2}$' })
            Assert-True ($days.Count -gt 7) 'A loaded contribution calendar is required.'
            $rows = @($days | Where-Object {
                $rect = $_.Current.BoundingRectangle
                -not $_.Current.IsOffscreen -and $rect.Width -gt 0 -and
                    $rect.Left -gt $bounds.Left -and $rect.Right -lt $bounds.Right
            } | ForEach-Object {
                $rect = $_.Current.BoundingRectangle
                Assert-True ($rect.Top -ge $bounds.Top - 1 -and $rect.Bottom -le $bounds.Bottom + 1) 'A weekday row is clipped.'
                # UIA truncates transformed bounds independently: an 8x8 rendered cell can report 7x8.
                Assert-True ([math]::Abs($rect.Width - $rect.Height) -le 1) "Cell $($_.Current.AutomationId) exceeds UIA rounding: $($rect.Width) x $($rect.Height) pixels."
                Assert-True ([math]::Abs($rect.Width - [math]::Round($rect.Width)) -lt 0.01) 'Cell width is not aligned to device pixels.'
                [datetime]::ParseExact($_.Current.AutomationId.Substring(16), 'yyyy-MM-dd',
                    [Globalization.CultureInfo]::InvariantCulture).DayOfWeek
            } | Select-Object -Unique)
            Assert-True ($rows.Count -eq 7) 'Not all weekday rows are visible.'
            $scroll = $viewport.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($size -eq 'Small') {
                Assert-True ($scroll.Current.HorizontalViewSize -ge 99.5) 'Small does not fit the full year.'
            } else {
                Assert-True ($scroll.Current.HorizontalViewSize -lt 99) "$size does not expose horizontal history."
                Assert-True ($scroll.Current.HorizontalScrollPercent -gt 99) "$size did not open at the present edge."
            }
        }
    }
    Test-Viewport 'The container shrinks with the selected cell size' {
        Assert-True ($heights['Small'] -lt $heights['Medium']) 'Small did not shrink the container.'
        Assert-True ($heights['Medium'] -lt $heights['Large']) 'Medium did not shrink the container.'
    }
    Test-Viewport 'Horizontal history uses native scrolling' {
        $scroll = $viewport.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $scroll.SetScrollPercent(0, [System.Windows.Automation.ScrollPattern]::NoScroll)
        Start-Sleep -Milliseconds 250
        Assert-True ($scroll.Current.HorizontalScrollPercent -lt 1) 'Could not scroll into history.'
        $scroll.SetScrollPercent(100, [System.Windows.Automation.ScrollPattern]::NoScroll)
        Start-Sleep -Milliseconds 250
        Assert-True ($scroll.Current.HorizontalScrollPercent -gt 99) 'Could not return to the present edge.'
    }
    if ($SettingsPath) {
        Test-Viewport 'Cell size persists without changing the refresh interval' {
            $saved = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
            Assert-True ($saved.ContributionCellSize -eq 'Large') 'Large was not persisted.'
            if ($null -ne $originalInterval) {
                Assert-True ($saved.RefreshMinutes -eq $originalInterval) 'Saving the cell size changed the refresh interval.'
            }
        }
    }
}
finally {
    Test-Viewport 'Restore the initial cell-size preference' {
        Set-CellSize $originalSize
    }
    $results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'results.json') -Encoding UTF8
}
$results | Format-Table name, status, detail -AutoSize
if (@($results | Where-Object status -eq 'FAIL').Count -gt 0) { exit 1 }
