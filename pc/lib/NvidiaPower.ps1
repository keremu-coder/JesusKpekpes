# NvidiaPower.ps1 -- Prefer maximum performance for Call of Duty, through NVIDIA's own interface.
# The driver database file is never opened. Nothing is journaled until the in-session readback is 1.
# If that readback fails, the session is thrown away and nothing is saved.
# Undo deletes a profile this tool created, puts back a value the profile already had,
# removes an override that was only inherited, or removes an exe this tool added.

$script:NvidiaPowerDir = $PSScriptRoot
$script:NvidiaCodProfile = 'Kpekpes Call of Duty'
$script:NvidiaTypeReady = $false
$script:NvidiaTypeError = ''

function Get-NvidiaUndoKind {
    # Created wins: the whole profile goes, including anything we added to it.
    # Added is an exe we attached to a profile we did not create. It is removed on its own.
    # HadOwn means the profile already stored this setting, so Undo writes that old value back.
    # Otherwise the value was inherited, and Undo removes the override instead of pinning it.
    param([bool]$Created, [bool]$HadOwn, [bool]$Added = $false)
    if ($Created) { return 'delete-profile' }
    if ($Added) { return 'delete-app' }
    if ($HadOwn) { return 'restore-value' }
    return 'delete-setting'
}

function Initialize-NvidiaPowerType {
    if ($script:NvidiaTypeReady) { return $true }
    if (-not ('Kpekpes.KpekpesNvidiaSession' -as [type])) {
        $src = Join-Path $script:NvidiaPowerDir 'NvidiaPower.cs'
        if (-not (Test-Path -LiteralPath $src)) {
            $script:NvidiaTypeError = 'NVIDIA helper source is missing. Set Power management mode by hand.'
            return $false
        }
        try { Add-Type -Path $src -ErrorAction Stop }
        catch {
            $script:NvidiaTypeError = 'NVIDIA helper could not be compiled. Set Power management mode by hand.'
            Write-KLog ('NVIDIA helper compile: ' + $_.Exception.Message) 'WARN'
            return $false
        }
    }
    $script:NvidiaTypeReady = $true
    return $true
}

function Open-NvidiaSession {
    $script:NvidiaWhy = ''
    if (-not [Environment]::Is64BitProcess) {
        $script:NvidiaWhy = 'This PowerShell is 32-bit, so NVIDIA''s interface cannot be used. Set Power management mode by hand.'
        return $null
    }
    $dll = Join-Path $env:SystemRoot 'System32\nvapi64.dll'
    if (-not (Test-Path -LiteralPath $dll)) {
        $script:NvidiaWhy = 'NVIDIA''s nvapi64.dll was not found. Nothing was written. Set Power management mode by hand.'
        return $null
    }
    if (-not (Initialize-NvidiaPowerType)) {
        $script:NvidiaWhy = $script:NvidiaTypeError
        return $null
    }
    $drs = [Kpekpes.KpekpesNvidiaSession]::Open()
    if (-not $drs.Ok) {
        $why = [string]$drs.Error
        if (-not $why) { $why = 'NVIDIA''s interface did not open. Nothing was written.' }
        $script:NvidiaWhy = $why
        try { $drs.Dispose() } catch { }
        return $null
    }
    $script:NvidiaGlobalRead = $false
    $script:NvidiaGlobal = $null
    return $drs
}

function Test-NvidiaSaved {
    param([int]$Status)
    return ($Status -eq 0 -or $Status -eq -157)
}

function Get-NvidiaGlobalPower {
    param($Drs)
    if ($script:NvidiaGlobalRead) { return $script:NvidiaGlobal }
    $script:NvidiaGlobalRead = $true
    $g = $Drs.GlobalProfile()
    if ([int]$g.Status -eq -9) {
        $script:NvidiaGlobal = @{ Abort = $true; Status = -9 }
        return $script:NvidiaGlobal
    }
    if ([int]$g.Status -ne 0 -or $g.Profile -eq 0) {
        $script:NvidiaGlobal = @{ Known = $false; Status = [int]$g.Status }
        return $script:NvidiaGlobal
    }
    $s = $Drs.ReadSetting([int64]$g.Profile)
    if ([int]$s.Status -eq -9) {
        $script:NvidiaGlobal = @{ Abort = $true; Status = -9 }
        return $script:NvidiaGlobal
    }
    if ([int]$s.Status -eq 0) {
        $script:NvidiaGlobal = @{ Known = $true; Value = [int]$s.Value }
        return $script:NvidiaGlobal
    }
    if ([int]$s.Status -eq -160) {
        # The driver's own default for this setting is Optimal power, not Prefer maximum.
        $script:NvidiaGlobal = @{ Known = $true; Value = 5 }
        return $script:NvidiaGlobal
    }
    $script:NvidiaGlobal = @{ Known = $false; Status = [int]$s.Status }
    return $script:NvidiaGlobal
}

