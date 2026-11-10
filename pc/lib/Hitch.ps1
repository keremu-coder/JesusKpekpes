# Hitch.ps1 -- name the driver or process behind the longest stall. Changes nothing.
# No journal entry. Nothing is disabled. If Windows will not give the trace, say so and stop.

$script:HitchDir = $PSScriptRoot
$script:HitchTypeReady = $false
$script:HitchTypeError = ''

function Get-KHitchLongest {
    param($Hits)
    $best = $null
    foreach ($h in @($Hits)) {
        if ($null -eq $h) { continue }
        $name = ''
        $kind = ''
        $us = 0L
        try { $name = ([string]$h.Name).Trim() } catch { continue }
        try { $kind = [string]$h.Kind } catch { $kind = '' }
        try { $us = [int64]$h.Us } catch { continue }
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($us -le 0) { continue }
        if ($null -eq $best -or $us -gt $best.Us) {
            $best = [pscustomobject]@{ Name = $name; Kind = $kind; Us = $us }
        }
    }
    return $best
}

function Get-KHitchTop {
    param($Hits, [int]$Count = 5)
    $rows = @()
    $seen = @{}
    $ordered = @($Hits | Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string]$_.Name) } | Sort-Object @{ Expression = { try { [int64]$_.Us } catch { 0 } }; Descending = $true })
    foreach ($h in $ordered) {
        $us = 0L
        try { $us = [int64]$h.Us } catch { continue }
        if ($us -le 0) { continue }
        $name = ([string]$h.Name).Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $kind = [string]$h.Kind
        $key = $kind + '|' + $name
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $rows += [pscustomobject]@{ Name = $name; Kind = $kind; Us = $us }
        if ($rows.Count -ge $Count) { break }
    }
    return @($rows)
}

function Get-KHitchKindText {
    param([string]$Kind)
    if ($Kind -eq 'DPC') { return 'delayed call (DPC)' }
    if ($Kind -eq 'ISR') { return 'interrupt (ISR)' }
    if ($Kind -eq 'Wake') { return 'thread wake' }
    if ($Kind) { return $Kind }
    return 'stall'
}

function Test-KHitchWindowsItself {
    param([string]$Name, [string]$Kind)
    $n = $Name.ToLowerInvariant()
    if ($n -eq 'ntoskrnl.exe' -or $n -eq 'ntkrnlmp.exe' -or $n -eq 'ntkrpamp.exe' -or $n -eq 'hal.dll') { return $true }
    if ($Kind -eq 'Wake' -and ($n -eq 'system' -or $n -eq 'idle' -or $n -eq 'memory compression' -or $n -eq 'registry')) { return $true }
    return $false
}

function Format-KHitchReport {
    param($Hits, [string]$ErrorText = '', [int]$Dropped = 0, [int]$Seen = 0, [int]$BadTime = 0)
    if ($ErrorText -and $ErrorText.Trim().Length -gt 0) {
        $e = $ErrorText.Trim()
        if ($e.EndsWith('.')) { $e = $e.Substring(0, $e.Length - 1) }
        return ($e + '. Nothing was changed.')
    }
    $best = Get-KHitchLongest $Hits
    if ($null -eq $best) {
        if ($Seen -gt 0) { return 'Windows recorded stalls, but none of them named a loaded driver. Nothing was changed.' }
        if ($BadTime -gt 0) { return 'Windows gave a trace, but the times were not usable. Nothing was changed.' }
        return 'Windows gave no delayed call, interrupt, or thread wake that could be named. Nothing was changed.'
    }
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('Hitch watch. Nothing was changed.')
    [void]$lines.Add('')
    [void]$lines.Add(('Longest stall: {0} microseconds   {1}   {2}' -f $best.Us, (Get-KHitchKindText $best.Kind), $best.Name))
    $also = @()
    foreach ($row in @(Get-KHitchTop $Hits 6)) {
        if ($row.Name -eq $best.Name -and $row.Kind -eq $best.Kind) { continue }
        $also += $row
    }
    if ($also.Count -gt 0) {
        [void]$lines.Add('')
        [void]$lines.Add('Also seen:')
        foreach ($row in $also) {
            [void]$lines.Add(('  {0} microseconds   {1}   {2}' -f $row.Us, (Get-KHitchKindText $row.Kind), $row.Name))
        }
    }
    [void]$lines.Add('')
    [void]$lines.Add('A name does not add frames. It only says where to look.')
    if (Test-KHitchWindowsItself $best.Name $best.Kind) {
        [void]$lines.Add('This is Windows itself. Leave it.')
    } elseif ($best.Kind -eq 'Wake') {
        [void]$lines.Add('This is a process that was slow to start running after it was woken. It is not a driver, and it was not stopped.')
    } else {
        [void]$lines.Add('If a hitch remains, update, roll back, or remove that one driver. This watch does not turn it off.')
    }
    [void]$lines.Add('Thread waits longer than 50 ms were left out.')
    if ($Dropped -gt 0) {
        [void]$lines.Add('Windows dropped some events. The name is still the longest one that was recorded.')
    }
    return ($lines -join "`r`n")
}

function Initialize-HitchType {
    if ($script:HitchTypeReady) { return $true }
    if (-not ('Kpekpes.KpekpesHitch' -as [type])) {
        $src = Join-Path $script:HitchDir 'HitchWatch.cs'
        if (-not (Test-Path -LiteralPath $src)) {
            $script:HitchTypeError = 'The hitch watch is missing a file. Nothing was changed.'
            return $false
        }
        try { Add-Type -Path $src -ErrorAction Stop }
        catch {
            $script:HitchTypeError = 'The hitch watch could not be compiled. Nothing was changed.'
            Write-KLog ('hitch watch compile: ' + $_.Exception.Message) 'WARN'
            return $false
        }
    }
    $script:HitchTypeReady = $true
    return $true
}

function Invoke-KHitchWatch {
    param([int]$Seconds = 20)
    Write-Host ''
    Write-Host '  Play for 20 seconds. This only watches. Nothing is changed.' -ForegroundColor Cyan
    try { [Console]::Out.Flush() } catch { }
    if (-not [Environment]::Is64BitProcess) {
        Write-Host '  This PowerShell is 32-bit. The hitch watch needs 64-bit Windows PowerShell. Nothing was changed.' -ForegroundColor Yellow
        return
    }
    if (-not (Initialize-HitchType)) {
        Write-Host ('  ' + $script:HitchTypeError) -ForegroundColor Yellow
        return
    }
    $r = $null
    try { $r = [Kpekpes.KpekpesHitch]::Watch($Seconds) }
    catch {
        Write-Host ('  The hitch watch stopped. ' + $_.Exception.Message + ' Nothing was changed.') -ForegroundColor Yellow
        return
    }
    $err = ''
    $hits = $null
    $dropped = 0
    $seen = 0
    $bad = 0
    if ($null -eq $r -or -not $r.Ok) {
        if ($null -ne $r) { $err = [string]$r.Error }
        if (-not $err) { $err = 'Windows would not give the trace.' }
    } else {
        $hits = $r.Hits
        try { $dropped = [int]$r.Dropped } catch { $dropped = 0 }
        try { $seen = [int]$r.Seen } catch { $seen = 0 }
        try { $bad = [int]$r.BadTime } catch { $bad = 0 }
    }
    $text = Format-KHitchReport -Hits $hits -ErrorText $err -Dropped $dropped -Seen $seen -BadTime $bad
    Write-Host ''
    foreach ($line in ($text -split '\r?\n')) { Write-Host ('  ' + $line) }
}
