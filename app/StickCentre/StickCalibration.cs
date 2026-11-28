using System.Diagnostics;
using System.Globalization;

namespace JesusKpekpes;

// The pad's own stick-centre and stick-range calibration, as the dualshock-tools reference does it (js/controllers/ds5-controller.js,
// ds5-edge-controller.js, modals/calib-center-modal.js): feature report 0x82 commands, answers on 0x83.
//
//   centre : begin [1,1,1]  -> 0x83010101    sample x4 [3,1,1] -> 0x83010101 (stick untouched)
//            end   [2,1,1]  -> DualSense 0x83010102 | Edge 0x83010101 then a second [2,1,1] -> 0x83010103 or 0x83010312
//   range  : begin [1,1,2]  -> 0x83010201    (the person turns both sticks fully round)
//            end   [2,1,2]  -> DualSense 0x83010202 | Edge 0x83010201 then a second [2,1,2] -> 0x83010203
//
// Nothing here saves into the controller's permanent storage (no 0x80 nvs commands). Every run keeps a backup of the live
// finetune values first and, for the centre run, undoes itself if a stick rests farther from the middle than before.
// PROVISIONAL: verified against the reference source and the fake pad only. Not yet run on a real pad.
internal static class StickCalibration
{
    internal readonly record struct Outcome(bool Success, string Message, string? BackupPath);

    private const byte CommandReport = 0x82;
    private const byte AnswerReport = 0x83;
    private const int AnswerLength = 64;
    private const int CentreSamples = 4;
    private const int PollAttempts = 6;
    private const int PollGapMs = 20;
    private const int SampleSettleMs = 150;
    private const int QuietWaitMs = 3000;
    private const float WorseMarginPercent = 0.1f;

    // ---- pure protocol pieces, tested without a pad ----

    internal static uint HeaderOf(byte[]? answer) =>
        answer is { Length: >= 4 } && answer[0] == AnswerReport
            ? (uint)(answer[0] << 24 | answer[1] << 16 | answer[2] << 8 | answer[3])
            : 0u;

    internal static string Hex(uint header) => header.ToString("X8", CultureInfo.InvariantCulture);

    // Send one 0x82 command and wait for one of the accepted 0x83 answers. The pad can answer a moment late, so the
    // answer is polled a few times (about 0.1 s) rather than read once.
    internal static bool Command(IControllerLink link, byte[] arguments, out uint got, params uint[] accepted)
    {
        var report = new byte[arguments.Length + 1];
        report[0] = CommandReport;
        Array.Copy(arguments, 0, report, 1, arguments.Length);
        got = 0;
        if (!link.SendFeatureReport(report)) return false;
        for (var attempt = 0; attempt < PollAttempts; attempt++)
        {
            Thread.Sleep(PollGapMs);
            got = HeaderOf(link.GetFeatureReport(AnswerReport, AnswerLength));
            if (Array.IndexOf(accepted, got) >= 0) return true;
        }
        return false;
    }

    private static string Why(IControllerLink link, string step, uint got) =>
        got == 0
            ? $"{step}: the controller sent no answer{DetailOf(link)}."
            : $"{step}: the controller answered {Hex(got)}, not what the reference tool expects{DetailOf(link)}.";

    private static string DetailOf(IControllerLink link) => string.IsNullOrEmpty(link.LastFailure) ? "" : " (" + link.LastFailure + ")";

    private static bool IsEdge(IControllerLink link) => link.Model == "DualSense Edge";

    private static bool End(IControllerLink link, byte kind, out uint got)
    {
        // kind 1 = centre, 2 = range
        var first = kind == 1 ? 0x83010101u : 0x83010201u;
        if (!IsEdge(link))
            return Command(link, [2, 1, kind], out got, kind == 1 ? 0x83010102u : 0x83010202u);
        if (!Command(link, [2, 1, kind], out got, first)) return false;
        return kind == 1
            ? Command(link, [2, 1, kind], out got, 0x83010103u, 0x83010312u)
            : Command(link, [2, 1, kind], out got, 0x83010203u);
    }

    // ---- centre ----

