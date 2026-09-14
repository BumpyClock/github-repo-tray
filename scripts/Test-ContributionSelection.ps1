param(
    [Parameter(Mandatory)][int]$AppPid,
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) "github-tray-selection-$AppPid")
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this script with powershell.exe to use the Windows UI Automation assemblies.'
}
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'MTA') {
    throw 'Run powershell.exe with -Mta so UI Automation event callbacks do not require a UI message loop.'
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase, System -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Automation;

public sealed class ContributionSelectionEvents : IDisposable
{
    private readonly AutomationElement graph;
    private readonly AutomationElement description;
    private readonly AutomationPropertyChangedEventHandler valueHandler;
    private readonly AutomationEventHandler liveHandler;
    private readonly ConcurrentQueue<string> values = new ConcurrentQueue<string>();
    private int liveCount;

    public ContributionSelectionEvents(AutomationElement graph, AutomationElement description)
    {
        this.graph = graph;
        this.description = description;
        valueHandler = (sender, args) => values.Enqueue((string)args.NewValue);
        liveHandler = (sender, args) => Interlocked.Increment(ref liveCount);
        Automation.AddAutomationPropertyChangedEventHandler(
            graph, TreeScope.Element, valueHandler, ValuePattern.ValueProperty);
        Automation.AddAutomationEventHandler(
            AutomationElementIdentifiers.LiveRegionChangedEvent,
            description, TreeScope.Element, liveHandler);
    }

    public string[] Values { get { return values.ToArray(); } }
    public int LiveCount { get { return Volatile.Read(ref liveCount); } }

    public void Dispose()
    {
        Automation.RemoveAutomationPropertyChangedEventHandler(graph, valueHandler);
        Automation.RemoveAutomationEventHandler(
            AutomationElementIdentifiers.LiveRegionChangedEvent, description, liveHandler);
    }
}
'@

function Invoke-WinApp {
    param([string[]]$Arguments)
    $output = & winapp @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ($output -join [Environment]::NewLine)
    }
}

function Find-Element {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$AutomationId)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) {
        throw "Missing element: $AutomationId. Open the tray panel and load its contribution calendar."
    }
    return $element
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$process = Get-Process -Id $AppPid
if ($process.MainWindowHandle -eq 0) {
    throw 'The tray panel must be open before running this test.'
}
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$graph = Find-Element $window 'ContributionGraph'
$description = Find-Element $graph 'ContributionSelectedDay'
$valuePattern = $graph.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$days = @($graph.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition) |
    Where-Object { $_.Current.AutomationId -match '^ContributionDay_\d{4}-\d{2}-\d{2}$' } |
    Sort-Object { $_.Current.AutomationId })
if ($days.Count -lt 16) {
    throw 'At least sixteen contribution days are required for the navigation fixture.'
}

function Get-DayDate {
    param([int]$Index)
    return [datetime]::ParseExact(
        $days[$Index].Current.AutomationId.Substring('ContributionDay_'.Length),
        'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
}

$initialValue = $valuePattern.Current.Value
$initialDay = @($days | Where-Object {
    $date = [datetime]::ParseExact($_.Current.AutomationId.Substring('ContributionDay_'.Length),
        'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $initialValue.Contains($date.ToString('ddd, d MMM yyyy'))
}) | Select-Object -First 1

$results = [Collections.Generic.List[object]]::new()
$events = [ContributionSelectionEvents]::new($graph, $description)

function Test-Selection {
    param([string]$Name, [scriptblock]$Action, [int]$ExpectedIndex, [bool]$Changed)
    $oldValues = $events.Values.Length
    $oldLive = $events.LiveCount
    try {
        & $Action
        $date = (Get-DayDate $ExpectedIndex).ToString('ddd, d MMM yyyy')
        $deadline = [datetime]::UtcNow.AddSeconds(3)
        do {
            $value = $valuePattern.Current.Value
            $ready = $value.Contains($date) -and
                (-not $Changed -or ($events.Values.Length -gt $oldValues -and $events.LiveCount -gt $oldLive))
            if ($ready) { break }
            Start-Sleep -Milliseconds 50
        } while ([datetime]::UtcNow -lt $deadline)
        Start-Sleep -Milliseconds 250
        if (-not $value.Contains($date)) { throw "Expected selected date $date; got $value." }
        if (-not $graph.Current.HasKeyboardFocus) {
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            throw "Heatmap focus changed to process $($focused.Current.ProcessId), element '$($focused.Current.AutomationId)'."
        }
        $expectedEvents = [int]$Changed
        $valueEvents = $events.Values.Length - $oldValues
        $liveEvents = $events.LiveCount - $oldLive
        if ($valueEvents -ne $expectedEvents -or $liveEvents -ne $expectedEvents) {
            throw "Expected $expectedEvents value/live events; got $valueEvents/$liveEvents."
        }
        if ($Changed -and $events.Values[-1] -ne $value) {
            throw 'The value notification did not contain the final selected description.'
        }
        if ($description.Current.Name -ne $value) {
            throw 'The bound day description and accessible value diverged.'
        }
        $results.Add([pscustomobject]@{ name = $Name; status = 'PASS' })
    }
    catch {
        $results.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; detail = $_.Exception.Message })
    }
}

