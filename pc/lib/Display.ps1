# Display.ps1 -- raise a monitor only to a rate Windows already lists, at the same resolution.
# A rate that is not in the list is never invented. The original rate is journaled before it is changed.

function Select-KRefreshHz {
    # 0 means do not change. A higher rate at a different resolution is ignored.
    param([int]$Width, [int]$Height, [int]$CurrentHz, $Modes)
    if ($Width -lt 640 -or $Height -lt 480 -or $CurrentHz -lt 24) { return 0 }
    $best = $CurrentHz
    foreach ($m in @($Modes)) {
        if ($null -eq $m) { continue }
        $w = 0; $h = 0; $hz = 0
        [void][int]::TryParse([string]$m.W, [ref]$w)
        [void][int]::TryParse([string]$m.H, [ref]$h)
        [void][int]::TryParse([string]$m.Hz, [ref]$hz)
        if ($w -eq $Width -and $h -eq $Height -and $hz -gt $best) { $best = $hz }
    }
    if ($best -le $CurrentHz) { return 0 }
    return $best
}

function Get-KHighestHzElsewhere {
    param([int]$Width, [int]$Height, $Modes)
    $best = 0
    foreach ($m in @($Modes)) {
        if ($null -eq $m) { continue }
        $w = 0; $h = 0; $hz = 0
        [void][int]::TryParse([string]$m.W, [ref]$w)
        [void][int]::TryParse([string]$m.H, [ref]$h)
        [void][int]::TryParse([string]$m.Hz, [ref]$hz)
        if (($w -ne $Width -or $h -ne $Height) -and $hz -gt $best) { $best = $hz }
    }
    return $best
}

function Add-KDisplayType {
    if ('KDisplay' -as [type]) { return $true }
    $src = @'
using System;
using System.Runtime.InteropServices;
public class KDisplay {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct DISPLAY_DEVICE {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);
    public const int Current = -1;
    public const int UpdateRegistry = 0x01;
    public const int Fields = 0x40000 | 0x80000 | 0x100000 | 0x400000;
}
'@
    try { Add-Type -TypeDefinition $src -ErrorAction Stop } catch { if (-not ('KDisplay' -as [type])) { return $false } }
    return $true
}

function New-KDevMode {
    $dm = New-Object KDisplay+DEVMODE
    $dm.dmDeviceName = New-Object string ([char]0, 32)
    $dm.dmFormName = New-Object string ([char]0, 32)
    $dm.dmSize = [int16][Runtime.InteropServices.Marshal]::SizeOf([type][KDisplay+DEVMODE])
    return $dm
}

function Get-KDisplayRows {
    if (-not (Add-KDisplayType)) { return @() }
    $rows = @()
    for ($i = 0; $i -lt 16; $i++) {
        $dd = New-Object KDisplay+DISPLAY_DEVICE
        $dd.cb = [Runtime.InteropServices.Marshal]::SizeOf([type][KDisplay+DISPLAY_DEVICE])
        if (-not [KDisplay]::EnumDisplayDevices($null, [uint32]$i, [ref]$dd, 0)) { break }
        if (($dd.StateFlags -band 1) -eq 0) { continue }
        $cur = New-KDevMode
        if (-not [KDisplay]::EnumDisplaySettings($dd.DeviceName, [KDisplay]::Current, [ref]$cur)) { continue }
        $modes = @()
        for ($n = 0; $n -lt 500; $n++) {
            $dm = New-KDevMode
            if (-not [KDisplay]::EnumDisplaySettings($dd.DeviceName, $n, [ref]$dm)) { break }
            if (($dm.dmDisplayFlags -band 2) -ne 0) { continue }
            if ($dm.dmPelsWidth -lt 640 -or $dm.dmDisplayFrequency -lt 24) { continue }
            $modes += [pscustomobject]@{ W = [int]$dm.dmPelsWidth; H = [int]$dm.dmPelsHeight; Hz = [int]$dm.dmDisplayFrequency }
        }
        $rows += [pscustomobject]@{
            Device = [string]$dd.DeviceName
            Name   = [string]$dd.DeviceString
            W      = [int]$cur.dmPelsWidth
            H      = [int]$cur.dmPelsHeight
            Bits   = [int]$cur.dmBitsPerPel
            Hz     = [int]$cur.dmDisplayFrequency
            Modes  = $modes
        }
    }
    return $rows
}

