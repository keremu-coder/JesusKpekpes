# Unreleased

- Audio: while a step is a hit, the plug-in also lifts the whole step, up to 9 dB, scaled by Footstep hit. Hit at 0 leaves that extra lift off. A steady tone, a loud gun, and a loud impact are not turned up. Press Turn on once after updating.
- **The program updates itself.** The first window shows an Update button when a newer release exists. It checks the
  SHA-256, starts the new copy once, swaps the files and undoes everything if a step fails.
- **Every Xbox app and service is removed or switched off**, not only Game Bar. Controller drivers are not touched.
- Audio: a footstep right after a loud stretch is no longer held down by the loudness stage.
- **Game Bar is removed** with the other Xbox apps (`Microsoft.XboxGamingOverlay`). On dual-CCD X3D chips it is still kept.
- **The "Get an app to open this ms-gamebar link" window is silenced** for both `ms-gamebar` and `ms-gamingoverlay`,
  in the same run that removes Game Bar and again every time the app starts. Verify reports both links.
- If Game Bar is installed again, the hidden handler is taken off so Game Bar works.
- Audio: Drop and Well trim 500 Hz and raise 3.4 kHz with the hit slider. The plug-in lifts the 3.4 kHz step tick
  up to 9 dB only while it is a hit, linked across ears, and not while a gun is being trimmed.

# Changes in 1.3.0

The logic behind these changes is checked by `tests/Test-Logic.ps1` (120 checks) and the new `tests/Test-Stats.ps1` (53 checks).
Both suites pass under PowerShell 7. The Windows-only parts (ACL checks, CPU probe, scheduled tasks, the seeded
round trip) parse cleanly but must be confirmed by `windows-test.yml` on a real Windows runner.

## Security fixes
- **DS4Windows starts with admin rights only when nobody but an administrator can replace it.** If DS4Windows and its
  profile folder sit in an admin-only place (for example `C:\Program Files\DS4Windows`) it starts with admin rights, as
  before. Otherwise it starts WITHOUT admin rights, from a normal logon task. Before, replacing `DS4Windows.exe` in
  Downloads/Desktop (no admin needed) gave silent admin rights at the next logon. `Controller.DS4Elevation` in
  `config.json` can force `elevated` or `normal`; the default is `auto`.
- **The strafe boost is written at every logon, as before.** With admin rights when the files are admin-only, otherwise by the
  same normal logon task. It is also written once at install.
- **Defender exclusions only for folders a normal account cannot change** (`lib/Safety.ps1`: trusted owner, no
  write rights for non-admins, no junctions, checked up to the drive root). `%LOCALAPPDATA%\Battle.net`, Downloads
  and user-writable Steam libraries are reported as "not excluded" instead of silently excluded.
- **`Protect-KDir` takes ownership first** (`icacls /setowner Administrators`), so a folder pre-created by a normal
  user can no longer keep its owner's power to rewrite the permissions.

## CPU-aware defaults
- Exact CCD count from the L3 caches (`lib/Topology.ps1`) replaces "8 cores or fewer = single CCD".
- On dual-CCD X3D chips (7900X3D / 7950X3D / 9900X3D / 9950X3D) core parking, Game Mode and Game Bar are kept:
  AMD's V-Cache driver needs all three.
- Game Mode stays off as before, except on dual-CCD X3D chips, where it is left alone.

## Undo
- Registry entries record what the tool wrote; Undo reports values changed afterwards by you or Windows ("drift").
- Every entry carries its tweak group. `-Action Undo -Module PC -Group 'Scheduling'` undoes one group only,
  which makes per-group A/B testing possible.

## Measurement (`lib/Stats.ps1`)
- Compare accepts several captures per side (`-Before a.csv,b.csv,c.csv -After ...`) and gives each metric a
  95 % interval (wider of Welch t and bootstrap) and a verdict: better / worse / no real difference / not enough data.
- New metrics: time-weighted 1 % low, frame-to-frame jitter (replaces the standard deviation, which counted smooth
  drift as jitter), stutter count and stutter time, input-to-photon latency when PresentMon records it.
- Displayed-frame times (`MsBetweenDisplayChange`) are preferred over present-to-present times.
- ABBA run order (cancels linear drift exactly), latency model, VRR cap formula, stick-response model, placebo-free score.
- The launcher's marketing comment is replaced by a plain description of what it does.

## Tests
- `Test-Stats.ps1` (new) runs in CI. `Test-Roundtrip.ps1 -Seeds 3` adds three runs that start from random existing
  values (absent / other value / other type) to prove Undo(Optimize(S)) = S for more than one starting state.

