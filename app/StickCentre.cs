using System.Globalization;
using System.Text;

namespace JesusKpekpes;

// In-memory DualSense / DualSense Edge stick centre.
//
// The feature-report commands and the 12-word layout below follow the
// MIT-licensed tool dualshock-tools by the_al, commit
// b7cdcfaacfadfac5e80f4f40b16e85503a488e63
// (js/controllers/ds5-controller.js, js/controllers/ds5-edge-controller.js, js/core.js).
// That code reads the in-memory module with report 0x80 payload [12, 2] (DualSense)
// or [12, 4] (Edge), and writes it back with [12, 1] plus twelve little-endian words:
// LL LT RL RT LR LB RR RB LX LY RX RY. Centre mode edits only the last four.
// Their centre step moves LX/LY/RX/RY by a small integer. A positive reported axis
// is pulled back by decreasing that word (d-pad right / circle is a negative X step).
//
// Copyright (c) 2024 the_al
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// This file never builds an NVS unlock, NVS lock, flash/save, module unlock,
// calibrate-sticks, or calibrate-range report. Those stay in the reference tool
// and are refused here.

enum StickKind { DualSense, Edge }

enum StickBus { Usb, Bluetooth, Other }

enum RestVerdict { Fine, WrongCentre, Worn, Both }

enum StickJob { Test, Apply, Reapply, Restore }

readonly record struct AxisStat(double MeanRaw, double StdDevRaw, int PeakToPeak, int Count)
{
    public double OffsetCounts => MeanRaw - StickCentre.CentreRaw;
    public double OffsetPercent => OffsetCounts / StickCentre.HalfTravel * 100.0;
    public double NoisePercent => StdDevRaw / StickCentre.HalfTravel * 100.0;
    public RestVerdict Verdict => StickCentre.Classify(OffsetCounts, StdDevRaw, PeakToPeak);
}

readonly record struct RateStat(int Count, double Hz, double WorstGapMs);

readonly record struct StickPicture(StickKind Kind, AxisStat LeftX, AxisStat LeftY, AxisStat RightX, AxisStat RightY, RateStat Rate)
{
    public AxisStat[] Axes => [LeftX, LeftY, RightX, RightY];
    public RestVerdict Left => StickCentre.Combine(LeftX.Verdict, LeftY.Verdict);
    public RestVerdict Right => StickCentre.Combine(RightX.Verdict, RightY.Verdict);
}

sealed class StickOutcome
{
    public bool Ok;
    public bool Wrote;
    public bool Restored;
    public int Writes;
    public string Text = "";
    public StickPicture? Picture;
    public ushort[]? Words;
}

interface IStickIo
{
    long Ticks { get; }
    byte[]? ReadInput();
    byte[] GetFeature(byte reportId);
    void SetFeature(byte[] report);
    void Wait(int milliseconds);
}

/// <summary>The app's own copy of the centre. Not the controller's flash.</summary>
sealed class StickStore
{
    public const string BackupName = "stick-centre-backup.txt";
    public const string CorrectionName = "stick-centre.txt";

    readonly string _backup;
    readonly string _correction;

    public StickStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _backup = Path.Combine(directory, BackupName);
        _correction = Path.Combine(directory, CorrectionName);
    }

    public static StickStore Default => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JesusKpekpes"));

    public bool HasCorrection => File.Exists(_correction);
    public string BackupPath => _backup;
    public string CorrectionPath => _correction;

    public void WriteBackup(StickKind kind, ushort[] words) => File.WriteAllText(_backup, Format(kind, words));
    public void WriteCorrection(StickKind kind, ushort[] words) => File.WriteAllText(_correction, Format(kind, words));
    public ushort[]? ReadBackup() => Read(_backup, out _);
    public bool TryReadCorrection(out StickKind kind, out ushort[] words)
    {
        words = Read(_correction, out var parsed) ?? [];
        kind = parsed;
        return words.Length == StickCentre.WordCount;
    }

    public void ClearCorrection()
    {
        if (File.Exists(_correction)) File.Delete(_correction);
    }

    static string Format(StickKind kind, ushort[] words)
    {
        if (words.Length != StickCentre.WordCount) throw new ArgumentException("expected 12 words");
        return "v=1\nkind=" + (kind == StickKind.Edge ? "edge" : "dualsense") + "\nwords=" + string.Join(" ", words) + "\n";
    }

    static ushort[]? Read(string path, out StickKind kind)
    {
        kind = StickKind.Edge;
        if (!File.Exists(path)) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var cut = line.Split('=', 2);
            if (cut.Length == 2) map[cut[0]] = cut[1];
        }
        if (!map.TryGetValue("v", out var version) || version != "1") return null;
        if (map.TryGetValue("kind", out var name) && name == "dualsense") kind = StickKind.DualSense;
        if (!map.TryGetValue("words", out var list)) return null;
        var parts = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != StickCentre.WordCount) return null;
        var words = new ushort[StickCentre.WordCount];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!ushort.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out words[i])) return null;
        }
        return words;
    }
}

