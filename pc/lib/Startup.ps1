# Startup.ps1 -- the Windows startup menu. Turning an item off does not delete the program.
# Windows Security and this program are never turned off.

function Test-KStartupProtected {
    param([string]$Name)
    return [bool]($Name -match 'SecurityHealth|Windows Defender|WinDefend|MsMpEng|Jesus\s*Kpekpes')
}

function Get-KStartupOffBytes { return [byte[]](3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) }

function Test-KStartupOn {
    param([byte[]]$Blob)
    if ($null -eq $Blob -or $Blob.Length -eq 0) { return $true }
    $b = $Blob[0]
    return ($b -eq 2 -or $b -eq 6 -or $b -eq 0)
}

function ConvertTo-KHex {
    param([byte[]]$Blob)
    if (-not $Blob -or $Blob.Length -eq 0) { return '' }
    return (($Blob | ForEach-Object { $_.ToString('X2') }) -join '-')
}

function ConvertFrom-KHex {
    param([string]$Hex)
    if (-not $Hex) { return [byte[]]@() }
    return [byte[]]@($Hex.Split('-') | ForEach-Object { [Convert]::ToByte($_, 16) })
}

function Test-KBytesEqual {
    param($Left, $Right)
    $a = @($Left); $b = @($Right)
    if ($a.Count -ne $b.Count) { return $false }
    for ($i = 0; $i -lt $a.Count; $i++) { if ([int]$a[$i] -ne [int]$b[$i]) { return $false } }
    return $true
}

function Read-KStartupApproved {
    param([string]$Path, [string]$Name)
    if (-not (Test-Path -LiteralPath $Path)) { return @{ Existed = $false; Blob = $null } }
    $p = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    if (-not $p) { return @{ Existed = $false; Blob = $null } }
    $raw = $p.$Name
    if ($raw -is [byte[]]) { return @{ Existed = $true; Blob = [byte[]]$raw } }
    return @{ Existed = $false; Blob = $null }
}

function Read-KRunKey {
    param([string]$RunPath, [string]$ApprovedPath, [string]$Where)
    if (-not (Test-Path -LiteralPath $RunPath)) { return @() }
    $item = Get-Item -LiteralPath $RunPath
    $out = @()
    foreach ($name in @($item.GetValueNames())) {
        if (-not $name) { continue }
        $approved = Read-KStartupApproved $ApprovedPath $name
        $out += [pscustomobject]@{
            Name = [string]$name
            Command = [string]$item.GetValue($name)
            On = (Test-KStartupOn $approved.Blob)
            Where = $Where
            ApprovedPath = $ApprovedPath
            Existed = [bool]$approved.Existed
            Hex = (ConvertTo-KHex $approved.Blob)
        }
    }
    return $out
}

function Read-KStartupFolder {
    param([string]$Folder, [string]$ApprovedPath, [string]$Where)
    if (-not $Folder -or -not (Test-Path -LiteralPath $Folder)) { return @() }
    $out = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $Folder -File -ErrorAction SilentlyContinue)) {
        $approved = Read-KStartupApproved $ApprovedPath $file.Name
        $out += [pscustomobject]@{
            Name = [string]$file.Name
            Command = [string]$file.FullName
            On = (Test-KStartupOn $approved.Blob)
            Where = $Where
            ApprovedPath = $ApprovedPath
            Existed = [bool]$approved.Existed
            Hex = (ConvertTo-KHex $approved.Blob)
        }
    }
    return $out
}

function Get-KStartupItems {
    $rows = @()
    $rows += Read-KRunKey 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' 'your account'
    $rows += Read-KRunKey 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' 'this PC'
    $rows += Read-KRunKey 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32' 'this PC, 32-bit'
    $rows += Read-KStartupFolder ([Environment]::GetFolderPath('Startup')) 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder' 'your startup folder'
    $rows += Read-KStartupFolder ([Environment]::GetFolderPath('CommonStartup')) 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder' 'the shared startup folder'
    return @($rows)
}

function Disable-KStartup {
    param([string[]]$Names)
    $want = @{}
    foreach ($n in @($Names)) { if ($n -and $n.Trim()) { $want[$n.Trim()] = $true } }
    $lines = New-Object System.Collections.Generic.List[string]
    if ($want.Count -eq 0) { $lines.Add('Nothing was ticked, so nothing was turned off.'); return $lines }
    $seen = @{}
    foreach ($item in @(Get-KStartupItems)) {
        if (-not $want.ContainsKey($item.Name)) { continue }
        $seen[$item.Name] = $true
        if (Test-KStartupProtected $item.Name) { $lines.Add($item.Name + ' was left on. It is Windows security or this program.'); continue }
        if (-not $item.On) { $lines.Add($item.Name + ' is already off in the startup menu.'); continue }
        if ($script:WhatIfMode) { $lines.Add('Would turn off ' + $item.Name + '. The program would not be deleted.'); continue }
        [void](Add-KEntry ('startup|' + $item.ApprovedPath + '|' + $item.Name) @{ Type = 'startup'; Path = $item.ApprovedPath; Name = $item.Name; Existed = $item.Existed; Value = $item.Hex })
        if (-not (Test-Path -LiteralPath $item.ApprovedPath)) { New-Item -Path $item.ApprovedPath -Force | Out-Null }
        New-ItemProperty -LiteralPath $item.ApprovedPath -Name $item.Name -PropertyType Binary -Value (Get-KStartupOffBytes) -Force | Out-Null
        $lines.Add('Turned off ' + $item.Name + '. The program is still installed. Undo turns it back on.')
    }
    foreach ($name in @($want.Keys)) { if (-not $seen.ContainsKey($name)) { $lines.Add($name + ' was not in the startup menu. Nothing was changed.') } }
    return $lines
}

function Restore-StartupEntry {
    param($e)
    try {
        $now = Read-KStartupApproved ([string]$e.Path) ([string]$e.Name)
        $off = Get-KStartupOffBytes
        $orig = ConvertFrom-KHex ([string]$e.Value)
        $sameOff = $now.Existed -and (Test-KBytesEqual $now.Blob $off)
        $sameOrig = if ($e.Existed) { $now.Existed -and (Test-KBytesEqual $now.Blob $orig) } else { -not $now.Existed }
        if (-not $sameOff -and -not $sameOrig) { [void]$script:KDrift.Add([string]$e.Name) }
        if ($e.Existed) {
            if (-not (Test-Path -LiteralPath $e.Path)) { New-Item -Path $e.Path -Force | Out-Null }
            New-ItemProperty -LiteralPath $e.Path -Name $e.Name -PropertyType Binary -Value $orig -Force | Out-Null
        } elseif (Test-Path -LiteralPath $e.Path) {
            Remove-ItemProperty -LiteralPath $e.Path -Name $e.Name -ErrorAction SilentlyContinue
        }
        return $true
    } catch { return $false }
}
