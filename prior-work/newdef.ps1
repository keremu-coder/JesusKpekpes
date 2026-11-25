# ---------------- Defender: PROCESS exclusions the person approved ----------------
# A process exclusion tells Defender to skip real-time scanning of every file the named program touches. It stays
# registered until it is removed (it does not switch on and off when the game starts and stops). So:
#   - only full executable paths, never folders;
#   - only after the person says yes (the answer is kept in an admin-only file, pinned to the publisher or SHA-256);
#   - Battle.net stays protected unless the config says DefenderOfferBattleNet = true and the person then approves;
#   - every add is checked against Get-MpPreference, and a rejected add is reported as a failure;
#   - only exclusions that were NOT there before are journaled, so Undo never removes one of yours.

function Get-PcApprovalFile { return (Join-Path (Join-Path $script:DataRoot 'pc') 'defender-approved.json') }

function Read-PcApprovals {
    $f = Get-PcApprovalFile
    if (-not (Test-Path -LiteralPath $f)) { return @() }
    try {
        $raw = Get-Content -LiteralPath $f -Raw -Encoding UTF8 | ConvertFrom-Json
        return @($raw | Where-Object { $_ -and $_.Path })
    } catch { Write-KLog ('Defender approvals unreadable: ' + $_.Exception.Message) 'WARN'; return @() }
}

function Save-PcApprovals {
    param($List)
    if ($script:WhatIfMode) { return }
    $f = Get-PcApprovalFile
    $dir = Split-Path -Path $f -Parent
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = ConvertTo-Json -InputObject @($List) -Depth 4
    [IO.File]::WriteAllText($f, $json, (New-Object Text.UTF8Encoding($false)))
}

function Get-PcPublisherName {
    param([string]$Subject)
    if ($Subject -match 'CN=("([^"]+)"|[^,]+)') { return ([string]$Matches[1]).Trim('"') }
    return $Subject
}

