# NvidiaGlobal.ps1 -- Shader cache size = Unlimited, and Vertical Sync = Off, in NVIDIA's GLOBAL profile.
# Needs no NVIDIA app or control panel, only the driver. Uses NvidiaGlobal.cs (separate from NvidiaPower.cs).
# Nothing is saved unless every value reads back exactly. The old value is journaled first, so Undo puts it back:
# a value the global profile already had is restored, a value it did not have is removed again.
# Call of Duty's own V-Sync is a separate setting in the game's options file and is handled there.

$script:NvidiaGlobalDir = $PSScriptRoot
$script:NvidiaGlobalTypeError = ''

function Get-NvidiaGlobalWanted {
    # Id and value are from NVIDIA's NvApiDriverSettings.h. Default is what the driver uses when the profile has no entry.
    return @(
        @{ Id = 0x00198FFF; Name = 'Shader cache'; Want = 1; Default = 1; WantText = 'On' },
        @{ Id = 0x00AC8497; Name = 'Shader cache size'; Want = -1; Default = 0x4000; WantText = 'Unlimited' },
        @{ Id = 0x00A879CF; Name = 'Vertical sync'; Want = 0x08416747; Default = 0x60925292; WantText = 'Off' }
    )
}

function Get-NvidiaGlobalNeed {
    # Returns 'skip' (already right), 'write' (change it) or 'unreadable' (do not touch anything).
    param([int]$Status, [int]$Value, [int]$Want, [int]$Default)
    if ($Status -eq 0) {
        if ($Value -eq $Want) { return 'skip' }
        return 'write'
    }
    if ($Status -eq -160) {
        if ($Default -eq $Want) { return 'skip' }
        return 'write'
    }
    return 'unreadable'
}

function Test-NvidiaGlobalSaved {
    param([int]$Status)
    return ($Status -eq 0 -or $Status -eq -157)
}

function Initialize-NvidiaGlobalType {
    if ('Kpekpes.KpekpesNvidiaGlobal' -as [type]) { return $true }
    $src = Join-Path $script:NvidiaGlobalDir 'NvidiaGlobal.cs'
    if (-not (Test-Path -LiteralPath $src)) {
        $script:NvidiaGlobalTypeError = 'The NVIDIA settings helper file is missing.'
        return $false
    }
    try { Add-Type -Path $src -ErrorAction Stop }
    catch {
        $script:NvidiaGlobalTypeError = 'The NVIDIA settings helper could not be compiled.'
        Write-KLog ('NVIDIA global helper compile: ' + $_.Exception.Message) 'WARN'
        return $false
    }
    return $true
}

function Add-NvidiaGlobalJournal {
    param([int]$Id, [string]$Name, [bool]$HadOwn, $Old)
    $data = @{ Type = 'nvglobal'; SettingId = $Id; Name = $Name; HadOwn = $HadOwn }
    if ($HadOwn) { $data['Old'] = [int]$Old }
    $prev = $script:KGroup
    $script:KGroup = 'NVIDIA'
    [void](Add-KEntry ('nvglobal|' + [string]$Id) $data)
    $script:KGroup = $prev
}

