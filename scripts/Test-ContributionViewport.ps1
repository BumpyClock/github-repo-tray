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
    $output = & winapp ui @Arguments -a "$AppPid" 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
}

function Find-Control {
    param([string]$Id)
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)
    $idCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $deadline = [datetime]::UtcNow.AddSeconds(5)
    do {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($window in $windows) {
            $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCondition)
            if ($null -ne $control) { return $control }
        }
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
    Invoke-UI -Arguments @('set-value', 'ContributionCellSizeSelector', $Size)
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
$graph = Find-Control 'ContributionGraph'
$viewport = Find-Control 'ContributionViewport'
$originalBounds = $viewport.Current.BoundingRectangle
Invoke-UI -Arguments @('invoke', 'SettingsButton')
$selector = Find-Control 'ContributionCellSizeSelector'
$originalSize = $selector.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).GetCurrentSelection()[0].Current.Name
Invoke-UI -Arguments @('invoke', 'SettingsBackButton')
$originalInterval = if ($SettingsPath -and (Test-Path -LiteralPath $SettingsPath)) {
    (Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json).RefreshMinutes
} else { $null }
$heights = @{}

try {
    foreach ($size in @('Small', 'Medium', 'Large')) {
        Test-Viewport "$size has crisp square cells and seven visible rows" {
            Set-CellSize $size
            $bounds = $viewport.Current.BoundingRectangle
            $heights[$size] = $bounds.Height
            Assert-True ($bounds.Width -eq $originalBounds.Width) 'Changing size changed the available width.'
            $days = @($graph.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.AutomationId -match '^ContributionDay_\d{4}-\d{2}-\d{2}$' })
            Assert-True ($days.Count -gt 7) 'A loaded contribution calendar is required.'
            $rows = @($days | Where-Object {
                $rect = $_.Current.BoundingRectangle
                $rect.Left -ge $bounds.Left -and $rect.Right -le $bounds.Right
            } | ForEach-Object {
                $rect = $_.Current.BoundingRectangle
                Assert-True ($rect.Top -ge $bounds.Top - 1 -and $rect.Bottom -le $bounds.Bottom + 1) 'A weekday row is clipped.'
                Assert-True ([math]::Abs($rect.Width - $rect.Height) -lt 0.01) 'A cell is not square.'
                Assert-True ([math]::Abs($rect.Width - [math]::Round($rect.Width)) -lt 0.01) 'Cell width is not aligned to device pixels.'
                [datetime]::ParseExact($_.Current.AutomationId.Substring(16), 'yyyy-MM-dd',
                    [Globalization.CultureInfo]::InvariantCulture).DayOfWeek
            } | Select-Object -Unique)
            Assert-True ($rows.Count -eq 7) 'Not all weekday rows are visible.'
            $scroll = $viewport.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($size -eq 'Medium') {
                Assert-True ($scroll.Current.HorizontalViewSize -ge 99.5) 'Medium does not fit the full year.'
            } elseif ($size -eq 'Large') {
                Assert-True ($scroll.Current.HorizontalViewSize -lt 99) 'Large does not expose horizontal history.'
            }
            Invoke-UI -Arguments @('screenshot', 'ContributionGraph', '-o', (Join-Path $OutputDirectory "$size.png"))
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
