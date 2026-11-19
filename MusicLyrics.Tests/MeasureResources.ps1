param(
    [Parameter(Mandatory)] [int]$ProcessId,
    [ValidateRange(1, 3600)] [int]$Seconds = 300
)

$ErrorActionPreference = 'Stop'
$process = Get-Process -Id $ProcessId
$processorCount = [Environment]::ProcessorCount
$cpuStart = $process.TotalProcessorTime.TotalSeconds
$timer = [Diagnostics.Stopwatch]::StartNew()
$peak = 0L
$samples = 0
do {
    $private = Get-CimInstance -ClassName Win32_PerfRawData_PerfProc_Process -Filter "IDProcess = $ProcessId"
    if ($null -ne $private.WorkingSetPrivate) {
        $peak = [Math]::Max($peak, [long]$private.WorkingSetPrivate)
        $samples++
    }
    $process.Refresh()
    if ($process.HasExited) { throw "The measured process exited." }
    if ($timer.Elapsed.TotalSeconds -ge $Seconds) { break }
    Start-Sleep -Milliseconds 1000
} while ($true)

if ($samples -eq 0) { throw "No Windows private-working-set samples were available." }
$cpu = ($process.TotalProcessorTime.TotalSeconds - $cpuStart) / $timer.Elapsed.TotalSeconds / $processorCount * 100
[PSCustomObject]@{
    ProcessId = $ProcessId
    Seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
    LogicalProcessors = $processorCount
    AverageTotalCpuPercent = [Math]::Round($cpu, 4)
    PeakPrivateWorkingSetMB = [Math]::Round($peak / 1000000, 2)
    PrivateWorkingSetSamples = $samples
}