function Get-PcExeFacts {
    # Everything we can prove about one executable, read only.
    param([string]$Path)
    $f = [ordered]@{ Path = $Path; Exists = $false; Sha256 = ''; SigStatus = 'NotChecked'; Signer = ''; Owner = ''; AdminOnly = $false; WhyNot = '' }
    if (-not $Path -or $Path -notmatch '\.exe$' -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $f.WhyNot = 'not an existing .exe file'
        return [pscustomobject]$f
    }
    $f.Exists = $true
    try { $f.Sha256 = [string](Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash } catch { }
    try {
        $sig = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
        $f.SigStatus = [string]$sig.Status
        if ($sig.SignerCertificate) { $f.Signer = [string]$sig.SignerCertificate.Subject }
    } catch { $f.SigStatus = 'Unknown' }
    try { $f.Owner = [string](Get-Acl -LiteralPath $Path -ErrorAction Stop).Owner } catch { }
    $s = Test-KPathSafeForElevation $Path
    $f.AdminOnly = [bool]$s.Safe
    $f.WhyNot = [string]$s.Reason
    return [pscustomobject]$f
}

function Get-PcExclusionRisk {
    # Low: signed and only admins can change it. Medium: signed, but a normal account can change the file or its folder.
    # High: no valid signature, so only the SHA-256 vouches for it.
    param($Facts)
    if ($Facts.SigStatus -eq 'Valid') { if ($Facts.AdminOnly) { return 'Low' } else { return 'Medium' } }
    return 'High'
}

function Get-PcDefenderCandidates {
    # Real files found on this PC, nothing guessed. Returns objects: Name, Path, Policy (Offer|Protected), Facts, Risk.
    param($Cfg)
    $raw = New-Object System.Collections.Generic.List[object]
    foreach ($p in (Get-CodExePaths)) { [void]$raw.Add(@{ Name = ('Call of Duty (' + (Split-Path -Path $p -Leaf) + ')'); Path = $p; Policy = 'Offer' }) }
    $bnPolicy = 'Protected'
    if ($Cfg -and $Cfg.DefenderOfferBattleNet) { $bnPolicy = 'Offer' }
    foreach ($r in (Get-BattleNetRoots)) {
        $bn = Join-Path $r 'Battle.net.exe'
        if (Test-Path -LiteralPath $bn -PathType Leaf) { [void]$raw.Add(@{ Name = 'Battle.net'; Path = $bn; Policy = $bnPolicy }) }
    }
    $ds4 = ''
    if (Get-Command Find-DS4Windows -ErrorAction SilentlyContinue) { $ds4 = Find-DS4Windows }
    if ($ds4 -and (Test-Path -LiteralPath $ds4 -PathType Leaf)) { [void]$raw.Add(@{ Name = 'DS4Windows'; Path = $ds4; Policy = 'Offer' }) }
    $seen = @{}
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($c in $raw) {
        $k = ([string]$c.Path).ToLowerInvariant()
        if ($seen.ContainsKey($k)) { continue }
        $seen[$k] = $true
        $facts = Get-PcExeFacts $c.Path
        [void]$out.Add([pscustomobject]@{ Name = $c.Name; Path = $c.Path; Policy = $c.Policy; Facts = $facts; Risk = (Get-PcExclusionRisk $facts) })
    }
    return @($out)
}

function Test-PcApprovalValid {
    # An approval only counts while the file is still what the person approved: same publisher if it is signed,
    # otherwise the same SHA-256. A replaced or modified file has to be approved again.
    param($Rec, $Facts)
    if (-not $Facts.Exists) { return $false }
    if ($Rec.Signer) { return ($Facts.SigStatus -eq 'Valid' -and [string]$Facts.Signer -eq [string]$Rec.Signer) }
    return ($Rec.Sha256 -and [string]$Facts.Sha256 -ieq [string]$Rec.Sha256)
}

function Read-PcYesNo {
    param([string]$Question)
    $a = Read-Host ('  ' + $Question + ' (y/n)')
    return ($a -match '^(y|yes)$')
}

function Show-PcCandidate {
    param($C)
    $f = $C.Facts
    $pub = 'none (not signed, or the signature is not valid)'
    if ($f.SigStatus -eq 'Valid' -and $f.Signer) { $pub = (Get-PcPublisherName $f.Signer) + ' (signature valid)' }
    elseif ($f.SigStatus -ne 'Valid') { $pub = 'no valid signature (' + $f.SigStatus + ')' }
    $who = 'only administrators can change it'
    if (-not $f.AdminOnly) { $who = 'a normal account can change it (' + $f.WhyNot + ')' }
    Write-Say ('  ' + $C.Name) 'White'
    Write-Say ('    path      : ' + $C.Path) 'Gray'
    Write-Say ('    publisher : ' + $pub) 'Gray'
    Write-Say ('    permission: ' + $who) 'Gray'
    Write-Say ('    risk      : ' + $C.Risk) 'Gray'
}

function Get-PcDefenderState {
    # Read only. Used by the Check screen and by the optimizer, so both tell the same story.
    param($Cfg)
    $cands = @(Get-PcDefenderCandidates $Cfg)
    $appr = @(Read-PcApprovals)
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($c in $cands) {
        $rec = $null
        foreach ($a in $appr) { if ([string]$a.Path -ieq [string]$c.Path) { $rec = $a } }
        $st = 'NotApproved'
        if ($c.Policy -eq 'Protected') { $st = 'Protected' }
        elseif ($rec) { if (Test-PcApprovalValid $rec $c.Facts) { $st = 'Approved' } else { $st = 'ChangedSinceApproval' } }
        [void]$rows.Add([pscustomobject]@{ Candidate = $c; Record = $rec; State = $st })
    }
    return @($rows)
}

function Invoke-PcDefender {
    # Returns $true when everything approved is really excluded (or nothing is approved). On a problem it
    # returns $false and sets $script:PcDefenderProblem to the text for the summary.
    param($Cfg, [switch]$Reapply)
    $script:PcDefenderProblem = ''
    $mp = $null
    if (-not $script:WhatIfMode) {
        try { $mp = Get-MpPreference -ErrorAction Stop } catch {
            Write-KLog ('Defender not available: ' + $_.Exception.Message) 'WARN'
            $script:PcDefenderProblem = 'Defender not available (another antivirus, or Defender is managed by policy)'
            Write-Say '  Defender could not be read, so nothing was changed.' 'Yellow'
            return $false
        }
    }
    $interactive = (-not $script:Silent) -and (-not $script:WhatIfMode) -and (-not $Reapply)
    $rows = @(Get-PcDefenderState $Cfg)
    if ($rows.Count -eq 0) {
        Write-Say '  No Call of Duty, Battle.net or DS4Windows program file was found, so there is nothing to exclude.' 'DarkGray'
        foreach ($r in (Get-CodInstallRoots)) { Write-Say ('  game folder found but no known game program inside it: ' + $r) 'DarkGray' }
        return $true
    }
    Write-Say '  A process exclusion makes Defender skip scanning files that program touches. It stays until removed.' 'DarkGray'
    Write-Say '  Only whole program paths are used (never folders). Undo removes only what this optimizer added.' 'DarkGray'
    $approvals = New-Object System.Collections.Generic.List[object]
    foreach ($a in @(Read-PcApprovals)) { [void]$approvals.Add($a) }
    $toApply = New-Object System.Collections.Generic.List[object]
    $waiting = 0
    foreach ($row in $rows) {
        $c = $row.Candidate
        if ($row.State -eq 'Protected') { Write-Say ('  ' + $c.Name + ': PROTECTED, not excluded (' + $c.Path + ')') 'DarkGray'; continue }
        if ($row.State -eq 'Approved') { [void]$toApply.Add($c); continue }
        if ($row.State -eq 'ChangedSinceApproval') {
            Write-Say ('  ' + $c.Name + ': the file changed since you approved it, so it is not excluded until you approve again (' + $c.Path + ')') 'Yellow'
        }
        if ($interactive) {
            Show-PcCandidate $c
            $q = 'Exclude this program from Defender real-time scanning?'
            if ($c.Name -eq 'Battle.net') { $q = 'Battle.net downloads and runs game updates. Exclude it anyway?' }
            if (Read-PcYesNo $q) {
                $rec = [pscustomobject]@{ Name = $c.Name; Path = $c.Path; Sha256 = [string]$c.Facts.Sha256; Signer = $(if ($c.Facts.SigStatus -eq 'Valid') { [string]$c.Facts.Signer } else { '' }); Approved = (Get-Date).ToString('s') }
                $keep = @($approvals | Where-Object { [string]$_.Path -ine [string]$c.Path })
                $approvals.Clear(); foreach ($k in $keep) { [void]$approvals.Add($k) }
                [void]$approvals.Add($rec)
                Save-PcApprovals $approvals
                [void]$toApply.Add($c)
            } else { Write-Say ('  ' + $c.Name + ': not excluded (you said no)') 'DarkGray' }
        } else {
            $waiting++
            Write-Say ('  ' + $c.Name + ': waiting for your approval (' + $c.Path + ')') 'Yellow'
        }
    }
    if ($waiting -gt 0) { Write-Say '  To approve, run Optimize PC from the menu and answer the questions. Silent runs never approve for you.' 'Yellow' }
    if ($script:WhatIfMode) {
        foreach ($c in $toApply) { Write-Say ('  would exclude process: ' + $c.Path) 'Yellow' }
        return $true
    }
    $failed = New-Object System.Collections.Generic.List[string]
    foreach ($c in $toApply) {
        $p = [string]$c.Path
        $have = @($mp.ExclusionProcess)
        if (@($have | Where-Object { [string]$_ -ieq $p }).Count -gt 0) { Write-Say ('  already excluded: ' + $p) 'DarkGray'; continue }
        [void](Add-KEntry ('defexcl|Process|' + $p) @{ Type = 'defexcl'; Kind = 'Process'; Value = $p })
        $err = ''
        try { Add-MpPreference -ExclusionProcess $p -ErrorAction Stop } catch { $err = $_.Exception.Message }
        $now = @()
        try { $now = @((Get-MpPreference -ErrorAction Stop).ExclusionProcess) } catch { }
        if (@($now | Where-Object { [string]$_ -ieq $p }).Count -gt 0) { Write-Say ('  excluded (checked in Defender): ' + $p) 'Green' }
        else {
            if (-not $err) { $err = 'Defender accepted the command but the exclusion is not in its list (Tamper Protection or policy may block it)' }
            Write-Say ('  FAILED: ' + $p + ' - ' + $err) 'Red'
            [void]$failed.Add($c.Name)
        }
    }
    if ($failed.Count -gt 0) {
        $script:PcDefenderProblem = 'Defender refused: ' + ($failed -join ', ')
        return $false
    }
    return $true
}

