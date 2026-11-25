using System.Diagnostics;
using System.Text.Json;

namespace JesusKpekpes;

// Explicit-call-only controller action. No worker, polling service, or input-path hook lives here.
// The caller owns the short-lived IControllerLink; this class never disposes it.
internal static class LiveCentre
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromMilliseconds(480);
    private const int ReadTimeoutCapMs = 50;
    private const int MinReportsForRateComparison = 100;
    private const int CentreStep = 5;
    private const int MaxAxisAdjustments = 3;
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
    private sealed record Adjustment(int Index, float OffsetPercent, ushort Before, ushort After);

    /// <summary>
    /// Measure, backup, apply one report, read back and re-measure. At most three centre fields
    /// change, each by the upstream tool's default five-unit step, clamped to the controller limit.
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
        try
        {
            original = CentreProtocol.ValidateDevice(link);
            before = Measure(link);
            if (before.ReportRate.Reports < MinReportsForRateComparison)
                return Fail("Too few USB reports were received to safely compare report rate. No calibration was written.", before);

            var adjustments = BuildAdjustments(original, before);
            if (adjustments.Count == 0)
                return new OperationResult(true, false, false,
                    "No stable off-centre axis needs a small centre adjustment. Nothing was written.", null, before, null, null);

            backupPath = WriteBackup(backupDirectory, original);
            var changedValues = (ushort[])original.Values.Clone();
            foreach (var adjustment in adjustments) changedValues[adjustment.Index] = adjustment.After;
            var proposed = original with { Values = changedValues };

            // From this point a failed send may still have reached the controller; every error rolls back.
            writeAttempted = true;
            CentreProtocol.WriteVolatile(link, proposed);
            var readBack = CentreProtocol.ReadCurrent(link);
            if (!CentreProtocol.ValuesEqual(proposed, readBack))
                throw new InvalidDataException("The controller's read-back did not match the requested centre values.");

            var after = Measure(link);
            if (StickMath.GotWorse(before.ReportRate, after.ReportRate))
            {
                var rollback = RollBack(link, original);
                Measurement? afterRollback = null;
                try { afterRollback = Measure(link); } catch { /* retain the verified restore result */ }
                return new OperationResult(false, false, rollback.Restored,
                    rollback.Restored
                        ? "Report rate or worst gap got worse, so the original in-memory calibration was restored."
                        : "Report rate or worst gap got worse, but automatic restore could not be verified. Use the saved backup to restore manually.",
                    backupPath, before, after, afterRollback);
            }

            if (TouchedCentreWorsened(adjustments, after))
            {
                var rollback = RollBack(link, original);
                Measurement? afterRollback = null;
                try { afterRollback = Measure(link); } catch { /* retain the verified restore result */ }
                return new OperationResult(false, false, rollback.Restored,
                    rollback.Restored
                        ? "The centre reading moved farther from neutral, so the original in-memory calibration was restored."
                        : "The centre reading worsened and automatic restore could not be verified. Use the saved backup to restore manually.",
                    backupPath, before, after, afterRollback);
            }

            var changes = string.Join(", ", adjustments.Select(a => $"{CentreName(a.Index)} {a.Before}→{a.After}"));
            return new OperationResult(true, true, false,
                $"Applied {adjustments.Count} small volatile centre adjustment(s) ({changes}); values were read back and report rate was not worse. Nothing was saved to controller storage.",
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

    private static List<Adjustment> BuildAdjustments(CentreProtocol.CalibrationSnapshot original, Measurement before)
    {
        var candidates = new List<(int Index, float Offset)>();
        AddIfStableOffCentre(candidates, CentreProtocol.LeftXCentreIndex, before.LeftStick, before.LeftStick.X);
        AddIfStableOffCentre(candidates, CentreProtocol.LeftYCentreIndex, before.LeftStick, before.LeftStick.Y);
        AddIfStableOffCentre(candidates, CentreProtocol.RightXCentreIndex, before.RightStick, before.RightStick.X);
        AddIfStableOffCentre(candidates, CentreProtocol.RightYCentreIndex, before.RightStick, before.RightStick.Y);

        var max = CentreProtocol.MaxValueFor(original.Model);
        var adjustments = new List<Adjustment>(MaxAxisAdjustments);
        foreach (var candidate in candidates.OrderByDescending(c => Math.Abs(c.Offset)).Take(MaxAxisAdjustments))
        {
            var delta = candidate.Offset > 0 ? CentreStep : -CentreStep;
            var next = Math.Clamp((int)original.Values[candidate.Index] + delta, 0, max);
            if (next == original.Values[candidate.Index]) continue;
            adjustments.Add(new Adjustment(candidate.Index, candidate.Offset, original.Values[candidate.Index], (ushort)next));
        }
        return adjustments;
    }

    private static void AddIfStableOffCentre(List<(int Index, float Offset)> candidates, int index, StickObservation stick, StickMath.AxisStats axis)
    {
        if (stick.Verdict.EnoughData && stick.Verdict.CentreOff && !stick.Verdict.Noisy && Math.Abs(axis.OffsetPercent) >= 1f)
            candidates.Add((index, axis.OffsetPercent));
    }

    private static bool TouchedCentreWorsened(IReadOnlyList<Adjustment> adjustments, Measurement after)
    {
        foreach (var adjustment in adjustments)
        {
            var observed = adjustment.Index switch
            {
                CentreProtocol.LeftXCentreIndex => after.LeftStick.X,
                CentreProtocol.LeftYCentreIndex => after.LeftStick.Y,
                CentreProtocol.RightXCentreIndex => after.RightStick.X,
                CentreProtocol.RightYCentreIndex => after.RightStick.Y,
                _ => throw new InvalidDataException("Unknown centre axis index.")
            };
            if (Math.Abs(observed.OffsetPercent) > Math.Abs(adjustment.OffsetPercent) + 0.1f) return true;
        }
        return false;
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
