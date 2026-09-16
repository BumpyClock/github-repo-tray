param(
    [Parameter(Mandatory)]
    [string]$TracePath,

    [string[]]$IdleFootprintPath = @(),

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$events = @(Get-Content -LiteralPath $TracePath |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { $_ | ConvertFrom-Json })
$processIds = @($events.process_id | Sort-Object -Unique)
if ($processIds.Count -ne 1 -or $null -eq $processIds[0]) {
    throw 'The trace must contain events from exactly one process.'
}
$reopens = @($events | Where-Object {
    $_.event -eq 'first-render-callback' -and $_.show_kind -eq 'reopen'
})
if ($reopens.Count -eq 0) {
    throw 'The trace contains no completed reopen measurements. Initial launch is excluded.'
}
$reopenEvents = @($events | Where-Object { $_.show_kind -eq 'reopen' })
$attempts = @($reopenEvents.show_number | Sort-Object -Unique).Count
if (@($reopens.show_number | Sort-Object -Unique).Count -ne $reopens.Count) {
    throw 'The trace contains duplicate rendering measurements for a reopen.'
}

function Get-Distribution([object[]]$Values) {
    if ($Values.Count -eq 0) {
        throw 'A measurement distribution cannot be empty.'
    }
    foreach ($value in $Values) {
        if (($value -isnot [int] -and $value -isnot [long] -and
                $value -isnot [double] -and $value -isnot [decimal]) -or
            [double]::IsNaN([double]$value) -or
            [double]::IsInfinity([double]$value) -or $value -lt 0) {
            throw "Invalid nonnegative numeric measurement: '$value'."
        }
    }
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) {
        $sorted[$middle]
    } else {
        ($sorted[$middle - 1] + $sorted[$middle]) / 2
    }
    [pscustomobject]@{
        Samples = $sorted.Count
        Minimum = $sorted[0]
        Median = $median
        Maximum = $sorted[-1]
    }
}

$report = [ordered]@{
    ProcessId = $processIds[0]
    RecordedReopenAttempts = $attempts
    CompletedReopens = $reopens.Count
    ReopensWithoutRenderCallback = $attempts - $reopens.Count
    RenderTimeouts = @($reopenEvents | Where-Object { $_.event -eq 'render-timeout' }).Count
    PreparationMilliseconds = Get-Distribution @($reopens.preparation_ms)
    SynchronousShowMilliseconds = Get-Distribution @($reopens.synchronous_show_ms)
    FirstRenderCallbackMilliseconds = Get-Distribution @($reopens.first_render_callback_ms)
    LatencyDefinition = 'ShowPanel entry to the first WinUI rendering callback; not input-to-display or compositor-present latency.'
    InitialLaunchExcluded = $true
}

if ($IdleFootprintPath.Count -gt 0) {
    $measurements = @(
        foreach ($path in $IdleFootprintPath) {
            $idle = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if ($idle.ProcessId -ne $processIds[0]) {
                throw "Idle report '$path' belongs to a different process."
            }
            if (@($idle.Measurements).Count -eq 0) {
                throw "Idle report '$path' has no samples."
            }
            $idle.Measurements
        }
    )
    $report.HiddenPrivateWorkingSetBytes = Get-Distribution @($measurements.PrivateWorkingSetBytes)
    $report.HiddenWorkingSetBytes = Get-Distribution @($measurements.WorkingSetBytes)
    $report.HiddenPrivateBytes = Get-Distribution @($measurements.PrivateBytes)
}

$result = [pscustomobject]$report
if ($OutputPath) {
    $result | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $OutputPath -Encoding utf8
}
$result