function Get-NvidiaProfilePower {
    param($Drs, [int64]$Profile)
    $s = $Drs.ReadSetting($Profile)
    if ([int]$s.Status -eq -9) { return @{ Abort = $true; Status = -9 } }
    if ([int]$s.Status -eq 0) {
        $own = ([int]$s.Location -eq 0)
        return @{ Known = $true; Value = [int]$s.Value; HadOwn = $own; Old = [int]$s.Value }
    }
    if ([int]$s.Status -eq -160) {
        $g = Get-NvidiaGlobalPower $Drs
        if ($g.Abort) { return $g }
        if (-not $g.Known) { return @{ Known = $false; Status = $g.Status } }
        return @{ Known = $true; Value = [int]$g.Value; HadOwn = $false; Old = $null }
    }
    return @{ Known = $false; Status = [int]$s.Status }
}

function Get-NvidiaPathsForExe {
    param([string]$Bare)
    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($p in @(Get-CodExePaths)) {
        if ([IO.Path]::GetFileName($p) -eq $Bare) { [void]$hits.Add($p) }
    }
    return @($hits | Select-Object -Unique)
}

function Resolve-NvidiaExe {
    param($Drs, [string]$Bare)
    $found = $Drs.Find($Bare)
    if ([int]$found.Status -eq -9) { return @{ Abort = $true } }
    if ([int]$found.Status -eq 0 -and $found.Profile -ne 0) {
        return @{ Found = $true; Profile = [int64]$found.Profile; Name = $Bare }
    }
    if ([int]$found.Status -eq -182) {
        $paths = @(Get-NvidiaPathsForExe $Bare)
        if ($paths.Count -eq 0) {
            return @{ Problem = ($Bare + ' matches more than one NVIDIA profile, and the game folder was not found.') }
        }
        $hits = New-Object System.Collections.ArrayList
        foreach ($p in $paths) {
            $one = $Drs.Find($p)
            if ([int]$one.Status -eq -9) { return @{ Abort = $true } }
            if ([int]$one.Status -eq 0 -and $one.Profile -ne 0) {
                [void]$hits.Add(@{ Found = $true; Profile = [int64]$one.Profile; Name = $p })
            }
        }
        if ($hits.Count -eq 0) {
            return @{ Problem = ($Bare + ' matches more than one NVIDIA profile, and none of the installed copies could be read.') }
        }
        return @{ Many = $true; Hits = @($hits) }
    }
    if ([int]$found.Status -eq -166 -or [int]$found.Status -eq -163) {
        return @{ Missing = $true; Bare = $Bare }
    }
    return @{ Problem = ($Bare + ' could not be read (NVIDIA status ' + [int]$found.Status + ').') }
}

function Add-NvidiaPlanItem {
    param($Plan, $Seen, [int64]$Profile, [string]$Name, [bool]$HadOwn, $Old, [string]$Journal, [bool]$Added = $false)
    $key = [string]$Profile + '|' + $Journal + '|' + $Name
    if ($Seen.ContainsKey($key)) { return }
    $Seen[$key] = $true
    [void]$Plan.Add(@{
        Profile = $Profile
        Name = $Name
        HadOwn = $HadOwn
        Old = $Old
        Journal = $Journal
        Added = $Added
    })
}

function Add-NvidiaJournal {
    param([string]$Id, [bool]$Created, [bool]$HadOwn, [bool]$Added, [string]$Name, $Old)
    $data = @{ Type = 'nvpower'; Created = $Created; HadOwn = $HadOwn; Added = $Added; Name = $Name }
    if ($HadOwn) { $data['Old'] = [int]$Old }
    $prev = $script:KGroup
    $script:KGroup = 'NVIDIA'
    [void](Add-KEntry $Id $data)
    $script:KGroup = $prev
}

