using System.Globalization;
using System.Text;

namespace JesusKpekpes;

internal sealed class FakeControllerLink : IControllerLink
{
    internal const int ValueCount = 12;
    internal const int RightXIndex = 10;
    internal readonly ushort[] Values = Enumerable.Repeat((ushort)2000, ValueCount).ToArray();
    internal readonly List<byte[]> SentReports = [];
    internal readonly List<byte> ReadReportIds = [];
    internal int InputReads { get; private set; }
    internal int FeatureReads { get; private set; }
    internal int VolatileWrites { get; private set; }
    internal int Disposals { get; private set; }
    internal string? BackupDirectoryToCheck { get; set; }
    internal bool BackupExistedBeforeFirstWrite { get; private set; }
    internal bool SlowAfterWrite { get; set; }
    internal bool CorruptFirstReadBackAfterWrite { get; set; }
    internal bool CorruptNextAnswerAfterWrite { get; private set; }
    internal bool InvalidAnswerHeader { get; set; }
    internal bool ReturnBadDeviceInfo { get; set; }
    internal bool Usb { get; set; } = true;
    internal string DeviceModel { get; set; } = "DualSense Edge";
    public string Model => DeviceModel;
    public bool IsUsb => Usb;

    // Thread.Sleep(1) lasts about 15 ms on Windows, which made the fake slower than a real pad (about 250 reports a second) and starved
    // the report-rate comparison. This waits the stated time exactly.
    private static void Pause(double milliseconds)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds) Thread.SpinWait(50);
    }

    public byte[]? ReadInputReport(int timeoutMs)
    {
        InputReads++;
        Pause(SlowAfterWrite && VolatileWrites > 0 ? 8.0 : 2.0);
        var rightX = Values[RightXIndex] > 2000 ? (byte)128 : (byte)134;
        return [0x01, 128, 128, rightX, 128];
    }

    public byte[]? GetFeatureReport(byte reportId, int length)
    {
        FeatureReads++;
        ReadReportIds.Add(reportId);
        if (reportId == 0x20)
        {
            var info = new byte[64];
            info[0] = 0x20;
            var date = Encoding.ASCII.GetBytes(DateTime.UtcNow.ToString("MMM dd yyyy", CultureInfo.GetCultureInfo("en-US")));
            Array.Copy(date, 0, info, 1, date.Length);
            if (ReturnBadDeviceInfo) info[0] = 0;
            return info;
        }
        if (reportId != 0x81) return null;
        var mode = DeviceModel == "DualSense Edge" ? (byte)4 : (byte)2;
        var answer = new byte[64];
        answer[0] = 0x81;
        answer[1] = 12;
        answer[2] = InvalidAnswerHeader ? (byte)0 : mode;
        answer[3] = 2;
        for (var i = 0; i < Values.Length; i++)
        {
            var value = Values[i];
            if (CorruptNextAnswerAfterWrite && i == RightXIndex)
            {
                value++;
                CorruptNextAnswerAfterWrite = false;
            }
            answer[4 + i * 2] = (byte)value;
            answer[5 + i * 2] = (byte)(value >> 8);
        }
        return answer;
    }

    public bool SendFeatureReport(byte[] data)
    {
        var copy = (byte[])data.Clone();
        SentReports.Add(copy);
        if (data.Length >= 3 && data[0] == 0x80 && data[1] == 12 && data[2] == 1)
        {
            if (VolatileWrites == 0 && BackupDirectoryToCheck is not null)
                BackupExistedBeforeFirstWrite = Directory.Exists(BackupDirectoryToCheck)
                    && Directory.EnumerateFiles(BackupDirectoryToCheck, "stick-centre-*.json").Any();
            VolatileWrites++;
            for (var i = 0; i < Values.Length; i++)
                Values[i] = (ushort)(data[3 + i * 2] | (data[4 + i * 2] << 8));
            CorruptNextAnswerAfterWrite = CorruptFirstReadBackAfterWrite && VolatileWrites == 1;
        }
        return true;
    }

    public void Dispose() => Disposals++;
}