---

# Changes in this version

Nothing was removed. Everything that worked still works; new behaviour is behind config switches.

## Fixes (found by the real-Windows test)
- Undo now restores a service's DelayedAutostart flag exactly (set it, or remove it) and checks it. (finding A)
- Hibernate journal now records whether `HibernateEnabled` existed and its value; undo restores it exactly. (finding B)
- Restore-point repair is now journaled: VSS/swprv start types and the shadow-storage size are put back on Undo. System Protection is never turned off by Undo (that would delete restore points); a note is shown instead. (point 7)
- Test-Logic.ps1 no longer runs the endless optimizer: it calls `write_tuned` through a helper with a 30 s limit.

## New config switches (config.json), safe defaults
- `HvciOff` default `false` (opt-in). (point 1)
- `DisableToastNotifications` default `false` (opt-in; can hide Windows Security alerts). (point 2)
- `UsePlatformTick` default `false` (opt-in; `disabledynamictick` is kept). (point 4)
- `ReapplyUserPreferences` default `false`: logon re-apply skips the mouse / visual / input group. (point 8)
- `Controller.KeepAwake` default `games` (`always` = old behaviour, `never`). (point 5)
- `Controller.TunedProfilePolicy` default `preserve-edits` (`overwrite` = old behaviour). (point 6)

To get exactly the old behaviour back, put this in `C:\ProgramData\KpekpesCODCustom\config.json`:
`{ "HvciOff": true, "DisableToastNotifications": true, "UsePlatformTick": true, "ReapplyUserPreferences": true, "Controller": { "KeepAwake": "always", "TunedProfilePolicy": "overwrite" } }`

## Controller module
- Controller optimizer: two added flags, `--keep-awake` and `--tuned-policy`. Defaults in the script itself are the old behaviour (`always`, `overwrite`), so running it by hand is unchanged. Everything else is byte-for-byte the same code.
- Tuned profile: a hash file (`.kpekpes-sha256`) remembers what the script wrote; an edited file is never overwritten, and Undo deletes it only if unedited.

## Tests / CI
- New `tests/Test-Roundtrip.ps1` (adapters, Defender, tasks, QoS, boot, services, hibernate, shadow storage; Safe and Full scenarios).
- Simplified `.github/workflows/windows-test.yml`: no patch step, 5 min limit on step 2, 8 min on step 3.
- Logic tests: 45 -> 113 checks.

## NVIDIA power (journaled)

Prefer maximum performance for Call of Duty is set through NVIDIA's NvAPI, not by editing the driver database. The previous value is stored in the undo journal before it is saved. Undo puts that value back, removes an inherited override, removes an exe this tool added, or deletes a profile this tool created. A predefined NVIDIA profile is never deleted. If NvAPI is missing, nothing is written. `Get-NvidiaUndoKind` is covered by `tests/Test-Logic.ps1`.

## Reflex and the in-game cap

The game file sets `NvidiaReflex` to `Enabled + boost`. `CapFps` is turned on and `MaxFpsInGame` is set to one under the primary monitor (200 Hz becomes 199). The game file does not change the Windows refresh rate. If the rate cannot be read, the cap is left alone. Undo still restores the original file.

## Monitor rate

Optimize, and Monitor and frames, raise a monitor only to a refresh rate Windows already lists at the current resolution. 60 Hz on a panel that lists 200 Hz at that resolution becomes 200. A higher rate that exists only at another resolution is reported and not applied. The original mode is journaled first. Undo puts that mode back, and names the monitor if the rate was changed again afterwards. `Select-KRefreshHz` is covered by `tests/Test-Logic.ps1`.

## Measured frames

Record before and Record after run the pinned PresentMon 2.6.0 build for 60 seconds against the named exe. Average FPS, 1% low and 0.1% low are `Get-FrameStats` on the displayed-frame column. If the file is missing, the hash does not match, or there are fewer than 100 frames, the window says nothing was measured. It does not print a zero.

## Startup menu

Startup menu lists the Run keys and the Startup folders. Turning one off writes the Windows startup-approved flag. The program is not deleted. SecurityHealth, Windows Defender and this program are refused. Undo restores the original flag.

## Hitch watch

`-Action Hitch` (menu option 10, and the PC window button "Name the hitch") records about 20 seconds and prints the driver or process behind the longest stall: a delayed call, an interrupt, or a thread wake under 50 ms. It does not write the journal and it does not disable anything. If Windows will not give the trace, it says so and stops. The ranking is covered by `tests/Test-Logic.ps1`. A name does not add frames.