function Invoke-NvidiaCodPower {
    if (-not (Test-HasNvidia)) {
        Write-Say '  no NVIDIA GPU found' 'DarkGray'
        return $true
    }
    if ($script:WhatIfMode) {
        Write-Say '  would set Power management mode = Prefer maximum performance for Call of Duty' 'Gray'
        return $true
    }
    $drs = Open-NvidiaSession
    if ($null -eq $drs) {
        Write-Say ('  ' + $script:NvidiaWhy) 'Yellow'
        Write-Say '  NVIDIA Control Panel > Manage 3D settings > Program Settings > add cod.exe > Power management mode = Prefer maximum performance.' 'Yellow'
        return $false
    }
    $problems = New-Object System.Collections.ArrayList
    $plan = New-Object System.Collections.ArrayList
    $seen = @{}
    $addBare = New-Object System.Collections.ArrayList
    $names = @($script:CodExeNames)
    if ($names.Count -eq 0) { $names = @('cod.exe', 'cod24.exe', 'cod23.exe', 'ModernWarfare.exe') }
    try {
        foreach ($bare in $names) {
            $resolved = Resolve-NvidiaExe $drs $bare
            if ($resolved.Abort) {
                Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                return $false
            }
            if ($resolved.Problem) { [void]$problems.Add([string]$resolved.Problem); continue }
            $hits = @()
            if ($resolved.Many) { $hits = @($resolved.Hits) }
            elseif ($resolved.Found) { $hits = @($resolved) }
            if ($hits.Count -gt 0) {
                foreach ($hit in $hits) {
                    $eff = Get-NvidiaProfilePower $drs ([int64]$hit.Profile)
                    if ($eff.Abort) {
                        Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                        return $false
                    }
                    if (-not $eff.Known) {
                        [void]$problems.Add([string]$hit.Name + ' power mode could not be read.')
                        continue
                    }
                    if ([int]$eff.Value -eq 1) {
                        Write-Say ('  ' + $hit.Name + ' already Prefer maximum performance.') 'DarkGray'
                        continue
                    }
                    Add-NvidiaPlanItem $plan $seen ([int64]$hit.Profile) ([string]$hit.Name) ([bool]$eff.HadOwn) $eff.Old 'setting'
                }
                continue
            }
            $global = Get-NvidiaGlobalPower $drs
            if ($global.Abort) {
                Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                return $false
            }
            if (-not $global.Known) {
                [void]$problems.Add('The global power mode could not be read, so ' + $bare + ' was left alone.')
                continue
            }
            if ([int]$global.Value -eq 1) {
                Write-Say ('  ' + $bare + ' already inherits Prefer maximum performance. Left unpinned.') 'DarkGray'
                continue
            }
            [void]$addBare.Add($bare)
        }

        $created = $false
        $owned = [int64]0
        $addedNames = New-Object System.Collections.ArrayList
        if ($addBare.Count -gt 0) {
            $fp = $drs.FindProfile($script:NvidiaCodProfile)
            if ([int]$fp.Status -eq -9) {
                Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                return $false
            }
            if ([int]$fp.Status -eq 0 -and $fp.Profile -ne 0) {
                $info = $drs.ReadProfile([int64]$fp.Profile)
                if ([int]$info.Status -eq -9) {
                    Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                    return $false
                }
                if ([int]$info.Status -ne 0 -or [int]$info.IsPredefined -ne 0) {
                    [void]$problems.Add('The Call of Duty profile belongs to NVIDIA, so the missing exes were not added.')
                } else {
                    $owned = [int64]$fp.Profile
                }
            } elseif ([int]$fp.Status -eq -163) {
                $made = $drs.CreateProfile($script:NvidiaCodProfile)
                if ([int]$made.Status -eq -9) {
                    Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                    return $false
                }
                if ([int]$made.Status -ne 0 -or $made.Profile -eq 0) {
                    [void]$problems.Add('Could not create the Call of Duty profile (NVIDIA status ' + [int]$made.Status + ').')
                } else {
                    $info = $drs.ReadProfile([int64]$made.Profile)
                    if ([int]$info.Status -ne 0 -or [int]$info.IsPredefined -ne 0) {
                        Write-Say '  The new profile was not a normal user profile. Nothing was written.' 'Yellow'
                        return $false
                    }
                    $owned = [int64]$made.Profile
                    $created = $true
                }
            } else {
                [void]$problems.Add('Could not look up the Call of Duty profile (NVIDIA status ' + [int]$fp.Status + ').')
            }
            if ($owned -ne 0) {
                foreach ($bare in @($addBare)) {
                    $st = [int]$drs.CreateApplication($owned, $bare)
                    if ($st -eq -9) {
                        Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                        return $false
                    }
                    if ($st -eq -167) {
                        $again = $drs.Find($bare)
                        if ([int]$again.Status -eq 0 -and $again.Profile -ne 0) {
                            $eff = Get-NvidiaProfilePower $drs ([int64]$again.Profile)
                            if ($eff.Abort) {
                                Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                                return $false
                            }
                            if ($eff.Known -and [int]$eff.Value -ne 1) {
                                Add-NvidiaPlanItem $plan $seen ([int64]$again.Profile) $bare ([bool]$eff.HadOwn) $eff.Old 'setting'
                            } elseif ($eff.Known) {
                                Write-Say ('  ' + $bare + ' already Prefer maximum performance.') 'DarkGray'
                            } else {
                                [void]$problems.Add($bare + ' is already in another profile and its power mode could not be read.')
                            }
                        } else {
                            [void]$problems.Add($bare + ' is already in another NVIDIA profile.')
                        }
                        continue
                    }
                    if ($st -ne 0) {
                        [void]$problems.Add('Could not add ' + $bare + ' (NVIDIA status ' + $st + ').')
                        continue
                    }
                    [void]$addedNames.Add($bare)
                    Write-Say ('  ' + $bare + ' added to ' + $script:NvidiaCodProfile) 'Gray'
                }
                if ($created -and $addedNames.Count -eq 0) {
                    # An empty profile must not be saved along with any other change.
                    $drop = [int]$drs.DeleteOwnedProfile($owned)
                    if ($drop -ne 0) {
                        Write-Say '  No Call of Duty exe could be added. Nothing was written.' 'Yellow'
                        foreach ($p in $problems) { Write-Say ('  ' + $p) 'Yellow' }
                        return $false
                    }
                    $created = $false
                    $owned = [int64]0
                }
                if ($addedNames.Count -gt 0) {
                    $effOwned = Get-NvidiaProfilePower $drs $owned
                    if ($effOwned.Abort) {
                        Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                        return $false
                    }
                    if (-not $effOwned.Known) {
                        Write-Say '  The Call of Duty profile could not be read back. Nothing was written.' 'Yellow'
                        return $false
                    }
                    if ($created) {
                        if ([int]$effOwned.Value -ne 1) {
                            Add-NvidiaPlanItem $plan $seen $owned $script:NvidiaCodProfile $false $null 'profile'
                        } else {
                            # Already max without a pin. Still record the profile so Undo can remove it.
                            Add-NvidiaPlanItem $plan $seen $owned $script:NvidiaCodProfile $false $null 'profile-only'
                        }
                    } else {
                        foreach ($bare in @($addedNames)) {
                            Add-NvidiaPlanItem $plan $seen $owned $bare $false $null 'app'
                        }
                        if ([int]$effOwned.Value -ne 1) {
                            Add-NvidiaPlanItem $plan $seen $owned ([string]$addedNames[0]) ([bool]$effOwned.HadOwn) $effOwned.Old 'setting'
                        }
                    }
                }
            }
        }

        $writes = @($plan | Where-Object { $_.Journal -ne 'app' -and $_.Journal -ne 'profile-only' })
        foreach ($item in $writes) {
            $st = [int]$drs.SetDword([int64]$item.Profile, 1)
            if ($st -eq -9) {
                Write-Say '  NVIDIA rejected the profile data. Nothing was written.' 'Yellow'
                return $false
            }
            if ($st -ne 0) {
                Write-Say ('  NVIDIA did not accept the power mode (status ' + $st + '). Nothing was written.') 'Yellow'
                return $false
            }
        }
        foreach ($item in $writes) {
            $rb = $drs.ReadSetting([int64]$item.Profile)
            if ([int]$rb.Status -ne 0 -or [int]$rb.Value -ne 1) {
                Write-Say '  The power mode did not read back as Prefer maximum performance. Nothing was written.' 'Yellow'
                return $false
            }
        }

        $needSave = ($writes.Count -gt 0 -or $addedNames.Count -gt 0)
        if ($needSave) {
            foreach ($item in @($plan)) {
                if ($item.Journal -eq 'profile' -or $item.Journal -eq 'profile-only') {
                    Add-NvidiaJournal 'nvpower|profile' $true $false $false $script:NvidiaCodProfile $null
                } elseif ($item.Journal -eq 'app') {
                    Add-NvidiaJournal ('nvpower|app|' + [string]$item.Name) $false $false $true ([string]$item.Name) $null
                } else {
                    Add-NvidiaJournal ('nvpower|exe|' + [string]$item.Name) $false ([bool]$item.HadOwn) $false ([string]$item.Name) $item.Old
                }
            }
            $saved = [int]$drs.Save()
            if (-not (Test-NvidiaSaved $saved)) {
                Write-Say ('  NVIDIA did not save the profile (status ' + $saved + '). Undo still has the original value.') 'Yellow'
                return $false
            }
            if ($saved -eq -157) { Write-Say '  NVIDIA asked for a driver reload. Restart the game or Windows.' 'Yellow' }
            Write-Say '  Prefer maximum performance set for Call of Duty.' 'Green'
        } elseif ($problems.Count -eq 0) {
            Write-Say '  Prefer maximum performance is already in effect for Call of Duty.' 'Green'
        } else {
            Write-Say '  Nothing was written.' 'Yellow'
        }
        foreach ($p in $problems) { Write-Say ('  ' + $p) 'Yellow' }
        return ($problems.Count -eq 0)
    } finally {
        try { $drs.Dispose() } catch { }
    }
}