static class StickCentre
{
    public const int ReportBytes = 64;
    public const int WordCount = 12;
    public const int CentreIndex = 8;
    public const double CentreRaw = 127.5;
    public const double HalfTravel = 127.5;
    public const double RestToleranceCounts = 0.75;
    public const double CentreOffsetCounts = 1.5;
    public const double WornStdDev = 1.25;
    public const int WornPeakToPeak = 4;
    public const int MaxAdjustments = 3;
    public const int ProbeUnits = 8;
    public const int MaxStepUnits = 40;
    public const int ReadSettleMs = 100;
    public const int SampleMs = 160;
    public const int RemeasureMs = 120;
    public const int TestMs = 5000;
    public const int MinReports = 20;
    public const int VendorSony = 0x054C;
    public const int PidDualSense = 0x0CE6;
    public const int PidEdge = 0x0DF2;

    public const string BluetoothRefusal =
        "This pad is on Bluetooth, so nothing was written. The in-memory centre is a USB feature report, and Bluetooth wraps those reports differently. Bluetooth also adds radio delay. Plug the DualSense in with a data USB cable. DS4Windows can stay open.";

    public const string HidHideAdvice =
        "HidHide is hiding the controller from this app, or it refused the open. Open HidHide Configuration Client, Applications, and add JesusKpekpes.exe. Leave the Devices list as DS4Windows set it, or the pad shows up twice. This app does not change HidHide.";

    public static int MaxWord(StickKind kind) => kind == StickKind.Edge ? 4095 : 65535;

    public static string BitNote(StickKind kind) => kind == StickKind.Edge
        ? "The USB report carries 8-bit sticks (0 to 255). The Edge in-memory centre is 12-bit (0 to 4095), so the rest point can sit inside one report count. DS4Windows still receives that same 8-bit report. This app cannot widen Sony's report, and it does not add a software deadzone."
        : "The USB report carries 8-bit sticks (0 to 255). The DualSense in-memory centre is 16-bit (0 to 65535), so the rest point can sit inside one report count. DS4Windows still receives that same 8-bit report. This app cannot widen Sony's report, and it does not add a software deadzone.";

