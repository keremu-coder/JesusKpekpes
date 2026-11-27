/*
 * Protocol framing adapted from dualshock-tools/dualshock-tools.github.io,
 * pinned at b7cdcfaacfadfac5e80f4f40b16e85503a488e63:
 * js/controllers/ds5-controller.js and js/controllers/ds5-edge-controller.js.
 * The upstream finetune-modal.js labels the 12 values LL, LT, RL, RT, LR, LB,
 * RR, RB, LX, LY, RX, RY; therefore indices 8-11 are the four stick-axis centres.
 *
 * Copyright (c) 2024 the_al. Used under the MIT License:
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System.Globalization;
using System.Text;

namespace JesusKpekpes;

// Live volatile finetune commands only. Deliberately has no NVS unlock/lock,
// flash, stick-calibration, range-calibration, reset, or permanent-save methods.
internal static class CentreProtocol
{
    internal const int ValueCount = 12;
    internal const int LeftXCentreIndex = 8;
    internal const int LeftYCentreIndex = 9;
    internal const int RightXCentreIndex = 10;
    internal const int RightYCentreIndex = 11;
    internal const ushort DualSenseMaxValue = ushort.MaxValue;
    internal const ushort DualSenseEdgeMaxValue = 4095;

    private const byte FeatureCommandReportId = 0x80;
    private const byte FeatureAnswerReportId = 0x81;
    private const byte DeviceInfoReportId = 0x20;
    private const int FeatureReportLength = 64;
    private const int CentreDataOffset = 4;
    private const int MaxReadAttempts = 3;
    private const int ReadPollMs = 10;

    // Same check the dualshock-tools reference makes: report 0x81, command 12, mode 2 or 4, status 2.
    private static bool HeaderIsValid(byte[] r) => r[0] == FeatureAnswerReportId && r[1] == 12 && (r[2] == 2 || r[2] == 4) && r[3] == 2;

    private static bool IsWriteAcknowledgement(byte[] response) =>
        response[0] == FeatureAnswerReportId && response[1] == 12 && response[2] == 1 && response[3] == 2;
    private const int ReadResponseDelayMs = 100;

    internal sealed record CalibrationSnapshot(string Model, byte Mode, ushort[] Values);

    internal static byte ModeFor(string model) => model switch
    {
        "DualSense" => 2,
        "DualSense Edge" => 4,
        _ => throw new NotSupportedException("Only DualSense and DualSense Edge are supported.")
    };

    internal static ushort MaxValueFor(string model) => model switch
    {
        "DualSense" => DualSenseMaxValue,
        "DualSense Edge" => DualSenseEdgeMaxValue,
        _ => throw new NotSupportedException("Only DualSense and DualSense Edge are supported.")
    };

    /// <summary>
    /// Applies the pinned upstream tool's non-clone checks: USB/model gate, valid 64-byte
    /// device-info response with no future build year, and a successful finetune read.
    /// IControllerLink.Model must be populated by the HID implementation from the allowed VID/PID.
    /// </summary>
    internal static CalibrationSnapshot ValidateDevice(IControllerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!link.IsUsb)
            throw new NotSupportedException("Connect the controller by USB. Calibration commands are refused over Bluetooth.");
        _ = ModeFor(link.Model);

        var info = link.GetFeatureReport(DeviceInfoReportId, FeatureReportLength);
        if (info is null || info.Length != FeatureReportLength || info[0] != DeviceInfoReportId)
            throw new InvalidDataException("The controller did not return a valid DualSense device-info report; clone/unknown devices are refused.");

        var buildDate = Encoding.ASCII.GetString(info, 1, 11).Trim();
        if (!DateTime.TryParseExact(buildDate, "MMM dd yyyy", CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var build))
            throw new InvalidDataException("The controller build date could not be verified; calibration is refused.");
        if (build.Year > DateTime.UtcNow.Year)
            throw new InvalidDataException("The controller reports a future firmware build date and is treated as a clone.");

        // The upstream clone gate also requires a successful live finetune read.
        // Model/VID/PID provenance is supplied by the IControllerLink implementation.
        return ReadCurrent(link);
    }

    /// <summary>Read volatile finetune data with request [12, mode], then validate the 0x81 answer.</summary>
    internal static CalibrationSnapshot ReadCurrent(IControllerLink link)
    {
        var mode = ModeFor(link.Model);
        byte[]? response = null;
        var needed = CentreDataOffset + ValueCount * sizeof(ushort);
        // The pad answers a read request within a few milliseconds, so the answer is polled every 10 ms for up to 100 ms instead of
        // always waiting the full 100 ms. A real read now costs a fraction of what it did, which keeps Apply well inside its time budget.
        // Right after a write the pad answers first with the write's own acknowledgement (81 0C 01 02 ...). That reply is not data:
        // keep polling, and if it is still the acknowledgement at the deadline, ask again (at most three times).
        for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            if (!link.SendFeatureReport([FeatureCommandReportId, 12, mode]))
                throw new IOException("The controller did not accept the live calibration read request." + Detail(link));

            var asked = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                Thread.Sleep(ReadPollMs);
                response = link.GetFeatureReport(FeatureAnswerReportId, FeatureReportLength);
                if (response is { } r && r.Length >= needed && !IsWriteAcknowledgement(r) && HeaderIsValid(r)) break;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds >= ReadResponseDelayMs) break;
            }
            if (response is null || response.Length < needed)
                throw new InvalidDataException("The controller returned no complete live calibration data.");
            if (IsWriteAcknowledgement(response)) continue;
            break;
        }
        if (!HeaderIsValid(response!))
            throw new InvalidDataException("The controller's live calibration response header is invalid (first bytes received: " + Convert.ToHexString(response!, 0, Math.Min(32, response!.Length)) + ", expected 81-0C-" + mode.ToString("X2") + "-02).");

        var values = new ushort[ValueCount];
        for (var i = 0; i < values.Length; i++)
        {
            var at = CentreDataOffset + i * sizeof(ushort);
            values[i] = (ushort)(response[at] | (response[at + 1] << 8));
        }
        return new CalibrationSnapshot(link.Model, response[2], values);
    }

    /// <summary>
    /// Write volatile finetune data with [12, 1] plus 12 little-endian values. This is not a
    /// permanent save; no NVS or flash command is issued by this project.
    /// </summary>
    internal static void WriteVolatile(IControllerLink link, CalibrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!link.IsUsb) throw new NotSupportedException("Calibration writes are USB-only.");
        if (!string.Equals(link.Model, snapshot.Model, StringComparison.Ordinal))
            throw new InvalidOperationException("The backup belongs to a different controller model.");
        if (snapshot.Values is null || snapshot.Values.Length != ValueCount)
            throw new InvalidDataException("Calibration data must contain exactly 12 values.");

        var maxValue = MaxValueFor(snapshot.Model);
        var report = new byte[3 + ValueCount * sizeof(ushort)];
        report[0] = FeatureCommandReportId;
        report[1] = 12;
        report[2] = 1;
        for (var i = 0; i < snapshot.Values.Length; i++)
        {
            if (snapshot.Values[i] > maxValue)
                throw new InvalidDataException($"Calibration value {i} exceeds the controller's {maxValue} limit.");
            var at = 3 + i * sizeof(ushort);
            report[at] = (byte)(snapshot.Values[i] & 0xff);
            report[at + 1] = (byte)(snapshot.Values[i] >> 8);
        }

        if (!link.SendFeatureReport(report))
            throw new IOException("The controller did not accept the volatile calibration write." + Detail(link));
    }

    private static string Detail(IControllerLink link) => string.IsNullOrEmpty(link.LastFailure) ? "" : " (" + link.LastFailure + ")";

    internal static bool ValuesEqual(CalibrationSnapshot left, CalibrationSnapshot right) =>
        string.Equals(left.Model, right.Model, StringComparison.Ordinal)
        && left.Mode == right.Mode
        && left.Values.AsSpan().SequenceEqual(right.Values);
}
