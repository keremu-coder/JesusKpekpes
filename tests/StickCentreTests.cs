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

    private static bool IsAllowedVolatileReport(byte[] report) =>
        report.Length >= 3 && report[0] == 0x80 && report[1] == 12
        && (report[2] == 1 && report.Length == 27 || report[2] == 4 && report.Length == 3);
#endif
}