    public static StickBus BusOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return StickBus.Other;
        var text = path.ToLowerInvariant();
        if (text.Contains("bthenum", StringComparison.Ordinal) || text.Contains("bluetooth", StringComparison.Ordinal)
            || text.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal))
            return StickBus.Bluetooth;
        if (text.Contains("vid_054c", StringComparison.Ordinal) && (text.Contains("pid_0ce6", StringComparison.Ordinal) || text.Contains("pid_0df2", StringComparison.Ordinal)))
            return StickBus.Usb;
        return StickBus.Other;
    }

    public static StickKind? KindOf(string? path)
    {
        if (BusOf(path) != StickBus.Usb || path is null) return null;
        var text = path.ToLowerInvariant();
        if (text.Contains("pid_0df2", StringComparison.Ordinal)) return StickKind.Edge;
        if (text.Contains("pid_0ce6", StringComparison.Ordinal)) return StickKind.DualSense;
        return null;
    }

    public static byte[] UsbReport(byte lx, byte ly, byte rx, byte ry)
    {
        var report = new byte[ReportBytes];
        report[0] = 0x01;
        report[1] = lx;
        report[2] = ly;
        report[3] = rx;
        report[4] = ry;
        return report;
    }

    public static byte[] Feature(byte id, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[ReportBytes];
        buffer[0] = id;
        payload.CopyTo(buffer.AsSpan(1));
        return buffer;
    }

    public static byte[] ReadRequest(StickKind kind) => Feature(0x80, kind == StickKind.Edge ? [12, 4] : [12, 2]);

    public static byte[] WriteRequest(ReadOnlySpan<ushort> words)
    {
        if (words.Length != WordCount) throw new ArgumentException("finetune image must be 12 words");
        var payload = new byte[2 + WordCount * 2];
        payload[0] = 12;
        payload[1] = 1;
        for (var i = 0; i < WordCount; i++)
        {
            payload[2 + i * 2] = (byte)words[i];
            payload[3 + i * 2] = (byte)(words[i] >> 8);
        }
        var report = Feature(0x80, payload);
        var refused = ForbidReason(report);
        if (refused is not null) throw new InvalidOperationException(refused);
        return report;
    }

    /// <summary>Null when the report is one of the two in-memory finetune commands. Anything else is refused.</summary>
    public static string? ForbidReason(ReadOnlySpan<byte> report)
    {
        if (report.Length == 0) return "empty feature report";
        if (report[0] == 0x82) return "calibrate-sticks or calibrate-range";
        if (report[0] == 0x80 && report.Length > 2 && report[1] == 3 && report[2] == 2) return "NVS unlock";
        if (report[0] == 0x80 && report.Length > 2 && report[1] == 3 && report[2] == 1) return "NVS lock";
        if (report[0] == 0x80 && report.Length > 1 && report[1] == 3) return "NVS status";
        if (report[0] == 0x80 && report.Length > 1 && report[1] == 21) return "module flash";
        if (report.Length != ReportBytes || report[0] != 0x80 || report[1] != 12) return "not an in-memory finetune report";
        if (report[2] == 2 || report[2] == 4)
        {
            for (var i = 3; i < report.Length; i++)
                if (report[i] != 0) return "not an in-memory finetune report";
            return null;
        }
        if (report[2] != 1) return "not an in-memory finetune report";
        for (var i = 3 + WordCount * 2; i < report.Length; i++)
            if (report[i] != 0) return "not an in-memory finetune report";
        return null;
    }

    public static bool TryParseModule(ReadOnlySpan<byte> report, out ushort[] words)
    {
        words = new ushort[WordCount];
        if (report.Length < 4 + WordCount * 2) return false;
        if (report[0] != 0x81 || report[1] != 12 || (report[2] != 2 && report[2] != 4) || report[3] != 2) return false;
        for (var i = 0; i < WordCount; i++)
            words[i] = (ushort)(report[4 + i * 2] | (report[5 + i * 2] << 8));
        return true;
    }

    public static bool TryParseUsbSticks(ReadOnlySpan<byte> report, out byte lx, out byte ly, out byte rx, out byte ry, out string? refuse)
    {
        lx = ly = rx = ry = 0;
        refuse = null;
        if (report.Length == 0)
        {
            refuse = "The pad sent an empty report.";
            return false;
        }
        if (report[0] == 0x31 || report.Length >= 78)
        {
            refuse = BluetoothRefusal;
            return false;
        }
        if (report[0] != 0x01 || report.Length < 5)
        {
            refuse = "Unexpected input report. Only USB report 0x01 is read. Nothing was written.";
            return false;
        }
        lx = report[1];
        ly = report[2];
        rx = report[3];
        ry = report[4];
        return true;
    }

    public static string? BuildDate(ReadOnlySpan<byte> info)
    {
        if (info.Length < 12 || info[0] != 0x20) return null;
        return Encoding.ASCII.GetString(info.Slice(1, 11));
    }

    /// <summary>Matches dualshock-tools detectClone: a future firmware year, or a finetune read that does not answer.</summary>
    public static string? CloneReason(string? buildDate, bool finetuneReadable, int yearNow)
    {
        if (!finetuneReadable) return "finetune";
        if (buildDate is null) return null;
        var trimmed = buildDate.Trim();
        if (trimmed.Length >= 4 && int.TryParse(trimmed[^4..], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year > yearNow)
            return "build_date";
        return null;
    }

    public static string CloneText(string reason, string? buildDate) => reason == "build_date"
        ? "The firmware date looks like a clone (" + (buildDate ?? "").Trim() + " is in the future). Nothing was written."
        : "The pad did not answer the in-memory stick read the way a genuine DualSense does. Clones are refused, because a write can hang the USB line. Nothing was written.";

    public static RestVerdict Classify(double offsetCounts, double stdDev, int peakToPeak)
    {
        var worn = peakToPeak >= WornPeakToPeak || stdDev >= WornStdDev;
        var off = Math.Abs(offsetCounts) >= CentreOffsetCounts;
        if (worn && off) return RestVerdict.Both;
        if (worn) return RestVerdict.Worn;
        if (off) return RestVerdict.WrongCentre;
        return RestVerdict.Fine;
    }

    public static RestVerdict Combine(RestVerdict a, RestVerdict b)
    {
        var worn = a is RestVerdict.Worn or RestVerdict.Both || b is RestVerdict.Worn or RestVerdict.Both;
        var off = a is RestVerdict.WrongCentre or RestVerdict.Both || b is RestVerdict.WrongCentre or RestVerdict.Both;
        if (worn && off) return RestVerdict.Both;
        if (worn) return RestVerdict.Worn;
        if (off) return RestVerdict.WrongCentre;
        return RestVerdict.Fine;
    }

    public static string VerdictText(RestVerdict verdict) => verdict switch
    {
        RestVerdict.WrongCentre => "The rest point sits off centre and the samples barely move. That is a wrong centre, not a worn stick. An in-memory centre write can take the steady spin out.",
        RestVerdict.Worn => "The rest point jitters. That looks like a worn stick. Moving the centre will not stop the noise.",
        RestVerdict.Both => "The rest point sits off centre and the samples jitter. The steady offset can be recentred in memory. The jitter looks like wear and will remain.",
        _ => "Rest is already at the centre. The noise is small, so this does not look like a worn stick.",
    };

    public static RateStat RateOf(IReadOnlyList<long> ticks)
    {
        if (ticks.Count < 2) return new RateStat(ticks.Count, 0, 0);
        long worst = 0;
        for (var i = 1; i < ticks.Count; i++)
        {
            var gap = ticks[i] - ticks[i - 1];
            if (gap > worst) worst = gap;
        }
        var span = ticks[^1] - ticks[0];
        var hz = span <= 0 ? 0 : (ticks.Count - 1) / (span / (double)TimeSpan.TicksPerSecond);
        return new RateStat(ticks.Count, hz, worst / (double)TimeSpan.TicksPerMillisecond);
    }

    /// <summary>True when the rate fell, or the worst gap grew, by more than 10 percent. A change of exactly 10 percent is not a fail.</summary>
    public static bool RateWorse(RateStat before, RateStat after)
    {
        if (before.Count < MinReports || after.Count < MinReports || before.Hz <= 0 || after.Hz <= 0) return true;
        if (after.Hz < before.Hz * 0.9) return true;
        if (before.WorstGapMs > 0 && after.WorstGapMs > before.WorstGapMs * 1.1) return true;
        return false;
    }

    public static string RateText(RateStat rate) =>
        rate.Count < 2 ? "not enough reports to measure a rate" : rate.Hz.ToString("0.0", CultureInfo.InvariantCulture) + " Hz, worst gap " + rate.WorstGapMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms, " + rate.Count + " reports";

    public static AxisStat StatOf(IReadOnlyList<byte[]> frames, int axis)
    {
        if (frames.Count == 0) return new AxisStat(CentreRaw, 0, 0, 0);
        double sum = 0;
        var min = 255;
        var max = 0;
        foreach (var frame in frames)
        {
            var value = frame[1 + axis];
            sum += value;
            if (value < min) min = value;
            if (value > max) max = value;
        }
        var mean = sum / frames.Count;
        double square = 0;
        foreach (var frame in frames)
        {
            var delta = frame[1 + axis] - mean;
            square += delta * delta;
        }
        var std = frames.Count < 2 ? 0 : Math.Sqrt(square / (frames.Count - 1));
        return new AxisStat(mean, std, max - min, frames.Count);
    }

    public static StickPicture Picture(StickKind kind, IReadOnlyList<byte[]> frames, IReadOnlyList<long> ticks) =>
        new(kind, StatOf(frames, 0), StatOf(frames, 1), StatOf(frames, 2), StatOf(frames, 3), RateOf(ticks));

    public static string Describe(StickPicture picture)
    {
        var text = new StringBuilder();
        text.Append(picture.Kind == StickKind.Edge ? "DualSense Edge" : "DualSense");
        text.Append(" over USB. Hands-off read.");
        text.AppendLine();
        text.AppendLine(BitNote(picture.Kind));
        text.Append("Report rate ");
        text.Append(RateText(picture.Rate));
        text.Append('.');
        text.AppendLine();
        text.AppendLine("The report rate is the pad's own USB rate. Reading it does not raise that rate. The handle is closed when this text is shown.");
        WriteAxis(text, "Left X", picture.LeftX);
        WriteAxis(text, "Left Y", picture.LeftY);
        WriteAxis(text, "Right X", picture.RightX);
        WriteAxis(text, "Right Y", picture.RightY);
        text.Append("Left stick: ");
        text.Append(VerdictText(picture.Left));
        text.AppendLine();
        text.Append("Right stick: ");
        text.Append(VerdictText(picture.Right));
        return text.ToString();
    }

    static void WriteAxis(StringBuilder text, string name, AxisStat axis)
    {
        text.Append(name);
        text.Append(": rest ");
        text.Append(axis.OffsetPercent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture));
        text.Append("% of travel, mean byte ");
        text.Append(axis.MeanRaw.ToString("0.00", CultureInfo.InvariantCulture));
        text.Append(", noise ");
        text.Append(axis.NoisePercent.ToString("0.00", CultureInfo.InvariantCulture));
        text.Append("% (peak-to-peak ");
        text.Append(axis.PeakToPeak);
        text.Append(" counts).");
        text.AppendLine();
    }

    public static string RateCompareText(RateStat before, RateStat after, bool worse)
    {
        var text = "Report rate before " + RateText(before) + ". After " + RateText(after) + ". ";
        if (worse) return text + "The rate dropped or the worst gap grew by more than 10%, so the previous in-memory values were written back.";
        return text + "The handle is closed, so this app is not in the input path. The rate did not drop by more than 10% and the worst gap did not grow by more than 10%.";
    }

    public static StickOutcome Test(IStickIo io, StickKind kind)
    {
        var sample = TakeSample(io, TestMs, 8000);
        if (sample.Refuse is not null) return Fail(sample.Refuse);
        if (sample.Frames.Count < MinReports) return Fail("Only " + sample.Frames.Count + " USB reports arrived. Nothing was written. Check the cable.");
        var picture = Picture(kind, sample.Frames, sample.Ticks);
        return new StickOutcome { Ok = true, Text = Describe(picture), Picture = picture };
    }

    public static StickOutcome Apply(IStickIo io, StickStore store, StickKind kind, int yearNow)
    {
        var read = ReadImage(io, kind, yearNow, out var fail);
        if (read.Words is null) return fail!;
        var before = TakeSample(io, SampleMs, 2000);
        if (before.Refuse is not null) return Fail(before.Refuse);
        if (before.Frames.Count < MinReports) return Fail("Not enough reports to see the rest point. Nothing was written.");
        var stats = Axes(before.Frames);
        var adjust = new CentreAdjust(read.Words, stats, MaxWord(kind));
        ushort[]? backup = null;
        var writes = 0;
        try
        {
            while (adjust.Propose(stats))
            {
                if (backup is null)
                {
                    backup = (ushort[])read.Words.Clone();
                    store.WriteBackup(kind, backup);
                }
                WriteImage(io, kind, adjust.Words);
                writes++;
                var back = ReadModule(io, kind);
                if (back is null || !Same(back, adjust.Words))
                    return RestoreOrWarn(io, store, kind, backup, writes, "The pad did not read back the centre that was just written.");
                var again = TakeSample(io, RemeasureMs, 2000);
                if (again.Refuse is not null)
                    return RestoreOrWarn(io, store, kind, backup, writes, again.Refuse);
                if (again.Frames.Count < MinReports)
                    return RestoreOrWarn(io, store, kind, backup, writes, "The remeasure did not get enough reports.");
                stats = Axes(again.Frames);
                var grown = adjust.Note(stats);
                if (grown is not null)
                    return RestoreOrWarn(io, store, kind, backup, writes, grown);
            }
            if (adjust.Abort is not null)
                return backup is null ? Fail(adjust.Abort) : RestoreOrWarn(io, store, kind, backup, writes, adjust.Abort);
            if (writes == 0)
            {
                var quiet = Picture(kind, before.Frames, before.Ticks);
                return new StickOutcome
                {
                    Ok = true,
                    Picture = quiet,
                    Words = read.Words,
                    Text = Describe(quiet) + "\nNo centre write. The rest point is already inside the tolerance.",
                };
            }
            var after = TakeSample(io, RemeasureMs, 2000);
            if (after.Refuse is not null || after.Frames.Count < MinReports)
                return RestoreOrWarn(io, store, kind, backup!, writes, after.Refuse ?? "The after-read did not get enough reports.");
            var finalStats = Axes(after.Frames);
            if (!adjust.Settled(finalStats))
                return RestoreOrWarn(io, store, kind, backup!, writes, "The small centre steps did not move the rest point enough, so the backup was written back.");
            var beforeRate = RateOf(before.Ticks);
            var afterRate = RateOf(after.Ticks);
            if (RateWorse(beforeRate, afterRate))
            {
                var rolled = RestoreOrWarn(io, store, kind, backup!, writes, RateCompareText(beforeRate, afterRate, true));
                return rolled;
            }
            store.WriteCorrection(kind, adjust.Words);
            var picture = Picture(kind, after.Frames, after.Ticks);
            var done = new StringBuilder();
            done.Append("In-memory centre written. It is not saved in the controller. Backup file: ");
            done.Append(store.BackupPath);
            done.AppendLine();
            done.Append(adjust.Steps);
            done.Append(" small ");
            done.Append(adjust.Steps == 1 ? "write" : "writes");
            done.Append(". Range words were not changed.");
            done.AppendLine();
            done.AppendLine(RateCompareText(beforeRate, afterRate, false));
            done.Append(Describe(picture));
            return new StickOutcome { Ok = true, Wrote = true, Writes = writes, Words = (ushort[])adjust.Words.Clone(), Picture = picture, Text = done.ToString() };
        }
        catch (Exception ex)
        {
            if (backup is null) return Fail("Stopped before a write: " + ex.Message);
            return RestoreOrWarn(io, store, kind, backup, writes, "Stopped: " + ex.Message);
        }
    }

    public static StickOutcome Reapply(IStickIo io, StickStore store, StickKind connected, int yearNow)
    {
        if (!store.TryReadCorrection(out var kind, out var wanted))
            return new StickOutcome { Ok = true, Text = "No stored centre. Nothing was written." };
        if (kind != connected)
            return Fail("The stored centre is for a different pad. Nothing was written.");
        var read = ReadImage(io, connected, yearNow, out var fail);
        if (read.Words is null) return fail!;
        if (Same(read.Words, wanted))
            return new StickOutcome { Ok = true, Words = read.Words, Text = "Stored centre is already in the pad's memory. The handle was closed without a write." };
        var before = TakeSample(io, SampleMs, 2000);
        if (before.Refuse is not null) return Fail(before.Refuse);
        if (before.Frames.Count < MinReports) return Fail("Not enough reports before the re-apply. Nothing was written.");
        store.WriteBackup(kind, read.Words);
        try
        {
            WriteImage(io, kind, wanted);
            var back = ReadModule(io, kind);
            if (back is null || !Same(back, wanted))
                return RestoreOrWarn(io, store, connected, read.Words, 1, "The stored centre did not read back.");
            var after = TakeSample(io, RemeasureMs, 2000);
            if (after.Refuse is not null || after.Frames.Count < MinReports)
                return RestoreOrWarn(io, store, connected, read.Words, 1, after.Refuse ?? "The after-read did not get enough reports.");
            var beforeRate = RateOf(before.Ticks);
            var afterRate = RateOf(after.Ticks);
            if (RateWorse(beforeRate, afterRate))
                return RestoreOrWarn(io, store, connected, read.Words, 1, RateCompareText(beforeRate, afterRate, true) + " The stored centre was kept on disk. Plug the pad in again to try once more, or press Restore.");
            return new StickOutcome
            {
                Ok = true,
                Wrote = true,
                Writes = 1,
                Words = wanted,
                Picture = Picture(connected, after.Frames, after.Ticks),
                Text = "Stored centre written again. It is still not in the controller's flash.\n" + RateCompareText(beforeRate, afterRate, false),
            };
        }
        catch (Exception ex)
        {
            return RestoreOrWarn(io, store, connected, read.Words, 1, "Re-apply stopped: " + ex.Message);
        }
    }

    public static StickOutcome Restore(IStickIo io, StickStore store, StickKind kind, int yearNow)
    {
        var backup = store.ReadBackup();
        if (backup is null) return Fail("There is no backup file yet. Nothing was written.");
        var read = ReadImage(io, kind, yearNow, out var fail);
        if (read.Words is null) return fail!;
        if (Same(read.Words, backup))
        {
            store.ClearCorrection();
            return new StickOutcome { Ok = true, Words = backup, Text = "The pad already matches the backup. The stored centre was cleared so it will not be written again. Backup file: " + store.BackupPath };
        }
        var before = TakeSample(io, SampleMs, 2000);
        try
        {
            WriteImage(io, kind, backup);
            var back = ReadModule(io, kind);
            if (back is null || !Same(back, backup))
                return Fail("Restore did not read back. The backup file is still here: " + store.BackupPath);
            store.ClearCorrection();
            var after = before.Frames.Count >= MinReports ? TakeSample(io, RemeasureMs, 2000) : default;
            var rate = "";
            if (before.Frames.Count >= MinReports && after.Frames.Count >= MinReports)
                rate = "\n" + RateCompareText(RateOf(before.Ticks), RateOf(after.Ticks), false);
            return new StickOutcome
            {
                Ok = true,
                Wrote = true,
                Restored = true,
                Writes = 1,
                Words = backup,
                Text = "Backup restored into the pad's memory. The stored centre was cleared. The controller's flash was not touched." + rate,
            };
        }
        catch (Exception ex)
        {
            return Fail("Restore stopped: " + ex.Message + " Backup file: " + store.BackupPath);
        }
    }

    sealed class ReadResult
    {
        public ushort[]? Words;
    }

    static ReadResult ReadImage(IStickIo io, StickKind kind, int yearNow, out StickOutcome? fail)
    {
        fail = null;
        var info = io.GetFeature(0x20);
        var date = BuildDate(info);
        if (date is null)
        {
            fail = Fail("The pad did not answer the firmware report. Nothing was written.");
            return new ReadResult();
        }
        var words = ReadModule(io, kind);
        var clone = CloneReason(date, words is not null, yearNow);
        if (clone is not null)
        {
            fail = Fail(CloneText(clone, date));
            return new ReadResult();
        }
        return new ReadResult { Words = words };
    }

    static ushort[]? ReadModule(IStickIo io, StickKind kind)
    {
        io.SetFeature(ReadRequest(kind));
        io.Wait(ReadSettleMs);
        var data = io.GetFeature(0x81);
        return TryParseModule(data, out var words) ? words : null;
    }

    static void WriteImage(IStickIo io, StickKind kind, ushort[] words)
    {
        if (words.Length != WordCount) throw new InvalidOperationException("finetune image must be 12 words");
        for (var i = 0; i < WordCount; i++)
            if (words[i] > MaxWord(kind)) throw new InvalidOperationException("centre word outside the pad's range");
        io.SetFeature(WriteRequest(words));
    }

    static StickOutcome RestoreOrWarn(IStickIo io, StickStore store, StickKind kind, ushort[] backup, int writes, string reason)
    {
        try
        {
            WriteImage(io, kind, backup);
            var back = ReadModule(io, kind);
            if (back is null || !Same(back, backup))
            {
                return new StickOutcome
                {
                    Ok = false,
                    Wrote = true,
                    Writes = writes + 1,
                    Text = reason + " The backup write did not read back. The file is still here: " + store.BackupPath,
                };
            }
            return new StickOutcome
            {
                Ok = false,
                Wrote = true,
                Restored = true,
                Writes = writes + 1,
                Words = backup,
                Text = reason + " The in-memory values from before the write are back. Flash was not touched. Backup file: " + store.BackupPath,
            };
        }
        catch (Exception ex)
        {
            return new StickOutcome
            {
                Ok = false,
                Wrote = true,
                Writes = writes,
                Text = reason + " Restore failed: " + ex.Message + " Backup file: " + store.BackupPath,
            };
        }
    }

    static AxisStat[] Axes(IReadOnlyList<byte[]> frames) => [StatOf(frames, 0), StatOf(frames, 1), StatOf(frames, 2), StatOf(frames, 3)];

    static bool Same(ushort[] left, ushort[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
        return true;
    }

    static StickOutcome Fail(string text) => new() { Text = text };

    readonly struct Sample
    {
        public List<byte[]> Frames { get; init; }
        public List<long> Ticks { get; init; }
        public string? Refuse { get; init; }
    }

    static Sample TakeSample(IStickIo io, int milliseconds, int maxFrames)
    {
        var frames = new List<byte[]>(Math.Min(maxFrames, 256));
        var ticks = new List<long>(Math.Min(maxFrames, 256));
        var start = io.Ticks;
        var limit = milliseconds * (double)TimeSpan.TicksPerMillisecond;
        var empty = 0;
        while (io.Ticks - start < limit && frames.Count < maxFrames)
        {
            var report = io.ReadInput();
            if (report is null)
            {
                if (++empty > 40) break;
                continue;
            }
            empty = 0;
            if (!TryParseUsbSticks(report, out _, out _, out _, out _, out var refuse))
                return new Sample { Frames = frames, Ticks = ticks, Refuse = refuse };
            frames.Add(report);
            ticks.Add(io.Ticks);
        }
        return new Sample { Frames = frames, Ticks = ticks };
    }
}