function Invoke-NvidiaGlobalTune {
    if (-not (Test-HasNvidia)) {
        Write-Say '  no NVIDIA GPU found' 'DarkGray'
        return $true
    }
    if ($script:WhatIfMode) {
        Write-Say '  would set Shader cache size = Unlimited and Vertical sync = Off in the NVIDIA global profile' 'Gray'
        return $true
    }
    if (-not [Environment]::Is64BitProcess) {
        Write-Say '  This PowerShell is 32-bit, so NVIDIA''s interface cannot be used. Shader cache and V-Sync were not changed.' 'Yellow'
        return $false
    }
    if (-not (Initialize-NvidiaGlobalType)) {
        Write-Say ('  ' + $script:NvidiaGlobalTypeError + ' Shader cache and V-Sync were not changed.') 'Yellow'
        return $false
    }
    $drs = [Kpekpes.KpekpesNvidiaGlobal]::Open()
    if (-not $drs.Ok) {
        $why = [string]$drs.Error
        if (-not $why) { $why = 'NVIDIA''s interface did not open. Nothing was written.' }
        Write-Say ('  ' + $why) 'Yellow'
        try { $drs.Dispose() } catch { }
        return $false
    }
    try {
        $plan = New-Object System.Collections.ArrayList
        foreach ($w in @(Get-NvidiaGlobalWanted)) {
            $r = $drs.Read([uint32]$w.Id)
            $need = Get-NvidiaGlobalNeed ([int]$r.Status) ([int]$r.Value) ([int]$w.Want) ([int]$w.Default)
            if ($need -eq 'unreadable') {
                Write-Say ('  ' + $w.Name + ' could not be read (NVIDIA status ' + [int]$r.Status + '). Nothing was written.') 'Yellow'
                return $false
            }
            if ($need -eq 'skip') {
                Write-Say ('  ' + $w.Name + ' is already ' + $w.WantText + '.') 'DarkGray'
                continue
            }
            $own = ([int]$r.Status -eq 0 -and [int]$r.Location -eq 0)
            [void]$plan.Add(@{ Item = $w; HadOwn = $own; Old = [int]$r.Value })
        }
        if ($plan.Count -eq 0) {
            Write-Say '  Shader cache is Unlimited and Vertical sync is Off already.' 'Green'
            return $true
        }
        foreach ($p in $plan) {
            $st = [int]$drs.Set([uint32]$p.Item.Id, [int]$p.Item.Want)
            if ($st -ne 0) {
                Write-Say ('  NVIDIA did not accept ' + $p.Item.Name + ' (status ' + $st + '). Nothing was written.') 'Yellow'
                return $false
            }
        }
        foreach ($p in $plan) {
            $rb = $drs.Read([uint32]$p.Item.Id)
            if ([int]$rb.Status -ne 0 -or [int]$rb.Value -ne [int]$p.Item.Want) {
                Write-Say ('  ' + $p.Item.Name + ' did not read back as ' + $p.Item.WantText + '. Nothing was written.') 'Yellow'
                return $false
            }
        }
        foreach ($p in $plan) { Add-NvidiaGlobalJournal ([int]$p.Item.Id) ([string]$p.Item.Name) ([bool]$p.HadOwn) $p.Old }
        $saved = [int]$drs.Save()
        if (-not (Test-NvidiaGlobalSaved $saved)) {
            Write-Say ('  NVIDIA did not save the settings (status ' + $saved + '). Undo still has the original values.') 'Yellow'
            return $false
        }
        if ($saved -eq -157) { Write-Say '  NVIDIA asked for a driver reload. Restart the game or Windows.' 'Yellow' }
        foreach ($p in $plan) { Write-Say ('  ' + $p.Item.Name + ' set to ' + $p.Item.WantText + '.') 'Green' }
        return $true
    } finally {
        try { $drs.Dispose() } catch { }
    }
}

function Restore-NvidiaGlobalEntry {
    param($Entry)
    if ($null -eq $Entry.SettingId) { return $false }
    $id = [uint32][int]$Entry.SettingId
    if (-not (Initialize-NvidiaGlobalType)) { Write-KLog ('NVIDIA global undo: ' + $script:NvidiaGlobalTypeError) 'WARN'; return $false }
    $drs = [Kpekpes.KpekpesNvidiaGlobal]::Open()
    if (-not $drs.Ok) { Write-KLog ('NVIDIA global undo could not open: ' + [string]$drs.Error) 'WARN'; try { $drs.Dispose() } catch { }; return $false }
    try {
        if ([bool]$Entry.HadOwn) {
            if ($null -eq $Entry.Old) { return $false }
            $st = [int]$drs.Set($id, [int]$Entry.Old)
            if ($st -ne 0) { return $false }
            $rb = $drs.Read($id)
            if ([int]$rb.Status -ne 0 -or [int]$rb.Value -ne [int]$Entry.Old) { return $false }
            return (Test-NvidiaGlobalSaved ([int]$drs.Save()))
        }
        $st = [int]$drs.Delete($id)
        if ($st -eq -160) { return $true }
        if ($st -ne 0) { return $false }
        $rb = $drs.Read($id)
        if ([int]$rb.Status -eq 0 -and [int]$rb.Location -eq 0) { return $false }
        return (Test-NvidiaGlobalSaved ([int]$drs.Save()))
    } finally {
        try { $drs.Dispose() } catch { }
    }
}
