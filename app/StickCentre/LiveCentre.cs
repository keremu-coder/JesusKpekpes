using System.Diagnostics;
using System.Text.Json;

namespace JesusKpekpes;

// Explicit-call-only controller action. No worker, polling service, or input-path hook lives here.
// The caller owns the short-lived IControllerLink; this class never disposes it.
internal static class LiveCentre
{
    // Time: one 100 ms response wait to read, one 150 ms window to measure, then about 250 ms per write (100 ms wait plus a window).
    // Typical run is two writes, about 0.75 s; the budget stops a third write if the run is already long.
    private static readonly TimeSpan RateWindow = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TimeBudget = TimeSpan.FromMilliseconds(800); // stop starting new writes after this
    private const int ReadTimeoutCapMs = 50;
    private const int MinReportsForRateComparison = 25;
    private const int MaxWrites = 3;
    private const int MinStepUnits = 5;
    private const float TargetPercent = StickMath.OffsetWarnPercent;
    private const float MinGainPerUnit = 0.003f;
    private const byte UsbInputReportId = 0x01;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal readonly record struct StickObservation(
        StickMath.AxisStats X,
        StickMath.AxisStats Y,
        StickMath.StickVerdict Verdict);

    internal sealed record Measurement(
        int ValidReports,
        int IgnoredReports,
        StickObservation LeftStick,
        StickObservation RightStick,
        StickMath.RateStats ReportRate);

    internal sealed record OperationResult(
        bool Success,
        bool Applied,
        bool RolledBack,
        string Message,
        string? BackupPath,
        Measurement? Before,
        Measurement? After,
        Measurement? AfterRollback);

    private sealed record BackupFile(int SchemaVersion, string Model, byte Mode, DateTime CreatedUtc, ushort[] Values);