    /// <summary>
    /// Centre calibration with the sticks untouched. Refuses to start, and stops before writing anything, if the pad is
    /// being held. After it, a short measurement decides whether the rest point really got better; if not, the values
    /// from before are written back.
    /// </summary>
    internal static Outcome CalibrateCentre(IControllerLink link, string backupDirectory)
    {
        ArgumentNullException.ThrowIfNull(link);
        string? backup = null;
        CentreProtocol.CalibrationSnapshot? original = null;
        var begun = false;
        try
        {
            original = CentreProtocol.ValidateDevice(link);
            var quiet = PadQuiet.Check(link, 400);
            if (!quiet.Quiet) return new Outcome(false, quiet.Message.Replace("nothing was changed", "centre calibration did not start"), null);

            var before = LiveCentre.Measure(link);
            if (before.ReportRate.Reports < 25)
                return new Outcome(false, "Too few USB reports arrived to calibrate safely. Nothing was changed.", null);

            backup = LiveCentre.WriteBackup(backupDirectory, original);

            if (!Command(link, [1, 1, 1], out var got, 0x83010101u))
                return new Outcome(false, Why(link, "Centre calibration did not start", got) + " Nothing was changed. Backup: " + Path.GetFileName(backup), backup);
            begun = true;

            for (var i = 1; i <= CentreSamples; i++)
            {
                Thread.Sleep(SampleSettleMs);
                if (!WaitUntilQuiet(link))
                    return Abandoned(link, original, backup, "A stick or the controller moved while it was sampling the centre.");
                if (!Command(link, [3, 1, 1], out got, 0x83010101u))
                    return Abandoned(link, original, backup, Why(link, $"Centre sample {i} of {CentreSamples} failed", got));
            }

            if (!End(link, 1, out got))
                return Abandoned(link, original, backup, Why(link, "Storing the centre in memory failed", got));
            begun = false;

            var after = LiveCentre.Measure(link);
            var verdict = Compare(before, after);
            if (verdict.Worse)
            {
                var restored = TryRestore(link, original);
                return new Outcome(false,
                    "The rest point did not get better (" + verdict.Summary + "), so the earlier in-memory values were " +
                    (restored ? "put back and checked." : "NOT confirmed back. Unplug and replug the controller, then use Restore from backup.") +
                    " Backup: " + Path.GetFileName(backup), backup);
            }

            return new Outcome(true,
                "Centre calibrated in memory. " + verdict.Summary + " Nothing was saved into the controller's permanent storage. Backup: " + Path.GetFileName(backup),
                backup);
        }
        catch (Exception ex)
        {
            var tail = begun ? " The calibration had already started: unplug and replug the controller to be sure it is back to normal." : "";
            return new Outcome(false, "Centre calibration stopped: " + ex.Message + tail + (backup is null ? "" : " Backup: " + Path.GetFileName(backup)), backup);
        }
    }

    private static bool WaitUntilQuiet(IControllerLink link)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (PadQuiet.Check(link, 200).Quiet) return true;
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > QuietWaitMs) return false;
        }
    }

    private static Outcome Abandoned(IControllerLink link, CentreProtocol.CalibrationSnapshot original, string backup, string reason) =>
        new(false, reason + " The calibration was not finished, so nothing was stored. Unplug and replug the controller to clear the half-started run. Backup: " + Path.GetFileName(backup), backup);

    private static bool TryRestore(IControllerLink link, CentreProtocol.CalibrationSnapshot original)
    {
        try
        {
            CentreProtocol.WriteVolatile(link, original);
            return CentreProtocol.ValuesEqual(original, CentreProtocol.ReadCurrent(link));
        }
        catch (Exception) { return false; }
    }

    internal readonly record struct Comparison(bool Worse, string Summary);

    // Each stick axis: how far its resting mean sits from the middle, before and after. Any axis that moved farther away
    // by more than a small margin counts as worse.
    internal static Comparison Compare(LiveCentre.Measurement before, LiveCentre.Measurement after)
    {
        var axes = new (string Name, StickMath.AxisStats Before, StickMath.AxisStats After)[]
        {
            ("left X", before.LeftStick.X, after.LeftStick.X),
            ("left Y", before.LeftStick.Y, after.LeftStick.Y),
            ("right X", before.RightStick.X, after.RightStick.X),
            ("right Y", before.RightStick.Y, after.RightStick.Y),
        };
        var worse = false;
        var parts = new List<string>();
        foreach (var (name, b, a) in axes)
        {
            if (b.Samples < 20 || a.Samples < 20) continue;
            if (Math.Abs(a.OffsetPercent) > Math.Abs(b.OffsetPercent) + WorseMarginPercent) worse = true;
            parts.Add($"{name} {StickMath.CountsFromPercent(b.OffsetPercent):+0.00;-0.00;0.00} → {StickMath.CountsFromPercent(a.OffsetPercent):+0.00;-0.00;0.00} counts");
        }
        return new Comparison(worse, parts.Count == 0 ? "No axis had enough readings to compare." : string.Join(", ", parts) + ".");
    }

    // ---- range ----

    /// <summary>Starts range calibration. The person then turns both sticks fully round, and the run is finished with <see cref="FinishRange"/>.</summary>
    internal static Outcome BeginRange(IControllerLink link, string backupDirectory)
    {
        ArgumentNullException.ThrowIfNull(link);
        string? backup = null;
        try
        {
            var original = CentreProtocol.ValidateDevice(link);
            backup = LiveCentre.WriteBackup(backupDirectory, original);
            if (!Command(link, [1, 1, 2], out var got, 0x83010201u))
                return new Outcome(false, Why(link, "Range calibration did not start", got) + " Nothing was changed. Backup: " + Path.GetFileName(backup), backup);
            return new Outcome(true,
                "Range calibration is running. Turn BOTH sticks slowly and fully round their edge a few times, then press Finish range. If you close this window first, unplug and replug the controller. Backup: " + Path.GetFileName(backup), backup);
        }
        catch (Exception ex)
        {
            return new Outcome(false, "Range calibration stopped: " + ex.Message, backup);
        }
    }

    internal static Outcome FinishRange(IControllerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        try
        {
            if (!End(link, 2, out var got))
                return new Outcome(false, Why(link, "Storing the range in memory failed", got) + " Unplug and replug the controller to clear it, or use Restore from backup.", null);
            return new Outcome(true, "Range calibrated in memory. Nothing was saved into the controller's permanent storage.", null);
        }
        catch (Exception ex)
        {
            return new Outcome(false, "Range calibration stopped: " + ex.Message + " Unplug and replug the controller to be sure it is back to normal.", null);
        }
    }
}