try {
    Invoke-WinApp -Arguments @('ui', 'send-keys', 'home', '--target', 'ContributionGraph',
        '-a', "$AppPid", '--via', 'send-input')
    Start-Sleep -Milliseconds 400
    Test-Selection 'Home boundary is silent' {
        Invoke-WinApp -Arguments @('ui', 'send-keys', 'home', '--target', 'ContributionGraph', '-a', "$AppPid", '--via', 'send-input')
    } 0 $false
    Test-Selection 'Down selects the next day and announces once' {
        Invoke-WinApp -Arguments @('ui', 'send-keys', 'down', '--target', 'ContributionGraph', '-a', "$AppPid", '--via', 'send-input')
    } 1 $true
    Test-Selection 'Right moves one calendar week' {
        Invoke-WinApp -Arguments @('ui', 'send-keys', 'right', '--target', 'ContributionGraph', '-a', "$AppPid", '--via', 'send-input')
    } 8 $true
    Test-Selection 'Up selects the previous day' {
        Invoke-WinApp -Arguments @('ui', 'send-keys', 'up', '--target', 'ContributionGraph', '-a', "$AppPid", '--via', 'send-input')
    } 7 $true
    Test-Selection 'Left moves one calendar week' {
        Invoke-WinApp -Arguments @('ui', 'send-keys', 'left', '--target', 'ContributionGraph', '-a', "$AppPid", '--via', 'send-input')
    } 0 $true
    Invoke-WinApp -Arguments @('ui', 'screenshot', 'ContributionGraph', '-a', "$AppPid",
        '-o', (Join-Path $OutputDirectory 'selection-first.png'))
    Test-Selection 'End selects the latest day' {
        Invoke-WinApp -Arguments @('ui', 'send-keys', 'end', '--target', 'ContributionGraph', '-a', "$AppPid", '--via', 'send-input')
    } ($days.Count - 1) $true
    Test-Selection 'End boundary is silent' {
        Invoke-WinApp -Arguments @('ui', 'send-keys', 'down right end', '--target', 'ContributionGraph', '-a', "$AppPid", '--via', 'send-input')
    } ($days.Count - 1) $false
    Test-Selection 'Pointer selects a day and retains heatmap focus' {
        Invoke-WinApp -Arguments @('ui', 'click', $days[10].Current.AutomationId, '-a', "$AppPid")
    } 10 $true
    Test-Selection 'Repeated pointer selection is silent' {
        Invoke-WinApp -Arguments @('ui', 'click', $days[10].Current.AutomationId, '-a', "$AppPid")
    } 10 $false
    Invoke-WinApp -Arguments @('ui', 'screenshot', 'ContributionGraph', '-a', "$AppPid",
        '-o', (Join-Path $OutputDirectory 'selection-pointer.png'))
    Invoke-WinApp -Arguments @('ui', 'send-keys', 'tab', '--target', 'ContributionGraph', '-a', "$AppPid", '--via', 'send-input')
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $focusPassed = $focused.Current.ProcessId -eq $AppPid -and
        $focused.Current.AutomationId -ne 'ContributionGraph' -and
        $focused.Current.AutomationId -notlike 'ContributionDay_*'
    $results.Add([pscustomobject]@{
        name = 'Tab leaves the heatmap rather than visiting day cells'
        status = $(if ($focusPassed) { 'PASS' } else { 'FAIL' })
    })
}
catch {
    $results.Add([pscustomobject]@{
        name = 'Native test execution'
        status = 'FAIL'
        detail = $_.Exception.Message
    })
}
finally {
    $events.Dispose()
    if ($null -ne $initialDay) {
        try {
            Invoke-WinApp -Arguments @('ui', 'click', $initialDay.Current.AutomationId, '-a', "$AppPid")
        }
        catch {
            $results.Add([pscustomobject]@{
                name = 'Restore initial selected day'
                status = 'FAIL'
                detail = $_.Exception.Message
            })
        }
    }
    $results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'results.json') -Encoding UTF8
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' })
$results | Format-Table name, status, detail -AutoSize
if ($failed.Count -gt 0) {
    throw "$($failed.Count) contribution-selection checks failed. See $OutputDirectory."
}
