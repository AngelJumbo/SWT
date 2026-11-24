<#
.SYNOPSIS
Measures fresh-process startup with a warm filesystem cache.
.DESCRIPTION
Runs the smoke workload without changing user configuration. Diagnostics add
overhead; first output is not necessarily a ready prompt. TotalMs includes exit
and cleanup. Successful samples' diagnostic logs are removed.
#>
[CmdletBinding()]
param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\SWT.Desktop\bin\Release\net10.0-windows\SWT.Desktop.exe'),
    [ValidateRange(1, 100)]
    [int]$Samples = 10
)

$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$logDirectory = Join-Path $env:LOCALAPPDATA 'SWT\logs'

# Discard one warmup; every sample still launches a fresh process.
foreach ($sample in 0..$Samples) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($executablePath)
    $start.UseShellExecute = $false
    $start.Arguments = '--smoke-test --diagnostics'
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($start)
    $log = $null
    try {
        $processStarted = [DateTimeOffset]$process.StartTime
        if (-not $process.WaitForExit(30000)) {
            Stop-Process -Id $process.Id -Force
            throw "Startup sample $sample timed out."
        }
        $watch.Stop()
        if ($process.ExitCode -ne 0) {
            throw "Startup sample $sample failed with exit code $($process.ExitCode)."
        }
        $log = @(Get-ChildItem -LiteralPath $logDirectory -Filter "diagnostics-*-$($process.Id).jsonl" |
            Where-Object LastWriteTimeUtc -GE $processStarted.UtcDateTime)
        if ($log.Count -ne 1) { throw "Expected one diagnostic log for process $($process.Id)." }
        $events = @(Get-Content -LiteralPath $log[0].FullName | ConvertFrom-Json)
        $milestones = @{}
        foreach ($reason in @('configuration-loaded', 'font-check-starting', 'font-checked', 'view-created',
            'constructed', 'loaded', 'content-rendered', 'conpty-starting', 'conpty-started', 'output-before')) {
            $entry = $events | Where-Object reason -EQ $reason | Select-Object -First 1
            if ($null -eq $entry) { throw "Startup sample $sample is missing '$reason'." }
            $milestones[$reason] = ([DateTimeOffset]$entry.timestamp - $processStarted).TotalMilliseconds
        }
        if ($sample -gt 0) {
            [pscustomobject]@{
                Sample = $sample
                ConfigurationMs = [Math]::Round($milestones['configuration-loaded'], 1)
                FontCheckMs = [Math]::Round($milestones['font-checked'] - $milestones['font-check-starting'], 1)
                ViewMs = [Math]::Round($milestones['view-created'] - $milestones['font-checked'], 1)
                ConstructedMs = [Math]::Round($milestones['constructed'], 1)
                LoadedMs = [Math]::Round($milestones['loaded'], 1)
                FirstRenderMs = [Math]::Round($milestones['content-rendered'], 1)
                ConPtyStartMs = [Math]::Round($milestones['conpty-started'] - $milestones['conpty-starting'], 1)
                FirstOutputMs = [Math]::Round($milestones['output-before'], 1)
                TotalMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
            }
        }
    }
    finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        if ($null -ne $log -and $log.Count -eq 1) {
            Remove-Item -LiteralPath $log[0].FullName
        }
        $process.Dispose()
    }
}