function Get-NvidiaPowerResult {
    if (-not (Test-HasNvidia)) { return $null }
    $name = 'NVIDIA power mode for Call of Duty'
    if ($script:WhatIfMode) {
        return (New-KResult $name 'INFO' 'not changed in what-if' 'NVIDIA')
    }
    $drs = Open-NvidiaSession
    if ($null -eq $drs) {
        return (New-KResult $name 'INFO' $script:NvidiaWhy 'NVIDIA')
    }
    try {
        $installed = New-Object System.Collections.Generic.List[string]
        foreach ($p in @(Get-CodExePaths)) {
            $base = [IO.Path]::GetFileName($p)
            if ($base -and -not $installed.Contains($base)) { [void]$installed.Add($base) }
        }
        if ($installed.Count -eq 0) {
            return (New-KResult $name 'INFO' 'Call of Duty is not installed. Optimize still sets Prefer maximum performance for the game exes.' 'NVIDIA')
        }
        $bad = New-Object System.Collections.ArrayList
        $unknown = New-Object System.Collections.ArrayList
        foreach ($bare in @($installed)) {
            $resolved = Resolve-NvidiaExe $drs $bare
            if ($resolved.Abort) {
                return (New-KResult $name 'INFO' 'NVIDIA rejected the profile data. Nothing was changed.' 'NVIDIA')
            }
            if ($resolved.Problem) { [void]$unknown.Add($bare); continue }
            $hits = @()
            if ($resolved.Many) { $hits = @($resolved.Hits) }
            elseif ($resolved.Found) { $hits = @($resolved) }
            if ($hits.Count -eq 0) {
                $global = Get-NvidiaGlobalPower $drs
                if ($global.Abort) { return (New-KResult $name 'INFO' 'NVIDIA rejected the profile data. Nothing was changed.' 'NVIDIA') }
                if (-not $global.Known) { [void]$unknown.Add($bare); continue }
                if ([int]$global.Value -ne 1) { [void]$bad.Add($bare) }
                continue
            }
            foreach ($hit in $hits) {
                $eff = Get-NvidiaProfilePower $drs ([int64]$hit.Profile)
                if ($eff.Abort) { return (New-KResult $name 'INFO' 'NVIDIA rejected the profile data. Nothing was changed.' 'NVIDIA') }
                if (-not $eff.Known) { [void]$unknown.Add($bare); continue }
                if ([int]$eff.Value -ne 1) { [void]$bad.Add([string]$hit.Name) }
            }
        }
        if ($unknown.Count -gt 0) {
            return (New-KResult $name 'INFO' ('Could not read: ' + ($unknown -join ', ') + '. Set it by hand if Optimize did not.') 'NVIDIA')
        }
        if ($bad.Count -gt 0) {
            return (New-KResult $name 'FAIL' ('Not Prefer maximum performance: ' + (($bad | Select-Object -Unique) -join ', ')) 'NVIDIA')
        }
        return (New-KResult $name 'PASS' 'Prefer maximum performance' 'NVIDIA')
    } finally {
        try { $drs.Dispose() } catch { }
    }
}

