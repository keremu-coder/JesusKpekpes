namespace JesusKpekpes;

// Saves the pad's current in-memory stick calibration into its permanent storage, so it survives unplugging.
// Follows the dualshock-tools reference (ds5-controller.js flash(), ds5-edge-controller.js flashModules()):
//
//   DualSense      : nvs unlock [0x80, 3, 2, 101, 50, 64, 12]  then  nvs lock [0x80, 3, 1]
//   DualSense Edge : unlock left and right stick module [0x80, 21, 6, i, 11] (answer 21 6 2)
//                    nvs unlock, read the live finetune values, write them back while unlocked (this is what reaches the modules),
//                    lock both modules [0x80, 21, 4, i, 8] (answer 21 4 2), nvs lock.
//
// This is the one operation here that changes something that stays after unplugging. So it is only ever run on an explicit
// press and confirmation, keeps a backup first, refuses a pad that is being held, and always tries to lock the storage again
// if anything goes wrong halfway. It runs once and is not part of any game's input path.
// PROVISIONAL: matches the reference source and the fake pad. Not yet run on a real pad.
internal static class StickSave
{
    internal readonly record struct Outcome(bool Success, string Message, string? BackupPath);

    private const byte CommandReport = 0x80;
    private const byte AnswerReport = 0x81;
    private const int AnswerLength = 64;
    private const int AckAttempts = 10;
    private const int AckGapMs = 50;

    internal static Outcome Save(IControllerLink link, string backupDirectory)
    {
        ArgumentNullException.ThrowIfNull(link);
        string? backup = null;
        var storageOpen = false;
        var modulesOpen = new bool[2];
        try
        {
            var live = CentreProtocol.ValidateDevice(link);
            var quiet = PadQuiet.Check(link, 400);
            if (!quiet.Quiet)
                return new Outcome(false, quiet.Message.Replace("nothing was changed", "nothing was saved"), null);

            backup = LiveCentre.WriteBackup(backupDirectory, live);

            if (link.Model != "DualSense Edge")
            {
                storageOpen = true;
                Send(link, [3, 2, 101, 50, 64, 12], "Unlocking the controller's storage");
                _ = link.GetFeatureReport(AnswerReport, AnswerLength);
                Thread.Sleep(50);
                Send(link, [3, 1], "Locking the controller's storage");
                _ = link.GetFeatureReport(AnswerReport, AnswerLength);
                storageOpen = false;
                return new Outcome(true, "Saved into the controller. Backup of the values before saving: " + Path.GetFileName(backup), backup);
            }

            Thread.Sleep(100);
            for (var i = 0; i < 2; i++)
            {
                Send(link, [21, 6, (byte)i, 11], i == 0 ? "Unlocking the left stick module" : "Unlocking the right stick module");
                modulesOpen[i] = true;
                Thread.Sleep(200);
                if (!WaitFor(link, [21, 6, 2]))
                    throw new IOException(i == 0 ? "The left stick module did not unlock." : "The right stick module did not unlock.");
            }

            storageOpen = true;
            Send(link, [3, 2, 101, 50, 64, 12], "Unlocking the controller's storage");
            _ = link.GetFeatureReport(AnswerReport, AnswerLength);
            Thread.Sleep(50);

            // The reference reads the live values and writes the same values straight back while everything is unlocked.
            // That write is what reaches the stick modules.
            var current = CentreProtocol.ReadCurrent(link);
            if (!current.Values.SequenceEqual(live.Values))
            {
                // The values moved between the first read and now. Saving something nobody looked at is refused.
                throw new InvalidDataException("The live calibration values changed while saving was starting, so nothing was written.");
            }
            Thread.Sleep(50);
            CentreProtocol.WriteVolatile(link, current);
            Thread.Sleep(100);

            for (var i = 0; i < 2; i++)
            {
                Send(link, [21, 4, (byte)i, 8], i == 0 ? "Locking the left stick module" : "Locking the right stick module");
                Thread.Sleep(200);
                if (!WaitFor(link, [21, 4, 2]))
                    throw new IOException(i == 0 ? "The left stick module did not lock again." : "The right stick module did not lock again.");
                modulesOpen[i] = false;
            }
            Thread.Sleep(100);
            Send(link, [3, 1], "Locking the controller's storage");
            _ = link.GetFeatureReport(AnswerReport, AnswerLength);
            storageOpen = false;
            Thread.Sleep(250);

            var after = CentreProtocol.ReadCurrent(link);
            if (!after.Values.SequenceEqual(current.Values))
                return new Outcome(false, "Saved, but the controller's values read back different from what was saved. Unplug and replug it, press Test, and compare. Backup: " + Path.GetFileName(backup), backup);

            return new Outcome(true,
                "Saved into the controller's permanent storage. Unplug and replug it, then press Test to confirm the centre stayed. Backup of the values before saving: " + Path.GetFileName(backup),
                backup);
        }
        catch (Exception ex)
        {
            var relocked = Relock(link, modulesOpen, storageOpen);
            return new Outcome(false,
                "Saving stopped: " + ex.Message + (relocked
                    ? " The controller's storage was locked again."
                    : " Locking the storage again could not be confirmed. Unplug and replug the controller before doing anything else.")
                + (backup is null ? " Nothing was written." : " Backup: " + Path.GetFileName(backup)), backup);
        }
    }

    private static void Send(IControllerLink link, byte[] arguments, string step)
    {
        var report = new byte[arguments.Length + 1];
        report[0] = CommandReport;
        Array.Copy(arguments, 0, report, 1, arguments.Length);
        if (!link.SendFeatureReport(report))
            throw new IOException(step + " was refused" + (string.IsNullOrEmpty(link.LastFailure) ? "." : " (" + link.LastFailure + ")."));
    }

    // The pad answers 0x81 with the command echoed in bytes 1 to 3, e.g. 21 6 2. Polled up to half a second, as the reference does.
    private static bool WaitFor(IControllerLink link, byte[] expected)
    {
        for (var attempt = 0; attempt < AckAttempts; attempt++)
        {
            var answer = link.GetFeatureReport(AnswerReport, AnswerLength);
            if (answer is { Length: >= 4 } && answer[0] == AnswerReport && expected.Select((b, i) => answer[1 + i] == b).All(x => x)) return true;
            Thread.Sleep(AckGapMs);
        }
        return false;
    }

    // Best effort, always attempted after a failure: close anything this run opened.
    private static bool Relock(IControllerLink link, bool[] modulesOpen, bool storageOpen)
    {
        var all = true;
        try
        {
            for (var i = 0; i < 2; i++)
            {
                if (!modulesOpen[i]) continue;
                Send(link, [21, 4, (byte)i, 8], "Locking a stick module");
                Thread.Sleep(200);
                all &= WaitFor(link, [21, 4, 2]);
            }
            if (storageOpen)
            {
                Send(link, [3, 1], "Locking the controller's storage");
                _ = link.GetFeatureReport(AnswerReport, AnswerLength);
            }
        }
        catch (Exception) { return false; }
        return all;
    }
}
