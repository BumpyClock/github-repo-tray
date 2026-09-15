param(
    [Parameter(Mandatory)]
    [ValidateRange(1, 2147483647)]
    [int]$AppPid,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [ValidateRange(3, 300)]
    [int]$Samples = 15,

    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 2,

    [string]$Label = 'hidden-idle'
)

$ErrorActionPreference = 'Stop'
$process = Get-Process -Id $AppPid
$startTime = $process.StartTime
$executable = $process.Path
$initialCpu = $process.TotalProcessorTime.TotalSeconds
$elapsed = [Diagnostics.Stopwatch]::StartNew()
$measurements = @(
    for ($index = 0; $index -lt $Samples; $index++) {
        if ($index -gt 0) {
            Start-Sleep -Seconds $IntervalSeconds
        }
        $process = Get-Process -Id $AppPid
        if ($process.StartTime -ne $startTime) {
            throw "Process $AppPid was replaced during measurement."
        }
        $counters = Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess = $AppPid"
        if ($null -eq $counters) {
            throw "Performance counters are unavailable for process $AppPid."
        }
        [pscustomobject]@{
            TimestampUtc = [DateTimeOffset]::UtcNow
            PrivateWorkingSetBytes = [long]$counters.WorkingSetPrivate
            WorkingSetBytes = [long]$counters.WorkingSet
            PrivateBytes = [long]$counters.PrivateBytes
            CpuSeconds = $process.TotalProcessorTime.TotalSeconds
            Handles = [int]$counters.HandleCount
            Threads = [int]$counters.ThreadCount
        }
    }
)
$elapsed.Stop()

function Get-Distribution([string]$Property) {
    $sorted = @($measurements.$Property | Sort-Object)
    $midpoint = [int][Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) {
        $sorted[$midpoint]
    } else {
        ($sorted[$midpoint - 1] + $sorted[$midpoint]) / 2
    }
    [pscustomobject]@{
        Minimum = $sorted[0]
        Median = $median
        Maximum = $sorted[-1]
    }
}

$report = [pscustomobject]@{
    Label = $Label
    ProcessId = $AppPid
    Executable = $executable
    ProcessStartTime = $startTime
    Samples = $Samples
    IntervalSeconds = $IntervalSeconds
    DurationSeconds = $elapsed.Elapsed.TotalSeconds
    CpuSeconds = $measurements[-1].CpuSeconds - $initialCpu
    PrivateWorkingSetBytes = Get-Distribution 'PrivateWorkingSetBytes'
    WorkingSetBytes = Get-Distribution 'WorkingSetBytes'
    PrivateBytes = Get-Distribution 'PrivateBytes'
    Measurements = $measurements
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$report | Select-Object Label, ProcessId, Samples, DurationSeconds, CpuSeconds,
    PrivateWorkingSetBytes, WorkingSetBytes, PrivateBytes | Format-List
