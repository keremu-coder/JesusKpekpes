using System.Globalization;
using System.Text;

static class StickChecks
{
    public static void Run()
    {
        Check(StickCentre.ForbidReason(Packet(0x82, 1, 1, 1)) == "calibrate-sticks or calibrate-range", "calibrate-sticks must be refused");
        var unlock = new byte[64];
        unlock[0] = 0x80;
        unlock[1] = 3;
        unlock[2] = 2;
        unlock[3] = 101;
        Check(StickCentre.ForbidReason(unlock) == "NVS unlock", "NVS unlock must be refused");
        unlock[2] = 1;
        Check(StickCentre.ForbidReason(unlock) == "NVS lock", "NVS lock must be refused");
        unlock[2] = 3;
        Check(StickCentre.ForbidReason(unlock) == "NVS status", "NVS status must be refused");
        unlock[1] = 21;
        unlock[2] = 6;
        Check(StickCentre.ForbidReason(unlock) == "module flash", "module flash must be refused");

        var edgeRead = StickCentre.ReadRequest(StickKind.Edge);
        var dsRead = StickCentre.ReadRequest(StickKind.DualSense);
        Check(StickCentre.ForbidReason(edgeRead) is null && edgeRead[0] == 0x80 && edgeRead[1] == 12 && edgeRead[2] == 4 && edgeRead[3] == 0, "Edge read is the in-memory query");
        Check(StickCentre.ForbidReason(dsRead) is null && dsRead[2] == 2, "DualSense read is the in-memory query");
        var words = new ushort[12];
        words[0] = 0x1111;
        words[8] = 0xBEEF;
        var write = StickCentre.WriteRequest(words);
        Check(StickCentre.ForbidReason(write) is null && write[1] == 12 && write[2] == 1 && write[3] == 0x11 && write[4] == 0x11 && write[19] == 0xEF && write[20] == 0xBE, "centre words are little-endian and the range word is kept");
        Check(StickCentre.TryParseModule(Module(words), out var parsed) && parsed[0] == 0x1111 && parsed[8] == 0xBEEF, "a module report should parse");
        var bad = Module(words);
        bad[3] = 1;
        Check(!StickCentre.TryParseModule(bad, out _), "a module report with the wrong status is refused");

        Check(StickCentre.BusOf(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0df2") == StickBus.Bluetooth, "the Bluetooth HID path is Bluetooth");
        Check(StickCentre.KindOf(@"\\?\hid#vid_054c&pid_0df2&mi_03#x") == StickKind.Edge, "Edge on USB is accepted");
        Check(StickCentre.KindOf(@"\\?\hid#vid_054c&pid_0ce6&mi_03#x") == StickKind.DualSense, "DualSense on USB is accepted");
        Check(StickCentre.KindOf(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0df2") is null, "Edge over Bluetooth is not a USB pad");
        Check(StickCentre.TryParseUsbSticks(StickCentre.UsbReport(1, 2, 3, 4), out var lx, out var ly, out var rx, out var ry, out var why) && lx == 1 && ly == 2 && rx == 3 && ry == 4 && why is null, "USB report 0x01 carries the sticks");
        var bt = new byte[78];
        bt[0] = 0x31;
        Check(!StickCentre.TryParseUsbSticks(bt, out _, out _, out _, out _, out why) && why == StickCentre.BluetoothRefusal, "a Bluetooth input report is refused");

        Check(StickCentre.CloneReason("Jan 29 2034", true, 2026) == "build_date", "a future firmware date is a clone");
        Check(StickCentre.CloneReason("Jan 29 2024", false, 2026) == "finetune", "a pad that will not answer finetune is refused");
        Check(StickCentre.CloneReason("Jan 29 2024", true, 2026) is null, "a normal date with a finetune answer is not a clone");
        Check(StickCentre.CloneReason("not-a-date", true, 2026) is null, "an unreadable date is not by itself a clone");
        var info = new byte[64];
        info[0] = 0x20;
        Encoding.ASCII.GetBytes("Jan 29 2024").CopyTo(info, 1);
        Check(StickCentre.BuildDate(info) == "Jan 29 2024", "the firmware date is the 11 bytes after the report id");

        Check(StickCentre.Classify(1.5, 0, 0) == RestVerdict.WrongCentre, "a stable offset is a wrong centre");
        Check(StickCentre.Classify(1.49, 0, 0) == RestVerdict.Fine, "under the offset line is not a wrong centre");
        Check(StickCentre.Classify(0, 1.25, 0) == RestVerdict.Worn && StickCentre.Classify(0, 0, 4) == RestVerdict.Worn, "jitter is a worn stick");
        Check(StickCentre.Classify(2, 0, 4) == RestVerdict.Both, "offset plus jitter is both");
        var steady = Frames(140, 128, 128, 128, 40);
        var steadyPicture = StickCentre.Picture(StickKind.Edge, steady, Ticks(40, 1));
        Check(steadyPicture.Left == RestVerdict.WrongCentre && steadyPicture.Right == RestVerdict.Fine, "only the left stick is off centre");
        Check(Math.Abs(steadyPicture.LeftX.OffsetPercent - (140 - 127.5) / 127.5 * 100) < 0.001, "offset percent is from the 127.5 centre");
        var noisy = new List<byte[]>();
        for (var i = 0; i < 40; i++) noisy.Add(StickCentre.UsbReport(128, 128, (byte)(i % 2 == 0 ? 120 : 136), 128));
        var noisyPicture = StickCentre.Picture(StickKind.Edge, noisy, Ticks(40, 1));
        Check(noisyPicture.Right == RestVerdict.Worn, "a jumping right stick looks worn");
        var described = StickCentre.Describe(steadyPicture);
        Check(described.Contains("wrong centre", StringComparison.Ordinal) && described.Contains("8-bit", StringComparison.Ordinal) && !ClaimsInstant(described), "the read says what it measured and does not promise instant input");

        var before = new RateStat(100, 1000, 1);
        Check(!StickCentre.RateWorse(before, new RateStat(100, 900, 1.1)), "exactly 10 percent is not a fail");
        Check(StickCentre.RateWorse(before, new RateStat(100, 899, 1)), "a larger rate drop is a fail");
        Check(StickCentre.RateWorse(before, new RateStat(100, 1000, 1.11)), "a larger gap is a fail");
        Check(StickCentre.RateWorse(before, new RateStat(5, 1000, 1)), "too few reports is a fail");

        var root = Path.Combine(Path.GetTempPath(), "kpk-stick-" + Guid.NewGuid().ToString("N"));
        var store = new StickStore(root);
        var centred = Pad();
        var quiet = StickCentre.Apply(centred, store, StickKind.Edge, 2026);
        Check(quiet.Ok && !quiet.Wrote && centred.WritePackets == 0, "a stick already at rest is not written: " + quiet.Text);

        var moved = Pad();
        moved.Base[0] = 140;
        var applied = StickCentre.Apply(moved, store, StickKind.Edge, 2026);
        Check(applied.Ok && applied.Wrote && applied.Writes is >= 1 and <= 3, "a resting offset is corrected in at most 3 writes: " + applied.Text);
        Check(moved.Words[8] < 2000 && moved.Words[0] == 1111 && moved.Words[7] == 2222, "only the centre word moves, and it moves down when the byte sits high");
        Check(store.TryReadCorrection(out var storedKind, out var stored) && storedKind == StickKind.Edge && stored[8] == moved.Words[8] && stored[0] == 1111, "the correction stored for the next plug keeps the range words");
        Check(!ClaimsInstant(applied.Text) && applied.Text.Contains("10%", StringComparison.Ordinal), "the result quotes the rate check");
        Check(moved.Sets.All(packet => StickCentre.ForbidReason(packet) is null) && moved.Sets.Any(packet => packet[2] == 4) && moved.Sets.All(packet => packet[1] != 3 && packet[0] != 0x82), "the only feature reports are the in-memory read and write");
        var again = StickCentre.Reapply(moved, store, StickKind.Edge, 2026);
        Check(again.Ok && !again.Wrote && moved.WritePackets == applied.Writes, "re-applying an image that is already present does not write");
        moved.Words = (ushort[])moved.Origin.Clone();
        var replugged = StickCentre.Reapply(moved, store, StickKind.Edge, 2026);
        Check(replugged.Ok && replugged.Writes == 1 && moved.Words[8] == stored[8], "a reconnect writes the stored centre once");
        var other = StickCentre.Reapply(moved, store, StickKind.DualSense, 2026);
        Check(!other.Ok && other.Text.Contains("different pad", StringComparison.Ordinal), "a stored Edge centre is not sent to a DualSense");

        var wrong = Pad();
        wrong.Base[0] = 140;
        wrong.Gain = -0.1;
        var backed = StickCentre.Apply(wrong, store, StickKind.Edge, 2026);
        Check(!backed.Ok && backed.Restored && wrong.Words[8] == 2000 && wrong.Words[0] == 1111, "a step that makes the offset worse is restored: " + backed.Text);
        var backup = store.ReadBackup();
        Check(backup is not null && backup[8] == 2000 && backup[0] == 1111, "the backup file is the image from before the write");

        var slow = Pad();
        slow.Base[0] = 140;
        slow.GapAfterWriteMs = 2;
        var rolled = StickCentre.Apply(slow, store, StickKind.Edge, 2026);
        Check(!rolled.Ok && rolled.Restored && slow.Words[8] == 2000, "a slower report rate writes the backup back: " + rolled.Text);

        var lie = Pad();
        lie.Base[0] = 140;
        lie.LieBudget = 1;
        var lied = StickCentre.Apply(lie, store, StickKind.Edge, 2026);
        Check(!lied.Ok && lied.Restored && lie.Words[8] == 2000, "a read-back that does not match is restored");

        var stuck = Pad();
        stuck.Words[8] = 0;
        stuck.Origin[8] = 0;
        stuck.Base[0] = 140;
        var ended = StickCentre.Apply(stuck, store, StickKind.Edge, 2026);
        Check(!ended.Ok && !ended.Wrote && stuck.WritePackets == 0 && ended.Text.Contains("end of its range", StringComparison.Ordinal), "a centre already at the end of the range is not wrapped");

        var clone = Pad();
        clone.BuildDate = "Jan 29 2034";
        clone.Base[0] = 140;
        var refused = StickCentre.Apply(clone, store, StickKind.Edge, 2026);
        Check(!refused.Ok && clone.WritePackets == 0 && refused.Text.Contains("clone", StringComparison.Ordinal), "a clone date is not written");
        var mute = Pad();
        mute.FinetuneMissing = true;
        mute.Base[0] = 140;
        var muteOut = StickCentre.Apply(mute, store, StickKind.Edge, 2026);
        Check(!muteOut.Ok && mute.WritePackets == 0, "a missing finetune answer is not written");

        var radio = Pad();
        radio.BluetoothInput = true;
        radio.Base[0] = 140;
        var radioOut = StickCentre.Apply(radio, store, StickKind.Edge, 2026);
        Check(!radioOut.Ok && radio.WritePackets == 0 && radioOut.Text == StickCentre.BluetoothRefusal, "Bluetooth input during the read stops the write");

        var wide = Pad();
        wide.Base[0] = 160;
        wide.Gain = 0.2;
        var three = StickCentre.Apply(wide, store, StickKind.Edge, 2026);
        Check(three.Writes <= 3 && wide.WritePackets <= 4, "the pad is not written more than three times, plus one restore: " + three.Text);

        store.ClearCorrection();
        var restored = StickCentre.Restore(moved, store, StickKind.Edge, 2026);
        Check(restored.Ok && restored.Restored && !store.HasCorrection, "restore puts the backup back and clears the stored centre: " + restored.Text);

        var read = StickCentre.Test(Pad(sample: true), StickKind.Edge);
        Check(read.Ok && read.Picture is not null && read.Picture.Value.Rate.Hz > 900 && read.Picture.Value.Rate.WorstGapMs <= 1.01 && !ClaimsInstant(read.Text), "a 5 second read reports the rate it actually saw");

        Check(StickCentre.HidHideAdvice.Contains("does not change HidHide", StringComparison.Ordinal) && StickCentre.HidHideAdvice.Contains("JesusKpekpes.exe", StringComparison.Ordinal), "HidHide is explained and not edited");
        Check(StickCentre.BitNote(StickKind.Edge).Contains("12-bit", StringComparison.Ordinal) && StickCentre.BitNote(StickKind.DualSense).Contains("16-bit", StringComparison.Ordinal), "the centre width is named, the USB report stays 8-bit");
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    static bool ClaimsInstant(string text) =>
        text.Contains("zero delay", StringComparison.OrdinalIgnoreCase)
        || text.Contains("0ms", StringComparison.OrdinalIgnoreCase)
        || text.Contains("no delay", StringComparison.OrdinalIgnoreCase);

    static StickFake Pad(bool sample = false)
    {
        var pad = new StickFake();
        if (sample) pad.Base[0] = 140;
        return pad;
    }

    static List<byte[]> Frames(byte lx, byte ly, byte rx, byte ry, int count)
    {
        var frames = new List<byte[]>(count);
        for (var i = 0; i < count; i++) frames.Add(StickCentre.UsbReport(lx, ly, rx, ry));
        return frames;
    }

    static List<long> Ticks(int count, double gapMs)
    {
        var ticks = new List<long>(count);
        for (var i = 0; i < count; i++) ticks.Add((long)(i * gapMs * TimeSpan.TicksPerMillisecond));
        return ticks;
    }

    static byte[] Packet(params byte[] payload) => StickCentre.Feature(payload[0], payload.AsSpan(1));

    static byte[] Module(ushort[] words)
    {
        var report = new byte[64];
        report[0] = 0x81;
        report[1] = 12;
        report[2] = 4;
        report[3] = 2;
        for (var i = 0; i < 12; i++)
        {
            report[4 + i * 2] = (byte)words[i];
            report[5 + i * 2] = (byte)(words[i] >> 8);
        }
        return report;
    }

    static void Check(bool ok, string message)
    {
        if (ok) return;
        Console.Error.WriteLine(message);
        Environment.Exit(1);
    }
}

sealed class StickFake : IStickIo
{
    public ushort[] Words = new ushort[12];
    public ushort[] Origin = new ushort[12];
    public int[] Base = [128, 128, 128, 128];
    public double Gain = 0.1;
    public double GapMs = 1;
    public double GapAfterWriteMs = 1;
    public string BuildDate = "Jan 29 2024";
    public bool FinetuneMissing;
    public bool BluetoothInput;
    public int LieBudget;
    public int WritePackets;
    public List<byte[]> Sets = [];
    int _lieReads;
    long _clock;

    public StickFake()
    {
        for (var i = 0; i < 12; i++) Words[i] = Origin[i] = 2000;
        Words[0] = Origin[0] = 1111;
        Words[7] = Origin[7] = 2222;
    }

    public long Ticks => _clock;

    public void Wait(int milliseconds)
    {
        if (milliseconds != StickCentre.ReadSettleMs) throw new InvalidOperationException("settle");
    }

    public byte[]? ReadInput()
    {
        _clock += (long)((WritePackets > 0 ? GapAfterWriteMs : GapMs) * TimeSpan.TicksPerMillisecond);
        if (BluetoothInput)
        {
            var radio = new byte[78];
            radio[0] = 0x31;
            return radio;
        }
        var report = new byte[64];
        report[0] = 0x01;
        for (var i = 0; i < 4; i++)
        {
            var shift = (int)Math.Round((Words[8 + i] - Origin[8 + i]) * Gain, MidpointRounding.AwayFromZero);
            var raw = Base[i] + shift;
            if (raw < 0) raw = 0;
            if (raw > 255) raw = 255;
            report[1 + i] = (byte)raw;
        }
        return report;
    }

    public byte[] GetFeature(byte reportId)
    {
        if (reportId == 0x20)
        {
            var info = new byte[64];
            info[0] = 0x20;
            var text = Encoding.ASCII.GetBytes(BuildDate);
            Array.Copy(text, 0, info, 1, Math.Min(11, text.Length));
            return info;
        }
        if (FinetuneMissing) return [];
        var words = (ushort[])Words.Clone();
        if (_lieReads > 0)
        {
            _lieReads--;
            words[8] ^= 1;
        }
        return StickChecksModule(words);
    }

    public void SetFeature(byte[] report)
    {
        Sets.Add((byte[])report.Clone());
        if (report.Length < 3 || report[0] != 0x80 || report[2] != 1) return;
        for (var i = 0; i < 12; i++)
            Words[i] = (ushort)(report[3 + i * 2] | (report[4 + i * 2] << 8));
        WritePackets++;
        if (LieBudget > 0)
        {
            _lieReads++;
            LieBudget--;
        }
    }

    static byte[] StickChecksModule(ushort[] words)
    {
        var report = new byte[64];
        report[0] = 0x81;
        report[1] = 12;
        report[2] = 4;
        report[3] = 2;
        for (var i = 0; i < 12; i++)
        {
            report[4 + i * 2] = (byte)words[i];
            report[5 + i * 2] = (byte)(words[i] >> 8);
        }
        return report;
    }
}
