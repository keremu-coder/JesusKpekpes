using System.Text;

namespace JesusKpekpes;

internal static class StickCentreTests
{
#if STICK_CENTRE_B
    internal static void RunReadOnly(Action<bool, string> check)
    {
        using var link = new FakeControllerLink();
        var result = StickTest.Run(link);
        check(result.Supported && result.Completed && result.Duration >= TimeSpan.FromSeconds(5),
            "the read-only stick test completes its five-second sample");
        check(result.LeftStick.HasValue && result.RightStick.HasValue && result.ValidReports > 100,
            "the read-only test returns both sticks and valid reports");
        check(result.ReportRate.Reports == result.ValidReports && result.ReportRate.PerSecond > 0,
            "the read-only test reports the measured input rate");
        check(result.PlainLanguage.Contains("No controller settings were changed", StringComparison.Ordinal),
            "the read-only result explains that settings were not changed");
        check(link.SentReports.Count == 0 && link.FeatureReads == 0,
            "the read-only test sends no feature reports and makes no feature reads");
        check(link.Disposals == 0, "the read-only test leaves link ownership and disposal to its caller");

        var bluetooth = new FakeControllerLink { Usb = false };
        var refused = StickTest.Run(bluetooth);
        check(!refused.Supported && !refused.Completed && bluetooth.InputReads == 0 && bluetooth.SentReports.Count == 0,
            "the read-only test refuses Bluetooth without opening an input sampling loop");
    }
#endif

#if STICK_CENTRE_C
    internal static void RunLive(Action<bool, string> check)
    {
        var protocol = new FakeControllerLink();
        var snapshot = CentreProtocol.ReadCurrent(protocol);
        check(snapshot.Values.Length == 12 && snapshot.Values[10] == 2000,
            "protocol parsing reads twelve little-endian values");
        check(protocol.SentReports.Count == 1 && protocol.SentReports[0].SequenceEqual(new byte[] { 0x80, 12, 4 }),
            "DualSense Edge read framing uses feature command 0x80, command 12, mode 4");
        var overLimit = (ushort[])snapshot.Values.Clone();
        overLimit[11] = 4096;
        var writesBeforeLimitCheck = protocol.SentReports.Count;
        var limitRefused = false;
        try { CentreProtocol.WriteVolatile(protocol, snapshot with { Values = overLimit }); }
        catch (InvalidDataException) { limitRefused = true; }
        check(limitRefused && protocol.SentReports.Count == writesBeforeLimitCheck,
            "DualSense Edge protocol refuses values above 4095 before sending a write");

        var malformed = new FakeControllerLink { InvalidAnswerHeader = true };
        var rejectedHeader = false;
        try { _ = CentreProtocol.ReadCurrent(malformed); }
        catch (InvalidDataException) { rejectedHeader = true; }
        check(rejectedHeader, "protocol rejects an invalid feature-response header");

        var bluetooth = new FakeControllerLink { Usb = false };
        var bluetoothResult = LiveCentre.Apply(bluetooth, Path.GetTempPath());
        check(!bluetoothResult.Success && !bluetoothResult.Applied && bluetooth.SentReports.Count == 0
            && bluetoothResult.Message.Contains("Bluetooth", StringComparison.Ordinal),
            "live calibration refuses Bluetooth before sending a feature command");

        var root = Path.Combine(Path.GetTempPath(), "stick-centre-tests-" + Guid.NewGuid().ToString("N"));
        var backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(root);
        try
        {
            var link = new FakeControllerLink { BackupDirectoryToCheck = backups };
            var original = (ushort[])link.Values.Clone();
            var applied = LiveCentre.Apply(link, backups);
            check(applied.Success && applied.Applied && !applied.RolledBack,
                "live apply makes one bounded change when rate and read-back stay safe: " + applied.Message);
            check(link.VolatileWrites == 1 && link.BackupExistedBeforeFirstWrite,
                "the backup file exists before the sole volatile apply write");
            check(applied.BackupPath is not null && File.Exists(applied.BackupPath),
                "live apply returns a durable backup path");
            check(applied.After is not null && applied.After.ReportRate.Reports >= 50,
                "live apply re-measures the report rate after the write");
            check(link.SentReports.All(IsAllowedVolatileReport),
                "protocol traffic contains only the live read and volatile-write framing, never a save command");

            var ackLink = new FakeControllerLink { AckAfterWrite = true };
            var ackRun = LiveCentre.Apply(ackLink, Path.Combine(root, "ack"));
            check(ackRun.Success && ackRun.Applied && !ackRun.RolledBack,
                "apply still works when the pad answers the first read after a write with its own acknowledgement (real DualSense Edge): " + ackRun.Message);

            var restored = LiveCentre.Restore(link, applied.BackupPath!, backups);
            check(restored.Success && restored.Applied && link.Values.SequenceEqual(original),
                "restore writes the backed-up in-memory values and verifies them");
            check(link.VolatileWrites == 2 && restored.BackupPath is not null && File.Exists(restored.BackupPath),
                "restore makes a safety backup before writing");
            check(link.SentReports.All(IsAllowedVolatileReport),
                "restore also avoids every permanent-save or calibration command");

            var rollbackLink = new FakeControllerLink { CorruptFirstReadBackAfterWrite = true };
            var rollbackOriginal = (ushort[])rollbackLink.Values.Clone();
            var rolledBack = LiveCentre.Apply(rollbackLink, Path.Combine(root, "rollback"));
            check(!rolledBack.Success && rolledBack.RolledBack && rollbackLink.VolatileWrites == 2,
                "an unexpected read-back causes a verified rollback");
            check(rollbackLink.Values.SequenceEqual(rollbackOriginal),
                "rollback restores every original in-memory value");
            check(rollbackLink.SentReports.All(IsAllowedVolatileReport),
                "rollback uses only the volatile write command and no permanent-save command");

            var rateLink = new FakeControllerLink { SlowAfterWrite = true };
            var rateOriginal = (ushort[])rateLink.Values.Clone();
            var rateRollback = LiveCentre.Apply(rateLink, Path.Combine(root, "rate-rollback"));
            check(!rateRollback.Success && rateRollback.RolledBack && rateLink.VolatileWrites == 2,
                "a worsened post-write report rate triggers automatic rollback");
            check(rateLink.Values.SequenceEqual(rateOriginal),
                "rate-triggered rollback restores the original in-memory values");

            // The scale and direction of the centre values are learned from the first step, so the loop must work for either sign.
            foreach (var gain in new[] { -0.1, 0.1 })
            {
                var linear = new FakeControllerLink { LinearGain = gain };
                var run = LiveCentre.Apply(linear, Path.Combine(root, "linear" + gain));
                var finalReading = Math.Round(127.5 + 6.5 + gain * (linear.Values[FakeControllerLink.RightXIndex] - 2000));
                check(run.Success && run.Applied && Math.Abs(finalReading - 127.5) <= 1.5 && linear.VolatileWrites <= 3,
                    $"closed loop centres the stick within three writes when one unit moves it {gain} counts: {run.Message}");
                check(linear.SentReports.All(IsAllowedVolatileReport), "the closed loop sends only the live read and volatile-write framing");
            }

            var dead = new FakeControllerLink { LinearGain = 0.0000001 };
            var deadValues = (ushort[])dead.Values.Clone();
            var deadRun = LiveCentre.Apply(dead, Path.Combine(root, "dead"));
            check(!deadRun.Success && deadRun.RolledBack && dead.Values.SequenceEqual(deadValues),
                "values that do not move the reading are undone and reported, not kept");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    internal static void RunCalibration(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "stick-calib-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            check(StickCalibration.HeaderOf([0x83, 1, 1, 1, 9]) == 0x83010101u && StickCalibration.HeaderOf([0x81, 1, 1, 1]) == 0u && StickCalibration.HeaderOf(null) == 0u,
                "a 0x83 answer is read as one 32-bit header, anything else as none");

            var edge = new FakeControllerLink { EffectOfCentreCalibration = FakeControllerLink.CalibrationEffect.Fixed };
            var ok = StickCalibration.CalibrateCentre(edge, Path.Combine(root, "edge"));
            check(ok.Success && ok.BackupPath is not null && File.Exists(ok.BackupPath), "Edge centre calibration succeeds and keeps a backup: " + ok.Message);
            var expected = new[] { "1,1,1", "3,1,1", "3,1,1", "3,1,1", "3,1,1", "2,1,1", "2,1,1" };
            check(edge.CalibrationCommands.SequenceEqual(expected),
                "Edge sends begin, four samples, then the two-step end, exactly as the reference tool: " + string.Join(" ", edge.CalibrationCommands));
            check(!edge.SentReports.Any(r => r[0] == 0x80 && r.Length > 1 && (r[1] == 3 || r[1] == 21)),
                "centre calibration never sends a permanent-storage command");

            var plain = new FakeControllerLink { DeviceModel = "DualSense", EffectOfCentreCalibration = FakeControllerLink.CalibrationEffect.Fixed };
            var plainRun = StickCalibration.CalibrateCentre(plain, Path.Combine(root, "plain"));
            check(plainRun.Success && plain.CalibrationCommands.Count(c => c == "2,1,1") == 1, "a standard DualSense ends with one command: " + plainRun.Message);

            var worse = new FakeControllerLink { EffectOfCentreCalibration = FakeControllerLink.CalibrationEffect.Worse };
            var worseValues = (ushort[])worse.Values.Clone();
            var worseRun = StickCalibration.CalibrateCentre(worse, Path.Combine(root, "worse"));
            check(!worseRun.Success && worseRun.Message.Contains("did not get better", StringComparison.Ordinal) && worse.Values.SequenceEqual(worseValues),
                "a calibration that leaves the stick farther off centre is undone and reported: " + worseRun.Message);

            var refused = new FakeControllerLink { RefuseCentreBegin = true };
            var refusedRun = StickCalibration.CalibrateCentre(refused, Path.Combine(root, "refused"));
            check(!refusedRun.Success && refused.CalibrationCommands.Count == 1 && refusedRun.Message.Contains("Nothing was changed", StringComparison.Ordinal),
                "a pad that refuses to begin is left alone: " + refusedRun.Message);

            var bluetooth = new FakeControllerLink { Usb = false };
            var bluetoothRun = StickCalibration.CalibrateCentre(bluetooth, root);
            check(!bluetoothRun.Success && bluetooth.CalibrationCommands.Count == 0, "Bluetooth is refused before any calibration command");

            var moving = new FakeControllerLink { StickMovesDuringSampling = true, EffectOfCentreCalibration = FakeControllerLink.CalibrationEffect.Fixed };
            var movingRun = StickCalibration.CalibrateCentre(moving, Path.Combine(root, "moving"));
            check(!movingRun.Success && !moving.CalibrationCommands.Contains("2,1,1") && movingRun.Message.Contains("not finished", StringComparison.Ordinal),
                "if the stick is touched mid-run, the run is abandoned and the end command is never sent: " + movingRun.Message);

            var range = new FakeControllerLink();
            var begin = StickCalibration.BeginRange(range, Path.Combine(root, "range"));
            var finish = StickCalibration.FinishRange(range);
            check(begin.Success && finish.Success && range.CalibrationCommands.SequenceEqual(["1,1,2", "2,1,2", "2,1,2"]),
                "Edge range calibration begins, then ends in two steps: " + string.Join(" ", range.CalibrationCommands));
            var rangePlain = new FakeControllerLink { DeviceModel = "DualSense" };
            _ = StickCalibration.BeginRange(rangePlain, Path.Combine(root, "range2"));
            check(StickCalibration.FinishRange(rangePlain).Success, "a standard DualSense ends range calibration in one step");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    internal static void RunSave(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "stick-save-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var edge = new FakeControllerLink();
            edge.Values[3] = 2222;
            var saved = StickSave.Save(edge, Path.Combine(root, "edge"));
            check(saved.Success && saved.BackupPath is not null && File.Exists(saved.BackupPath), "Edge save succeeds and keeps a backup: " + saved.Message);
            check(edge.PersistedValues is not null && edge.PersistedValues.SequenceEqual(edge.Values),
                "the values reached the stick modules (written back while both modules and storage were unlocked)");
            check(!edge.StorageOpen && !edge.ModuleOpen[0] && !edge.ModuleOpen[1], "everything is locked again after a save");
            check(edge.SaveCommands.SequenceEqual(["21,6,0", "21,6,1", "3,2", "21,4,0", "21,4,1", "3,1"]),
                "Edge sends unlock module 0, 1, storage, then lock modules 0, 1, storage: " + string.Join(" ", edge.SaveCommands));

            var plain = new FakeControllerLink { DeviceModel = "DualSense" };
            var plainSaved = StickSave.Save(plain, Path.Combine(root, "plain"));
            check(plainSaved.Success && plain.SaveCommands.SequenceEqual(["3,2", "3,1"]) && !plain.StorageOpen,
                "a standard DualSense unlocks and locks the storage once: " + plainSaved.Message);

            var stuck = new FakeControllerLink { RefuseRightModuleUnlock = true };
            var stuckRun = StickSave.Save(stuck, Path.Combine(root, "stuck"));
            check(!stuckRun.Success && stuck.PersistedValues is null && !stuck.ModuleOpen[0] && !stuck.ModuleOpen[1] && !stuck.StorageOpen,
                "if a module will not unlock, nothing is written and whatever was opened is locked again: " + stuckRun.Message);

            var noLock = new FakeControllerLink { RightModuleNeverLocks = true };
            var noLockRun = StickSave.Save(noLock, Path.Combine(root, "nolock"));
            check(!noLockRun.Success && noLockRun.Message.Contains("Unplug and replug", StringComparison.Ordinal) && !noLock.StorageOpen,
                "if a module will not lock, the person is told to replug and the storage is still locked: " + noLockRun.Message);

            var bluetooth = new FakeControllerLink { Usb = false };
            var bluetoothRun = StickSave.Save(bluetooth, root);
            check(!bluetoothRun.Success && bluetooth.SaveCommands.Count == 0, "Bluetooth is refused before any save command");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static bool IsAllowedVolatileReport(byte[] report) =>
        report.Length >= 3 && report[0] == 0x80 && report[1] == 12
        && (report[2] == 1 && report.Length == 27 || report[2] == 4 && report.Length == 3);
#endif
}