/// <summary>At most three clamped centre steps. Range words (the first eight) are copied through and never edited.</summary>
sealed class CentreAdjust
{
    readonly int _max;
    readonly double[] _start = new double[4];
    readonly double[] _previous = new double[4];
    readonly int[] _applied = new int[4];
    readonly bool[] _seen = new bool[4];
    public ushort[] Words { get; private set; }
    public int Steps { get; private set; }
    public string? Abort { get; private set; }

    public CentreAdjust(ushort[] words, AxisStat[] axes, int max)
    {
        Words = (ushort[])words.Clone();
        _max = max;
        for (var i = 0; i < 4; i++) _start[i] = _previous[i] = axes[i].OffsetCounts;
    }

    public bool Propose(AxisStat[] axes)
    {
        if (Abort is not null || Steps >= StickCentre.MaxAdjustments) return false;
        var next = (ushort[])Words.Clone();
        var any = false;
        for (var i = 0; i < 4; i++)
        {
            var offset = axes[i].OffsetCounts;
            var delta = Delta(i, offset);
            if (Abort is not null) return false;
            if (delta > StickCentre.MaxStepUnits) delta = StickCentre.MaxStepUnits;
            if (delta < -StickCentre.MaxStepUnits) delta = -StickCentre.MaxStepUnits;
            var updated = Words[StickCentre.CentreIndex + i] + delta;
            if (updated < 0 || updated > _max)
            {
                var clamped = Math.Clamp(updated, 0, _max);
                if (clamped == Words[StickCentre.CentreIndex + i] && Math.Abs(offset) >= StickCentre.RestToleranceCounts)
                {
                    Abort = "The in-memory centre is already at the end of its range. Nothing new was written.";
                    return false;
                }
                delta = clamped - Words[StickCentre.CentreIndex + i];
                updated = clamped;
            }
            if (delta != 0) any = true;
            next[StickCentre.CentreIndex + i] = (ushort)updated;
            _applied[i] = delta;
            _previous[i] = offset;
        }
        if (!any) return false;
        for (var i = 0; i < StickCentre.CentreIndex; i++)
            if (next[i] != Words[i]) throw new InvalidOperationException("range word changed");
        Words = next;
        Steps++;
        return true;
    }