function Restore-NvidiaPowerEntry {
    param($Entry)
    $added = $false
    if ($Entry.PSObject.Properties['Added']) { $added = [bool]$Entry.Added }
    $kind = Get-NvidiaUndoKind ([bool]$Entry.Created) ([bool]$Entry.HadOwn) $added
    $drs = Open-NvidiaSession
    if ($null -eq $drs) { Write-KLog ('NVIDIA undo could not open: ' + $script:NvidiaWhy) 'WARN'; return $false }
    try {
        if ($kind -eq 'delete-profile') {
            $name = [string]$Entry.Name
            if (-not $name) { $name = $script:NvidiaCodProfile }
            $found = $drs.FindProfile($name)
            if ([int]$found.Status -eq -9) { return $false }
            if ([int]$found.Status -eq -163) { return $true }
            if ([int]$found.Status -ne 0 -or $found.Profile -eq 0) { return $false }
            $del = [int]$drs.DeleteOwnedProfile([int64]$found.Profile)
            if ($del -eq 1001) { Write-KLog 'refused to delete a predefined NVIDIA profile' 'WARN'; return $false }
            if ($del -ne 0) { return $false }
            $again = $drs.FindProfile($name)
            if ([int]$again.Status -ne -163) { return $false }
            return (Test-NvidiaSaved ([int]$drs.Save()))
        }
        $appName = [string]$Entry.Name
        if (-not $appName) { return $false }
        $found = $drs.Find($appName)
        if ([int]$found.Status -eq -9) { return $false }
        if ([int]$found.Status -eq -166 -or [int]$found.Status -eq -163) { return $true }
        if ([int]$found.Status -ne 0 -or $found.Profile -eq 0) { return $false }
        $info = $drs.ReadProfile([int64]$found.Profile)
        if ([int]$info.Status -ne 0) { return $false }
        if ([int]$info.IsPredefined -ne 0 -and $kind -eq 'delete-app') {
            Write-KLog 'refused to remove an exe from a predefined NVIDIA profile' 'WARN'
            return $false
        }
        if ($kind -eq 'delete-app') {
            $del = [int]$drs.DeleteApplication([int64]$found.Profile, $appName)
            if ($del -eq -166) { return $true }
            if ($del -ne 0) { return $false }
            $again = $drs.Find($appName)
            if ([int]$again.Status -eq 0 -and [int64]$again.Profile -eq [int64]$found.Profile) { return $false }
            return (Test-NvidiaSaved ([int]$drs.Save()))
        }
        if ($kind -eq 'restore-value') {
            if ($null -eq $Entry.Old) { return $false }
            $st = [int]$drs.SetDword([int64]$found.Profile, [int]$Entry.Old)
            if ($st -ne 0) { return $false }
            $rb = $drs.ReadSetting([int64]$found.Profile)
            if ([int]$rb.Status -ne 0 -or [int]$rb.Value -ne [int]$Entry.Old) { return $false }
            return (Test-NvidiaSaved ([int]$drs.Save()))
        }
        $st = [int]$drs.DeleteSetting([int64]$found.Profile)
        if ($st -eq -160) { return $true }
        if ($st -ne 0) { return $false }
        $rb = $drs.ReadSetting([int64]$found.Profile)
        if ([int]$rb.Status -eq 0 -and [int]$rb.Location -eq 0 -and [int]$rb.Value -eq 1) { return $false }
        return (Test-NvidiaSaved ([int]$drs.Save()))
    } finally {
        try { $drs.Dispose() } catch { }
    }
}
