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
    // 0 keeps the simple two-state model. Otherwise the right stick reads 6.5 counts off centre and moves by LinearGain counts per unit written.
    internal double LinearGain { get; set; }
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
        var rightX = LinearGain != 0
            ? (byte)Math.Clamp(Math.Round(127.5 + 6.5 + LinearGain * (Values[RightXIndex] - 2000)), 0, 255)
            : Values[RightXIndex] > 2000 ? (byte)128 : (byte)134;
        if (_movingAfterSample) rightX = 200;
        if (_calibrationEffect == CalibrationEffect.Fixed) rightX = 128;
        else if (_calibrationEffect == CalibrationEffect.Worse) rightX = 141;
        // A full 64-byte USB report: sticks at 1 to 4, triggers 0, d-pad released (0x08), no buttons, gyro still.
        var report = new byte[64];
        report[0] = 0x01; report[1] = 128; report[2] = 128; report[3] = rightX; report[4] = 128; report[8] = 0x08;
        return report;
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
        if (reportId == 0x83)
        {
            var reply = new byte[64];
            reply[0] = (byte)(_answer83 >> 24); reply[1] = (byte)(_answer83 >> 16); reply[2] = (byte)(_answer83 >> 8); reply[3] = (byte)_answer83;
            return reply;
        }
        if (reportId == 0x81 && _reply81 is not null)
        {
            var reply = _reply81;
            _reply81 = null;
            return reply;
        }
        if (reportId != 0x81) return null;
        if (AckAfterWrite && _ackPending)
        {
            _ackPending = false;
            var ack = new byte[64];
            ack[0] = 0x81; ack[1] = 12; ack[2] = 1; ack[3] = 2; ack[4] = 0x28;
            return ack;
        }
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

    // Real DualSense Edge behaviour: the first answer after a write is the write's own acknowledgement, not calibration data.
    public bool AckAfterWrite { get; set; }
    private bool _ackPending;

    // The pad's own 0x82/0x83 stick calibration, as the dualshock-tools reference describes it.
    internal enum CalibrationEffect { None, Fixed, Worse }
    internal CalibrationEffect EffectOfCentreCalibration { get; set; } = CalibrationEffect.None;
    internal bool RefuseCentreBegin { get; set; }
    internal bool StickMovesDuringSampling { get; set; }
    internal readonly List<string> CalibrationCommands = [];
    private CalibrationEffect _calibrationEffect = CalibrationEffect.None;
    private uint _answer83;
    private bool _centreBegun;
    private int _endStage;

    private void HandleCalibration(byte[] data)
    {
        var kind = data.Length > 3 ? data[3] : (byte)0;
        CalibrationCommands.Add($"{data[1]},{data[2]},{kind}");
        var edge = DeviceModel == "DualSense Edge";
        _answer83 = 0;
        if (data[1] == 1 && kind == 1)
        {
            if (RefuseCentreBegin) return;
            _centreBegun = true; _endStage = 0; _answer83 = 0x83010101;
        }
        else if (data[1] == 3 && kind == 1 && _centreBegun)
        {
            _answer83 = 0x83010101;
            if (StickMovesDuringSampling) _movingAfterSample = true;
        }
        else if (data[1] == 2 && kind == 1 && _centreBegun)
        {
            if (!edge) { _answer83 = 0x83010102; FinishCentre(); }
            else if (_endStage == 0) { _endStage = 1; _answer83 = 0x83010101; }
            else { _answer83 = 0x83010103; FinishCentre(); }
        }
        else if (data[1] == 1 && kind == 2) { _rangeBegun = true; _endStage = 0; _answer83 = 0x83010201; }
        else if (data[1] == 2 && kind == 2 && _rangeBegun)
        {
            if (!edge) { _answer83 = 0x83010202; _rangeBegun = false; }
            else if (_endStage == 0) { _endStage = 1; _answer83 = 0x83010201; }
            else { _answer83 = 0x83010203; _rangeBegun = false; }
        }
    }

    private bool _rangeBegun;
    private bool _movingAfterSample;

    private void FinishCentre()
    {
        _centreBegun = false;
        _calibrationEffect = EffectOfCentreCalibration;
    }

    // The pad's permanent-save commands (0x80 with 21 or 3), as the dualshock-tools reference describes them.
    internal readonly List<string> SaveCommands = [];
    internal bool RefuseRightModuleUnlock { get; set; }
    internal bool RightModuleNeverLocks { get; set; }
    internal readonly bool[] ModuleOpen = new bool[2];
    internal bool StorageOpen { get; private set; }
    internal ushort[]? PersistedValues { get; private set; }
    private byte[]? _reply81;

    private bool HandleSave(byte[] data)
    {
        if (data.Length < 3 || data[0] != 0x80) return false;
        if (data[1] == 21 && data.Length >= 5)
        {
            SaveCommands.Add($"{data[1]},{data[2]},{data[3]}");
            var module = data[3] & 1;
            var ack = new byte[64]; ack[0] = 0x81; ack[1] = 21; ack[2] = data[2]; ack[3] = 2;
            if (data[2] == 6)
            {
                if (RefuseRightModuleUnlock && module == 1) ack[3] = 0; else ModuleOpen[module] = true;
            }
            else if (data[2] == 4)
            {
                if (RightModuleNeverLocks && module == 1) ack[3] = 0; else ModuleOpen[module] = false;
            }
            _reply81 = ack;
            return true;
        }
        if (data[1] == 3)
        {
            SaveCommands.Add($"{data[1]},{data[2]}");
            StorageOpen = data[2] == 2;
            _reply81 = new byte[64];
            _reply81[0] = 0x81; _reply81[1] = 3; _reply81[2] = data[2];
            return true;
        }
        return false;
    }

    public bool SendFeatureReport(byte[] data)
    {
        if (HandleSave(data)) { SentReports.Add((byte[])data.Clone()); return true; }
        if (data.Length >= 3 && data[0] == 0x82) { SentReports.Add((byte[])data.Clone()); HandleCalibration(data); return true; }
        var copy = (byte[])data.Clone();
        SentReports.Add(copy);
        if (data.Length >= 3 && data[0] == 0x80 && data[1] == 12 && data[2] == 1)
        {
            if (VolatileWrites == 0 && BackupDirectoryToCheck is not null)
                BackupExistedBeforeFirstWrite = Directory.Exists(BackupDirectoryToCheck)
                    && Directory.EnumerateFiles(BackupDirectoryToCheck, "stick-centre-*.json").Any();
            VolatileWrites++;
            if (StorageOpen && ModuleOpen[0] && ModuleOpen[1]) PersistedValues = null; // set after the values are copied below
            _ackPending = true;
            for (var i = 0; i < Values.Length; i++)
                Values[i] = (ushort)(data[3 + i * 2] | (data[4 + i * 2] << 8));
            CorruptNextAnswerAfterWrite = CorruptFirstReadBackAfterWrite && VolatileWrites == 1;
            if (StorageOpen && ModuleOpen[0] && ModuleOpen[1]) PersistedValues = (ushort[])Values.Clone();
        }
        return true;
    }

    public void Dispose() => Disposals++;
}