    public string? Note(AxisStat[] axes)
    {
        for (var i = 0; i < 4; i++)
        {
            var offset = axes[i].OffsetCounts;
            if (_applied[i] != 0)
            {
                if (Math.Abs(offset) > Math.Abs(_start[i]) + 0.5)
                    return "The centre made the resting offset larger, so it was treated as unexpected.";
                var moved = offset - _previous[i];
                var gain = moved / _applied[i];
                if (gain > 2.0)
                    return "The centre jumped more than expected.";
                _seen[i] = true;
            }
        }
        return null;
    }

    public bool Settled(AxisStat[] axes)
    {
        if (axes.All(axis => Math.Abs(axis.OffsetCounts) < StickCentre.RestToleranceCounts)) return true;
        for (var i = 0; i < 4; i++)
            if (Math.Abs(_start[i]) - Math.Abs(axes[i].OffsetCounts) >= 0.5) return true;
        return false;
    }

    int Delta(int index, double offset)
    {
        if (Math.Abs(offset) < StickCentre.RestToleranceCounts) return 0;
        if (!_seen[index] || _applied[index] == 0) return -Math.Sign(offset) * StickCentre.ProbeUnits;
        var moved = offset - _previous[index];
        var gain = moved / _applied[index];
        if (gain <= 0)
            return -Math.Sign(offset) * Math.Min(StickCentre.MaxStepUnits, StickCentre.ProbeUnits * (Steps + 1));
        var desired = -offset / gain;
        var magnitude = Math.Abs(desired);
        if (magnitude < 1) return 0;
        if (magnitude > StickCentre.MaxStepUnits) magnitude = StickCentre.MaxStepUnits;
        var delta = (int)Math.Round(Math.Sign(desired) * magnitude, MidpointRounding.AwayFromZero);
        var predicted = offset + gain * delta;
        if (Math.Sign(predicted) != 0 && Math.Sign(predicted) != Math.Sign(offset) && Math.Abs(predicted) > 0.25)
            delta = (int)Math.Round(-offset / gain, MidpointRounding.AwayFromZero);
        return delta;
    }
}