function Invoke-KDisplayRaise {
    $lines = New-Object System.Collections.Generic.List[string]
    $rows = @(Get-KDisplayRows)
    if ($rows.Count -eq 0) { $lines.Add('The monitor rate could not be read. Nothing was changed.'); return $lines }
    foreach ($row in $rows) {
        $target = Select-KRefreshHz $row.W $row.H $row.Hz $row.Modes
        $label = ($row.Name + ' ' + $row.W + 'x' + $row.H)
        if ($target -eq 0) {
            $else = Get-KHighestHzElsewhere $row.W $row.H $row.Modes
            if ($else -gt $row.Hz) {
                $lines.Add($label + ' is at ' + $row.Hz + ' Hz. ' + $else + ' Hz is listed only at another resolution, so the resolution was not changed.')
            } else {
                $lines.Add($label + ' is already at ' + $row.Hz + ' Hz, the highest rate Windows lists at this resolution. Nothing was changed.')
            }
            continue
        }
        if ($script:WhatIfMode) {
            $lines.Add($label + ' is at ' + $row.Hz + ' Hz. It would be raised to ' + $target + ' Hz. Resolution stays ' + $row.W + 'x' + $row.H + '.')
            continue
        }
        $id = 'display|' + $row.Device
        [void](Add-KEntry $id @{ Type = 'display'; Device = $row.Device; Name = $row.Name; W = $row.W; H = $row.H; Bits = $row.Bits; Hz = $row.Hz; AppliedHz = $target })
        $dm = New-KDevMode
        $dm.dmBitsPerPel = $row.Bits
        $dm.dmPelsWidth = $row.W
        $dm.dmPelsHeight = $row.H
        $dm.dmDisplayFrequency = $target
        $dm.dmFields = [KDisplay]::Fields
        $rc = [KDisplay]::ChangeDisplaySettingsEx($row.Device, [ref]$dm, [IntPtr]::Zero, [KDisplay]::UpdateRegistry, [IntPtr]::Zero)
        if ($rc -eq 0) { $lines.Add($label + ' was raised from ' + $row.Hz + ' Hz to ' + $target + ' Hz. Undo puts ' + $row.Hz + ' Hz back.') }
        else { $lines.Add($label + ' stayed at ' + $row.Hz + ' Hz. Windows refused ' + $target + ' Hz (code ' + $rc + ').') }
    }
    return $lines
}

function Restore-DisplayEntry {
    param($e)
    try {
        if (-not (Add-KDisplayType)) { return $false }
        $cur = New-KDevMode
        $known = [KDisplay]::EnumDisplaySettings([string]$e.Device, [KDisplay]::Current, [ref]$cur)
        if ($known) {
            $now = [int]$cur.dmDisplayFrequency
            $orig = [int]$e.Hz
            $applied = [int]$e.AppliedHz
            if ($now -ne $orig -and $now -ne $applied) { [void]$script:KDrift.Add(([string]$e.Name) + ' refresh') }
        }
        $dm = New-KDevMode
        $dm.dmBitsPerPel = [int]$e.Bits
        $dm.dmPelsWidth = [int]$e.W
        $dm.dmPelsHeight = [int]$e.H
        $dm.dmDisplayFrequency = [int]$e.Hz
        $dm.dmFields = [KDisplay]::Fields
        $rc = [KDisplay]::ChangeDisplaySettingsEx([string]$e.Device, [ref]$dm, [IntPtr]::Zero, [KDisplay]::UpdateRegistry, [IntPtr]::Zero)
        return ($rc -eq 0)
    } catch { return $false }
}
