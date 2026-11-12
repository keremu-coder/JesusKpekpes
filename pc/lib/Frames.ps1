# Frames.ps1 -- record displayed frames with the pinned PresentMon build, then count them.
# Average, 1% low and 0.1% low are computed from that file. Nothing here is estimated.

function Get-KPresentMonPath {
    return (Join-Path (Split-Path -Parent $PSScriptRoot) 'presentmon\PresentMon-2.6.0-x64.exe')
}

function Test-KPresentMon {
    $path = Get-KPresentMonPath
    $want = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
    if (-not (Test-Path -LiteralPath $path)) { return 'PresentMon is not next to the program, so nothing was measured.' }
    $got = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($got -ne $want) { return 'PresentMon is not the file that was checked, so it was not run. Nothing was measured.' }
    return ''
}

function Invoke-KMeasure {
    param([string]$Side, [int]$Seconds = 60, [string]$ProcessName = 'cod.exe')
    $lines = New-Object System.Collections.Generic.List[string]
    if ($Side -ne 'Before' -and $Side -ne 'After') { $lines.Add('Say before or after. Nothing was measured.'); return $lines }
    if ($ProcessName -notmatch '^[A-Za-z0-9._-]+\.exe$') { $lines.Add('The program name has to look like cod.exe. Nothing was measured.'); return $lines }
    if ($Seconds -lt 30) { $Seconds = 30 }
    if ($Seconds -gt 120) { $Seconds = 120 }
    $bad = Test-KPresentMon
    if ($bad) { $lines.Add($bad); return $lines }
    if (-not (Initialize-KRoot)) { $lines.Add('The capture folder could not be created. Nothing was measured.'); return $lines }
    $dir = Join-Path $script:DataRoot 'captures'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $csv = Join-Path $dir ($Side.ToLower() + '.csv')
    if (Test-Path -LiteralPath $csv) { Remove-Item -LiteralPath $csv -Force }
    $arg = '--process_name ' + $ProcessName + ' --output_file "' + $csv + '" --timed ' + $Seconds + ' --terminate_after_timed --stop_existing_session --v1_metrics --exclude_dropped --no_console_stats'
    $proc = Start-Process -FilePath (Get-KPresentMonPath) -ArgumentList $arg -PassThru -WindowStyle Hidden
    if (-not $proc.WaitForExit(($Seconds + 45) * 1000)) {
        try { $proc.Kill() } catch { }
        $lines.Add('The recorder did not stop. Nothing was measured.')
        return $lines
    }
    try {
        $stats = Get-FrameStats (Import-FrameTimes $csv)
    } catch {
        $lines.Add([string]$_.Exception.Message)
        $lines.Add('Nothing was measured. Start ' + $ProcessName + ' and stay in a match for the whole minute.')
        return $lines
    }
    $lines.Add('Measured ' + $Side + ' from ' + $stats.Frames + ' displayed frames of ' + $ProcessName + '.')
    $lines.Add('Average FPS  ' + $stats.AvgFps)
    $lines.Add('1% low FPS   ' + $stats.Low1Fps)
    $lines.Add('0.1% low FPS ' + $stats.Low01Fps)
    $lines.Add('These numbers are from that capture only. They are not a guess.')
    $otherSide = if ($Side -eq 'Before') { 'after' } else { 'before' }
    $other = Join-Path $dir ($otherSide + '.csv')
    if (Test-Path -LiteralPath $other) {
        try {
            $cmp = Compare-FrameCaptures $(if ($Side -eq 'After') { $other } else { $csv }) $(if ($Side -eq 'After') { $csv } else { $other })
            $lines.Add('Before and after, from the two captures:')
            foreach ($row in @($cmp.Rows)) {
                if ([string]$row.Metric -match 'Average|1% low|0.1% low') {
                    $lines.Add([string]$row.Metric + '  before ' + $row.Before + '  after ' + $row.After + '  ' + $row.Change)
                }
            }
            $lines.Add('One capture on each side is a hint. It is not enough to prove the change.')
        } catch {
            $lines.Add('The other capture could not be read: ' + $_.Exception.Message)
        }
    } elseif ($Side -eq 'Before') {
        $lines.Add('Record after the same way, in the same kind of match, when you want the comparison.')
    }
    return $lines
}
