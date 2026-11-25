# Stick centre: handoff for several AIs

Branch: `stick-centre` in keremu-coder/JesusKpekpes. Never push to `main`. The owner decides about `main`.

PUSH EARLY AND OFTEN. Work that is not pushed to GitHub is lost when your usage limit ends, and the owner
cannot recover it. Push after every meaningful step, even if it is unfinished. If you cannot push, stop and
tell the owner at once instead of carrying on.

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

- Branch names: Git cannot hold a branch called `stick-centre` and a branch called `stick-centre/x` at the same
  time, so do NOT put a slash after `stick-centre`. Branch from `stick-centre` as `sc-<letter>-<short-name>`,
  for example `sc-a-hid` or `sc-c-live`. Open a PR back into `stick-centre`.
- Package E merges after CI is green. The owner decides about `main`.
- Do not edit another package's files. If you need a change there, add an entry under "Requests" below.
- CI is the `Check` workflow. It runs on every push. Read its result, do not guess.
- Keep this file current: update the status board and add a progress-log line whenever you finish something.

## Status board

| Pkg | Status | Notes |
|---|---|---|
| A | PR #16 open | Branch `sc-a-hid`. `WinHidLink` implements `IControllerLink` and registers itself as `StickCentreForm.OpenController`. Check/engine and Check/windows passed (it compiles). Never run against a real pad: hardware checklist items 1 to 7 are untested. |
| B | merged; spec conflict unresolved | PR #12 Checks passed and the implementation is read-only, but its required five-second open sampling window conflicts with hard rule 1's under-one-second handle limit. The explicit B task says five seconds; owner resolution is needed before treating this as compliant or using the test on hardware. |
| C | open; audit fix requested | PR #13 Checks pass, but apply/restore exceed the under-one-second hard limit: two 480 ms measurement windows plus two 100 ms protocol waits already total about 1.16 s before HID overhead. Not merged pending a bounded-time fix and test. |
| D | merged; safety follow-up required | PR #14 Checks passed, but auto-apply defaults on and runs on open/reconnect with no guard for an active game, conflicting with the rule against feature-report traffic while gaming. UI still cannot reach a real controller until package A supplies `OpenController`. |
| E | PR #15 open | Branch `sc-e-tests`; fake link and conditional B/C tests pushed. Focused B/C fake suites passed against the open C source in a temporary worktree; app build succeeded. The full Linux runner still stops later at an unrelated existing `nova cinema` C/C# comparison. |

## Requests (between packages)

- Package C: reduce the measured apply/restore path to comply with hard rule 1 (controller access well under one second), including feature-report waits and rate windows. Current source has two 480 ms windows and two 100 ms waits, so its minimum is already about 1.16 seconds. Add or run fake-link coverage for the bounded duration and re-run the Check workflow before requesting merge.
- Package B / spec owner: resolve the conflict between B's required five-second input sample and hard rule 1's under-one-second open-handle bound. B reads input reports only and sends no feature reports, but it retains the controller link for the full five seconds.
- Package D: prevent reconnect auto-apply from issuing feature reports while a game is active. The merged form enables auto-apply by default and has no game-active guard; package E does not own the form code. Review the default and safe activation policy before wiring package A/C into a usable build.
- Package A: done in PR #16 (see status board). Once it is merged, the UI reaches a real pad. Needs the owner to run hardware checklist items 1 and 2 first.
- Package E: add tests for `HidRules` (`ModelFor`, `LooksUsb`, `Prefer`, `Explain`) in `tests/Program.cs`. They need no hardware.

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
- 2026-11-25, Claude: fixed the branch naming rule. The first version said `stick-centre/<letter>-<name>`, which Git cannot create while the branch `stick-centre` exists. Use `sc-<letter>-<name>`. A first AI's work was started before this and never reached GitHub, so nothing from it is on any branch.
- 2026-11-25, Package E: audited PRs #12–#14 and read their GitHub Actions Check results. B (#12) and D (#14) were merged after green checks; follow-up audit found unresolved hard-rule conflicts: B's five-second sampling window exceeds the generic handle bound, and D's default reconnect auto-apply has no game-active guard. C (#13) is green but held because its 480 ms before/after windows and two 100 ms protocol waits exceed one second before HID overhead. These findings are recorded for follow-up; no real controller or hardware checklist was available.
- 2026-11-25, Package E: added `FakeControllerLink` and console tests for B and, conditionally, C. The focused B/C fake suites passed against C's current PR source copied into a temporary worktree. The overall Linux runner still fails later at the existing `nova cinema: C and C# differ by 0.016184654` comparison, so a fully green local runner is not claimed. The Windows app build succeeded with two existing warnings; real controller and hardware checks remain untested.
- 2026-11-25, Package E: PR #15's final Check run on commit `f72d586` (Actions run 45565535579) passed both `Check/engine` and `Check/windows`. PR #15 remains open for the owner to merge into `stick-centre`; PRs #12 and #14 are merged with the follow-up issues above, and PR #13 remains held for the timing fix.

- 2026-11-25, Package A: added `app/StickCentre/WinHidLink.cs`. A handle exists only between Open and Dispose; finding the pad uses a no-access handle to read vendor and product number; the pad is then opened with shared read and write, so DS4Windows keeps working; reads are overlapped and cancelled on timeout; opening sends no feature report; HidHide is detected from its service key and explained, never changed. No thread, timer or watcher. Check passed on both jobs. Not tested on a real DualSense Edge. USB is judged by the 64-byte input report and the device path; a clone with the same IDs would not be told apart.

## Paste this to any AI you hand a package to

> You are joining a multi-AI build on the repo keremu-coder/JesusKpekpes, branch `stick-centre`.
> Read `docs/STICK_CENTRE.md` completely, then take Work package <LETTER> and only edit the files it owns.
> Follow the hard rules. Input delay and report rate come first. Work on a branch named `sc-<letter>-<name>`
> (no slash after `stick-centre`) and open a PR into `stick-centre`. Never push to `main`. Push after every
> meaningful step, because unpushed work is lost when your limit ends. Read the Check result in GitHub Actions
> instead of assuming. Say what you tested and what you could not. Update the status board and the progress log.
> Finish with a short report of files changed, what passed, what failed, and what is blocked.
