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

### Clarification of rule 1 (2026-11-26, written by the AI that took over all packages at the owner's request; the owner should confirm)
The one-second limit is for anything that sends feature reports or writes: Apply, Restore and rest corrections. Read-only input sampling that the owner starts (the 5 s stick test, the 60 s glitch capture) may run longer, because it only reads reports the pad already sends, on its own shared handle, and sends nothing. Nothing may run by itself without the pad-in-use guard (`PadQuiet`). Auto-apply is off by default.

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
| D | guard added | Auto-apply is now off by default and skips itself when a button, trigger, stick or the pad is moving (`PadQuiet`). Glitch capture button added. |
| E | tests added | Fake link now waits exactly 2 ms per report. New tests: rest watch, glitch capture, pad guard, HID rules, closed loop with either direction and an axis that does not respond. |

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
| A | merged (PR #16) | Windows HID link, rest watch, glitch capture, pad-in-use guard. Never run on a real pad. |
| B | merged | Read-only. The five-second sample is allowed as a read-only exception to rule 1 (see the clarification under Hard rules). |
| C | merged (PR #13) + closed loop | Measures, takes a bounded step, learns the scale and direction from the result, finishes in at most three writes, restores on any surprise. Typical run about 0.75 s, stops starting writes after 0.8 s. Protocol (mode 4 on the Edge, centre values at indices 8 to 11) NOT checked against upstream source here: the source was unreachable from the authoring sandbox. |
| D | guard added | Auto-apply is now off by default and skips itself when a button, trigger, stick or the pad is moving (`PadQuiet`). Glitch capture button added. |
| E | tests added | Fake link now waits exactly 2 ms per report. New tests: rest watch, glitch capture, pad guard, HID rules, closed loop with either direction and an axis that does not respond. |

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

- 2026-11-25, Package E: audited PRs #12–#14 and read their GitHub Actions Check results. B (#12) and D (#14) were merged after green checks; follow-up audit found unresolved hard-rule conflicts: B's five-second sampling window exceeds the generic handle bound, and D's default reconnect auto-apply has no game-active guard. C (#13) is green but held because its 480 ms before/after windows and two 100 ms protocol waits exceed one second before HID overhead. These findings are recorded for follow-up; no real controller or hardware checklist was available.
- 2026-11-25, Package E: added `FakeControllerLink` and console tests for B and, conditionally, C. The focused B/C fake suites passed against C's current PR source copied into a temporary worktree. The overall Linux runner still fails later at the existing `nova cinema: C and C# differ by 0.016184654` comparison, so a fully green local runner is not claimed. The Windows app build succeeded with two existing warnings; real controller and hardware checks remain untested.
- 2026-11-25, Package E: PR #15's final Check run on commit `f72d586` (Actions run 45565535579) passed both `Check/engine` and `Check/windows`. PR #15 remains open for the owner to merge into `stick-centre`; PRs #12 and #14 are merged with the follow-up issues above, and PR #13 remains held for the timing fix.

- 2026-11-25, Package A: added `app/StickCentre/WinHidLink.cs`. A handle exists only between Open and Dispose; finding the pad uses a no-access handle to read vendor and product number; the pad is then opened with shared read and write, so DS4Windows keeps working; reads are overlapped and cancelled on timeout; opening sends no feature report; HidHide is detected from its service key and explained, never changed. No thread, timer or watcher. Check passed on both jobs. Not tested on a real DualSense Edge. USB is judged by the 64-byte input report and the device path; a clone with the same IDs would not be told apart.

- 2026-11-26, Package A (owner request: fix a stick that suddenly stops returning to centre, with no added delay): added `app/StickCentre/RestWatch.cs`, a pure state machine fed with input reports. It says "stick stuck off centre" only after 3 s with no button, both triggers released, the gyro showing the pad lying still, the stick steady, and the offset between 2.5 and 14 counts. It opens nothing, has no thread and sends nothing. Limits are provisional. Not run on hardware. Check passed.
  - Package C: expose one correction call, `ApplyRestCorrection(link, side, offsetX, offsetY)`: backup, one clamped adjustment, read-back, rate check, restore on any surprise, all under one second.
  - Package D: an opt-in switch (off by default) that feeds RestWatch from reports the app already reads, and calls C only on a StuckOffCentre verdict. It must never send a feature report while the pad is in use (RestWatch returns Active for that).
  - Package E: tests for RestWatch with synthetic reports (quiet pad, held pad, both sticks off, offset too big, cooldown).
  - Open: whether a background read while a game runs is acceptable. It needs its own shared handle, and the owner decides after the hardware checklist shows the report rate is unchanged.

- 2026-11-26, Package A (owner symptom: the stick sometimes snaps to an extreme or gives an instant 360 turn, and in the calibrator it sometimes rests perfectly and sometimes drifts to the end): added `app/StickCentre/GlitchTest.cs`. It is read-only and reads the pad's own input reports, so it can tell a fault in the controller from one in DS4Windows or the game. It counts moves of more than 40 counts from rest while the gyro says the pad was not bumped, and says plainly when the stick sensor is at fault. Check passed. Not run on hardware. A fault like this cannot be calibrated away; RestWatch deliberately ignores offsets above 14 counts.
  - Package D: add a "Glitch capture (60 s, hands off)" button that calls `GlitchTest.Run`. It is user-started only, reads only, and sends no feature report. It runs far longer than one second, so the owner has to accept that for a read-only test, as with Package B.
  - Package E: tests for `GlitchTest.Analyse` with synthetic samples: quiet pad, one false spike, a spike while the pad moves, a stick stuck at the end.

- 2026-11-26, Package C/D/E (all packages taken over at the owner's request): (1) merged A (#16) and C (#13). (2) The held calibration moved the centre by only 5 units per run, which cannot correct a real 3 to 6 percent offset; replaced with a closed loop that sizes the first step from the measured offset (capped at max/100 units, 40 on the Edge), learns the real scale and direction from the first write, and finishes in at most three writes (cap 3 x first step from the original), stopping early under 0.75 percent. If the reading does not move, or moves away, everything is restored. (3) The offset judge needed 50 readings per axis but a 150 ms window gives about 37 on a real pad, so Apply would never have fired; the limit is now 25 for both the judge and the rate comparison. (4) Fake link used Sleep(1) (about 15 ms on Windows); now exact 2 ms. (5) Auto-apply default off; automatic runs skip when the pad is in use. (6) Added Glitch capture button. Windows and engine Check passed. NOT DONE: nothing has run on a real DualSense Edge; the protocol layout could not be compared with the upstream source from here; hardware checklist items 1 to 7 are untested.

## Paste this to any AI you hand a package to

> You are joining a multi-AI build on the repo keremu-coder/JesusKpekpes, branch `stick-centre`.
> Read `docs/STICK_CENTRE.md` completely, then take Work package <LETTER> and only edit the files it owns.
> Follow the hard rules. Input delay and report rate come first. Work on a branch named `sc-<letter>-<name>`
> (no slash after `stick-centre`) and open a PR into `stick-centre`. Never push to `main`. Push after every
> meaningful step, because unpushed work is lost when your limit ends. Read the Check result in GitHub Actions
> instead of assuming. Say what you tested and what you could not. Update the status board and the progress log.
> Finish with a short report of files changed, what passed, what failed, and what is blocked.