    /// <summary>
    /// Closed-loop volatile centre correction. Measure; back up; step each off-centre axis by a bounded amount sized from the measured
    /// offset; read the values back; re-measure; learn from the result how far one unit moves the reading (and in which direction);
    /// finish the correction. At most three writes, each clamped, all in memory only. Any surprise (bad read-back, a slower report
    /// rate, a centre that moved farther away, an axis that does not respond) puts the original values back and verifies them.
    /// </summary>
    internal static OperationResult Apply(IControllerLink link, string backupDirectory)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrWhiteSpace(backupDirectory))
            return Fail("Choose a backup folder before applying calibration.");

        Measurement? before = null;
        string? backupPath = null;
        CentreProtocol.CalibrationSnapshot? original = null;
        var writeAttempted = false;
        var clock = Stopwatch.StartNew();
        try
        {
            original = CentreProtocol.ValidateDevice(link);
            before = Measure(link);
            if (before.ReportRate.Reports < MinReportsForRateComparison)
                return Fail("Too few USB reports were received to safely compare report rate. No calibration was written.", before);

            var axes = BuildAxes(original, before);
            if (axes.Count == 0)
                return new OperationResult(true, false, false,
                    "No stable off-centre axis needs a centre correction. Nothing was written.", null, before, null, null);

            backupPath = WriteBackup(backupDirectory, original);
            var values = (ushort[])original.Values.Clone();
            var max = CentreProtocol.MaxValueFor(original.Model);
            var firstCap = Math.Max(1, max / 100);
            var totalCap = firstCap * 3;
            var after = before;
            writeAttempted = true;

            for (var write = 0; write < MaxWrites; write++)
            {
                var moved = false;
                foreach (var axis in axes.Where(a => !a.Done))
                {
                    var wanted = write == 0 ? FirstStep(axis, max, firstCap) : Refine(axis, firstCap * 2);
                    // Never drift further than the total cap from the original value, and stay inside the controller's range.
                    var target = Math.Clamp(values[axis.Index] + wanted, Math.Max(0, original.Values[axis.Index] - totalCap), Math.Min(max, original.Values[axis.Index] + totalCap));
                    var actual = target - values[axis.Index];
                    if (actual == 0) { axis.Done = true; continue; }
                    values[axis.Index] = (ushort)target;
                    axis.LastDelta = actual;
                    moved = true;
                }
                if (!moved) break;

                var proposed = original with { Values = (ushort[])values.Clone() };
                CentreProtocol.WriteVolatile(link, proposed);
                var readBack = CentreProtocol.ReadCurrent(link);
                if (!CentreProtocol.ValuesEqual(proposed, readBack))
                    throw new InvalidDataException("The controller's read-back did not match the requested centre values.");

                after = Measure(link);
                if (StickMath.GotWorse(before.ReportRate, after.ReportRate))
                    return RolledBackResult(link, original, backupPath, before, after,
                        "Report rate or worst gap got worse, so the original in-memory calibration was restored.",
                        "Report rate or worst gap got worse, but automatic restore could not be verified. Use the saved backup to restore manually.");

                foreach (var axis in axes.Where(a => a.LastDelta != 0))
                {
                    var now = Observed(after, axis.Index).OffsetPercent;
                    axis.Gain = (axis.CurrentPercent - now) / axis.LastDelta; // percent of travel removed per unit written
                    axis.CurrentPercent = now;
                    axis.Writes++;
                    if (Math.Abs(now) < TargetPercent) axis.Done = true;
                }
                if (axes.All(a => a.Done) || clock.Elapsed > TimeBudget) break;
            }

            // Judge the result. Every touched axis must have moved toward neutral; any axis that got farther away undoes everything.
            var worse = axes.Where(a => a.Writes > 0 && Math.Abs(a.CurrentPercent) > Math.Abs(a.StartPercent) + 0.1f).ToList();
            var improved = axes.Where(a => a.Writes > 0 && Math.Abs(a.CurrentPercent) < Math.Abs(a.StartPercent) - 0.1f).ToList();
            if (worse.Count > 0)
                return RolledBackResult(link, original, backupPath, before, after,
                    "The centre reading moved farther from neutral, so the original in-memory calibration was restored.",
                    "The centre reading worsened and automatic restore could not be verified. Use the saved backup to restore manually.");
            if (improved.Count == 0)
                return RolledBackResult(link, original, backupPath, before, after,
                    "The controller accepted the values but the centre reading did not move, so nothing was kept. Its calibration layout may differ from what this app expects.",
                    "The centre reading did not move and automatic restore could not be verified. Use the saved backup to restore manually.");

            var summary = string.Join(", ", improved.Select(a =>
                $"{CentreName(a.Index)} {StickMath.CountsFromPercent(a.StartPercent):+0.00;-0.00;0.00} → {StickMath.CountsFromPercent(a.CurrentPercent):+0.00;-0.00;0.00} counts"));
            return new OperationResult(true, true, false,
                $"Centre corrected in memory in {clock.ElapsedMilliseconds} ms: {summary}. Values were read back and report rate was not worse. Nothing was saved to controller storage.",
                backupPath, before, after, null);
        }
        catch (Exception ex)
        {
            if (writeAttempted && original is not null)
            {
                var rollback = RollBack(link, original);
                Measurement? afterRollback = null;
                try { afterRollback = Measure(link); } catch { /* report the rollback outcome even if measurement fails */ }
                return new OperationResult(false, false, rollback.Restored,
                    rollback.Restored
                        ? $"Apply stopped safely and the original in-memory calibration was restored: {ex.Message}"
                        : $"Apply stopped, but automatic restore could not be verified: {ex.Message}. Use the saved backup to restore manually.",
                    backupPath, before, null, afterRollback);
            }
            return Fail($"Calibration was not written: {ex.Message}", before, backupPath);
        }
    }

    private sealed class Axis
    {
        internal int Index;
        internal float StartPercent;
        internal float CurrentPercent;
        internal int LastDelta;
        internal float Gain;
        internal int Writes;
        internal bool Done;
    }

    private static OperationResult RolledBackResult(IControllerLink link, CentreProtocol.CalibrationSnapshot original, string? backupPath,
        Measurement before, Measurement after, string restoredMessage, string failedMessage)
    {
        var rollback = RollBack(link, original);
        Measurement? afterRollback = null;
        try { afterRollback = Measure(link); } catch { /* retain the verified restore result */ }
        return new OperationResult(false, false, rollback.Restored, rollback.Restored ? restoredMessage : failedMessage,
            backupPath, before, after, afterRollback);
    }

    private static List<Axis> BuildAxes(CentreProtocol.CalibrationSnapshot original, Measurement before)
    {
        var axes = new List<Axis>(4);
        void Add(int index, StickObservation stick, StickMath.AxisStats axis)
        {
            if (stick.Verdict.EnoughData && stick.Verdict.CentreOff && !stick.Verdict.Noisy && Math.Abs(axis.OffsetPercent) >= StickMath.OffsetWarnPercent)
                axes.Add(new Axis { Index = index, StartPercent = axis.OffsetPercent, CurrentPercent = axis.OffsetPercent });
        }
        Add(CentreProtocol.LeftXCentreIndex, before.LeftStick, before.LeftStick.X);
        Add(CentreProtocol.LeftYCentreIndex, before.LeftStick, before.LeftStick.Y);
        Add(CentreProtocol.RightXCentreIndex, before.RightStick, before.RightStick.X);
        Add(CentreProtocol.RightYCentreIndex, before.RightStick, before.RightStick.Y);
        return axes;
    }

    // The first step is a guess at the scale (about max/200 units per percent of travel), capped small. A raised centre value is
    // expected to lower the reading, so a positive offset gets a positive step. The result of this step teaches the real scale and
    // direction, and a wrong guess is caught by the final check and undone.
    private static int FirstStep(Axis axis, int max, int cap)
    {
        var units = (int)Math.Round(Math.Abs(axis.StartPercent) * (max / 200.0));
        units = Math.Clamp(units, MinStepUnits, cap);
        return axis.StartPercent > 0 ? units : -units;
    }

    private static int Refine(Axis axis, int cap)
    {
        if (Math.Abs(axis.Gain) < MinGainPerUnit) { axis.Done = true; return 0; } // the axis does not respond: stop touching it
        var units = (int)Math.Round(axis.CurrentPercent / axis.Gain);
        return Math.Clamp(units, -cap, cap);
    }

    private static StickMath.AxisStats Observed(Measurement m, int index) => index switch
    {
        CentreProtocol.LeftXCentreIndex => m.LeftStick.X,
        CentreProtocol.LeftYCentreIndex => m.LeftStick.Y,
        CentreProtocol.RightXCentreIndex => m.RightStick.X,
        CentreProtocol.RightYCentreIndex => m.RightStick.Y,
        _ => throw new InvalidDataException("Unknown centre axis index.")
    };

    /// <summary>Restore a previous Apply backup, first backing up the current state for rollback.</summary>
    internal static OperationResult Restore(IControllerLink link, string backupFilePath, string safetyBackupDirectory)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrWhiteSpace(backupFilePath) || string.IsNullOrWhiteSpace(safetyBackupDirectory))
            return Fail("A restore file and a safety-backup folder are required.");

        Measurement? before = null;
        string? safetyBackupPath = null;
        CentreProtocol.CalibrationSnapshot? current = null;
        var writeAttempted = false;
        try
        {
            current = CentreProtocol.ValidateDevice(link);
            var saved = ReadBackup(backupFilePath);
            if (!string.Equals(saved.Model, link.Model, StringComparison.Ordinal))
                throw new InvalidDataException("This backup belongs to a different controller model.");
            var target = new CentreProtocol.CalibrationSnapshot(saved.Model, saved.Mode, saved.Values);
            ValidateValues(target);

            before = Measure(link);
            if (before.ReportRate.Reports < MinReportsForRateComparison)
                return Fail("Too few USB reports were received to safely compare report rate. Restore was not written.", before);

            safetyBackupPath = WriteBackup(safetyBackupDirectory, current);
            writeAttempted = true;
            CentreProtocol.WriteVolatile(link, target);
            var readBack = CentreProtocol.ReadCurrent(link);
            if (!CentreProtocol.ValuesEqual(target, readBack))
                throw new InvalidDataException("The restored values did not match the read-back.");

            var after = Measure(link);
            if (StickMath.GotWorse(before.ReportRate, after.ReportRate))
            {
                var rollback = RollBack(link, current);
                Measurement? afterRollback = null;
                try { afterRollback = Measure(link); } catch { }
                return new OperationResult(false, false, rollback.Restored,
                    rollback.Restored
                        ? "Restore would have worsened report rate or worst gap, so the pre-restore in-memory values were put back."
                        : "Restore worsened report rate and the safety rollback could not be verified.",
                    safetyBackupPath, before, after, afterRollback);
            }

            return new OperationResult(true, true, false,
                "The selected backup was restored in memory, read back successfully, and report rate was not worse. No permanent save was issued.",
                safetyBackupPath, before, after, null);
        }
        catch (Exception ex)
        {
            if (writeAttempted && current is not null)
            {
                var rollback = RollBack(link, current);
                Measurement? afterRollback = null;
                try { afterRollback = Measure(link); } catch { }
                return new OperationResult(false, false, rollback.Restored,
                    rollback.Restored
                        ? $"Restore stopped safely and the pre-restore in-memory values were restored: {ex.Message}"
                        : $"Restore stopped, but its safety rollback could not be verified: {ex.Message}",
                    safetyBackupPath, before, null, afterRollback);
            }
            return Fail($"Restore was not written: {ex.Message}", before, safetyBackupPath);
        }
    }

    /// <summary>Collect a short USB rate and centre sample only while Apply/Restore is explicitly running.</summary>
    internal static Measurement Measure(IControllerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var lx = new List<byte>(256);
        var ly = new List<byte>(256);
        var rx = new List<byte>(256);
        var ry = new List<byte>(256);
        var arrivalMs = new List<double>(256);
        var ignored = 0;
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < RateWindow)
        {
            var remaining = RateWindow - Stopwatch.GetElapsedTime(started);
            var timeoutMs = Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds), 1, ReadTimeoutCapMs);
            var report = link.ReadInputReport(timeoutMs);
            if (Stopwatch.GetElapsedTime(started) >= RateWindow) break;
            if (report is null)
            {
                Thread.Sleep(1);
                continue;
            }
            if (report.Length < 5 || report[0] != UsbInputReportId)
            {
                ignored++;
                continue;
            }

            lx.Add(report[1]);
            ly.Add(report[2]);
            rx.Add(report[3]);
            ry.Add(report[4]);
            arrivalMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var leftX = StickMath.Analyse(lx);
        var leftY = StickMath.Analyse(ly);
        var rightX = StickMath.Analyse(rx);
        var rightY = StickMath.Analyse(ry);
        return new Measurement(arrivalMs.Count, ignored,
            new StickObservation(leftX, leftY, StickMath.Judge(leftX, leftY)),
            new StickObservation(rightX, rightY, StickMath.Judge(rightX, rightY)),
            StickMath.AnalyseRate(arrivalMs));
    }

    private static (bool Restored, string? Error) RollBack(IControllerLink link, CentreProtocol.CalibrationSnapshot original)
    {
        try
        {
            CentreProtocol.WriteVolatile(link, original);
            var readBack = CentreProtocol.ReadCurrent(link);
            if (!CentreProtocol.ValuesEqual(original, readBack))
                return (false, "Original values did not match after rollback.");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string WriteBackup(string directory, CentreProtocol.CalibrationSnapshot snapshot)
    {
        ValidateValues(snapshot);
        Directory.CreateDirectory(directory);
        var fileName = $"stick-centre-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(directory, fileName);
        var backup = new BackupFile(1, snapshot.Model, snapshot.Mode, DateTime.UtcNow, (ushort[])snapshot.Values.Clone());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(backup, JsonOptions);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static BackupFile ReadBackup(string path)
    {
        var backup = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The selected backup is empty or invalid.");
        if (backup.SchemaVersion != 1 || backup.Values is null || backup.Values.Length != CentreProtocol.ValueCount)
            throw new InvalidDataException("The selected backup has an unsupported format.");
        return backup;
    }

    private static void ValidateValues(CentreProtocol.CalibrationSnapshot snapshot)
    {
        if (snapshot.Values is null || snapshot.Values.Length != CentreProtocol.ValueCount)
            throw new InvalidDataException("Calibration data must contain exactly 12 values.");
        var max = CentreProtocol.MaxValueFor(snapshot.Model);
        if (snapshot.Values.Any(v => v > max))
            throw new InvalidDataException($"Calibration values exceed the {snapshot.Model} limit of {max}.");
    }

    private static string CentreName(int index) => index switch
    {
        CentreProtocol.LeftXCentreIndex => "left X centre",
        CentreProtocol.LeftYCentreIndex => "left Y centre",
        CentreProtocol.RightXCentreIndex => "right X centre",
        CentreProtocol.RightYCentreIndex => "right Y centre",
        _ => $"value {index}"
    };

    private static OperationResult Fail(string message, Measurement? before = null, string? backupPath = null) =>
        new(false, false, false, message, backupPath, before, null, null);
}
