# Stick centre: handoff for several AIs

Branch: `stick-centre` in keremu-coder/JesusKpekpes. Never push to `main`. The owner decides about `main`.

## What we are building

A "Stick centre" tab in Jesus Kpekpes that removes a resting stick spin the way the community
calibration tool does, but applied live each time the owner opens the tab, with no input delay and
nothing saved into the controller. The owner's controller is a PS5 DualSense Edge over USB, used with
DS4Windows. The calibration tool already helped him once, so the fix is real. The job is to do it
without a layer in the input path.

Input delay and report rate are the top priority. Any design that adds a hop, a loop or a background
thread while a game is running is wrong, even if it works.

## Hard rules (every package)

1. Nothing in the input path. Open the controller for the test or the apply, for well under a second, then close it.
   No feature-report traffic while gaming. No background thread while idle.
2. Live, in-memory only. NEVER call the NVS unlock, NVS lock, flash/save, calibrate-sticks or
   calibrate-range commands. Saving into the controller stays with the original web tool.
3. Back up the current in-memory values to a file before any write. Provide a restore.
4. Apply once, read back, re-measure, stop. At most 3 small, clamped adjustments. Abort and restore on anything unexpected.
5. Measure report rate and worst gap before and after. If it got worse (`StickMath.GotWorse`), restore automatically.
6. Never write "zero delay" in the UI, a commit or a note unless the measurements show it.
7. USB only. Refuse Bluetooth and say why. Refuse clones. Accept only DualSense (054C:0CE6) and DualSense Edge (054C:0DF2).
8. Open with shared access so DS4Windows keeps working. If HidHide hides the pad, detect it and explain. Do not change HidHide.
9. No drivers, no injection, no touching the game process.
10. Verify before you claim. Mark anything you could not test as untested.

## Protocol notes (a summary of the MIT-licensed web tool, NOT verified on hardware)

Source: github.com/dualshock-tools/dualshock-tools.github.io at commit
b7cdcfaacfadfac5e80f4f40b16e85503a488e63. Read `js/controllers/ds5-controller.js`,
`js/controllers/ds5-edge-controller.js` and `js/core.js` yourself and trust them over this summary.
Keep the tool's copyright and licence notice in anything derived from it.

- Feature report 0x80 sends a command, feature report 0x81 returns the answer.
- Live read of the in-memory calibration data: command `[12, 2]`, wait about 100 ms, read 0x81, validate the header, then read 12 little-endian u16 values.
- Live write: command `[12, 1]` followed by the 12 u16 values.
- The permanent save is a separate NVS unlock and lock. Do not implement it.
- Open question: which of the 12 values are the stick centres, and whether the Edge differs. Work package C must answer this from the source and then confirm it on a real controller.

## Contracts

- `app/StickCentre/IControllerLink.cs`: the only door to the controller. Use it, do not bypass it.
- `app/StickCentre/StickMath.cs`: offset and noise per axis, the verdict wording, report rate stats, and the "got worse" rule. Its limits are PROVISIONAL.

## Work packages

Each AI takes one letter. Only edit the files you own.

| Pkg | Owns | Job | Done when |
|---|---|---|---|
| A | `app/StickCentre/WinHidLink.cs` and any native helper | `IControllerLink` over Windows HID (hid.dll): find the pad, USB check, shared access, input read with a timeout, feature get/set, short-lived handle, clear errors for access denied and for HidHide | Opens, reads and closes on a real pad; fake-free unit seams documented |
| B | `app/StickCentre/StickTest.cs` | Stage 1, read-only: read 5 s hands off, call `StickMath`, return per-stick results and the report rate | Result object and plain-language text; works against a fake link |
| C | `app/StickCentre/LiveCentre.cs`, `app/StickCentre/CentreProtocol.cs` | Stage 2: protocol port, backup, closed-loop apply with read-back, rollback, rate check | Passes the fake-link tests; hardware checklist items 1 to 6 pass |
| D | `app/StickCentreForm.cs`, menu entry, config persistence | The tab: Test, Apply my centre, Restore, status, auto-apply when the tab opens and when the pad reconnects (device-arrival event, no polling loop) | Tab works end to end; nothing runs while idle |
| E | `tests/`, `docs/`, review | Fake `IControllerLink`, unit tests in the style of `tests/Program.cs`, the only person who edits `tests/EngineTests.csproj` and `tests/Program.cs`, merges the PRs, audits the others against the hard rules | CI green, audit notes in the progress log |

## Working together

- Branch from `stick-centre` as `stick-centre/<letter>-<short-name>`, and open a PR back into `stick-centre`.
- Package E merges after CI is green. The owner decides about `main`.
- Do not edit another package's files. If you need a change there, add an entry under "Requests" below.
- CI is the `Check` workflow. It runs on every push. Read its result, do not guess.
- Keep this file current: update the status board and add a progress-log line whenever you finish something.

## Status board

| Pkg | Status | Notes |
|---|---|---|
| A | not started | |
| B | not started | `StickMath` exists |
| C | not started | |
| D | not started | |
| E | not started | |

## Requests (between packages)

(none yet)

## Hardware checklist (the owner runs this with the real DualSense Edge)

1. Controller on USB, DS4Windows closed. Open the tab, press Test with hands off. The numbers match what DS4Windows Controller Readings shows.
2. Note the report rate and worst gap.
3. Press Apply. Re-run the test. The resting offset is smaller. The report rate and worst gap are not worse.
4. Press Restore. The offset returns to what it was.
5. Unplug and replug. The tab re-applies the centre without a click.
6. Open DS4Windows and play for a few minutes. No spin at rest, and aim feels as fast as before.
7. Unplug and power-cycle the pad. Confirm nothing was saved into it (the spin returns until the tab applies again).

## Progress log

- 2026-11-25, Claude: created the branch, `StickMath`, `IControllerLink` and this file. The sandbox has no .NET, so none of it was compiled. The first Check run on the branch is the first real build result.

## Paste this to any AI you hand a package to

> You are joining a multi-AI build on the repo keremu-coder/JesusKpekpes, branch `stick-centre`.
> Read `docs/STICK_CENTRE.md` completely, then take Work package <LETTER> and only edit the files it owns.
> Follow the hard rules. Input delay and report rate come first. Work on a sub-branch and open a PR into
> `stick-centre`. Never push to `main`. Read the Check result in GitHub Actions instead of assuming. Say what you tested and what
> you could not. Update the status board and the progress log. Finish with a short report of files changed,
> what passed, what failed, and what is blocked.
