namespace JesusKpekpes;

internal static class RestWatchTests
{
    private static byte[] Report(byte lx = 128, byte ly = 128, byte rx = 128, byte ry = 128, byte l2 = 0, byte b1 = 0x08, short gyro = 0)
    {
        var r = new byte[28];
        r[0] = 1; r[1] = lx; r[2] = ly; r[3] = rx; r[4] = ry; r[5] = l2; r[8] = b1;
        r[16] = (byte)gyro; r[17] = (byte)(gyro >> 8);
        return r;
    }

    private static RestWatch.Verdict Run(RestWatch watch, int seconds, Func<int, byte[]> make)
    {
        // The watch gives a verdict once and then stays quiet for its cooldown, so keep the first "stuck" verdict if there is one.
        RestWatch.Verdict last = default;
        for (var ms = 0; ms <= seconds * 1000; ms += 4)
        {
            last = watch.Feed(make(ms), ms);
            if (last.State == RestWatch.State.StuckOffCentre) return last;
        }
        return last;
    }

    internal static void Run(Action<bool, string> check)
    {
        var stuck = Run(new RestWatch(), 4, _ => Report(rx: 134));
        check(stuck.State == RestWatch.State.StuckOffCentre && stuck.Stick == RestWatch.Side.Right && Math.Abs(stuck.OffsetX - 6.5f) < 0.1f,
            "rest watch finds a right stick resting 6.5 counts off centre on a still pad");

        var used = Run(new RestWatch(), 4, ms => Report(rx: 134, b1: ms > 1000 ? (byte)0x28 : (byte)0x08));
        check(used.State == RestWatch.State.Active, "rest watch does nothing while a button is down");

        var trigger = Run(new RestWatch(), 4, _ => Report(rx: 134, l2: 40));
        check(trigger.State == RestWatch.State.Active, "rest watch does nothing while a trigger is pressed");

        var both = Run(new RestWatch(), 4, _ => Report(lx: 134, rx: 134));
        check(both.State != RestWatch.State.StuckOffCentre, "rest watch ignores both sticks being off together");

        var big = Run(new RestWatch(), 4, _ => Report(rx: 160));
        check(big.State != RestWatch.State.StuckOffCentre, "rest watch never treats a large deflection as a centre fault");

        var tiny = Run(new RestWatch(), 4, _ => Report(rx: 129));
        check(tiny.State != RestWatch.State.StuckOffCentre, "rest watch ignores an offset that is only noise");

        var moving = Run(new RestWatch(), 4, ms => Report(rx: 134, gyro: (short)((ms / 4 % 2 == 0) ? 0 : 500)));
        check(moving.State != RestWatch.State.StuckOffCentre, "rest watch waits while the pad is being moved");

        var watch = new RestWatch();
        var first = Run(watch, 4, _ => Report(rx: 134));
        var again = RestWatchSecond(watch);
        check(first.State == RestWatch.State.StuckOffCentre && again.State != RestWatch.State.StuckOffCentre,
            "rest watch stays quiet after a verdict until its cooldown ends");

        check(!RestWatchBadReport(), "rest watch ignores reports that are not USB input reports");

        // Glitch capture analysis.
        var quiet = Enumerable.Range(0, 400).Select(i => new GlitchTest.Sample(i * 4.0, 128, 128, 128, 128, 0, 0, 0)).ToList();
        check(GlitchTest.Analyse(quiet).Events.Count == 0, "glitch capture finds nothing on a quiet stick");

        var spiked = quiet.Select((s, i) => i is >= 200 and < 210 ? s with { Lx = 250 } : s).ToList();
        var (events, ignored) = GlitchTest.Analyse(spiked);
        check(events.Count == 1 && events[0].Stick == "left" && events[0].ReachedExtreme && ignored == 0,
            "glitch capture counts one false full deflection of the left stick");

        var bumped = spiked.Select((s, i) => i == 205 ? s with { Gx = 5000 } : s).ToList();
        var (bumpEvents, bumpIgnored) = GlitchTest.Analyse(bumped);
        check(bumpEvents.Count == 0 && bumpIgnored == 1, "glitch capture ignores a move that came with the pad being bumped");

        var stuckEnd = quiet.Select((s, i) => i >= 350 ? s with { Ry = 250 } : s).ToList();
        var stuckResult = GlitchTest.Analyse(stuckEnd).Events;
        check(stuckResult.Count == 1 && stuckResult[0].Stick == "right" && stuckResult[0].ReachedExtreme,
            "glitch capture reports a stick that goes to the end and stays there");

        // Guard for automatic actions.
        var still = Enumerable.Range(0, 40).Select(_ => Report()).ToList();
        check(PadQuiet.Judge(still).Quiet, "the guard allows an automatic action on a pad at rest");
        var pressed = Enumerable.Range(0, 40).Select(i => Report(b1: i == 20 ? (byte)0x28 : (byte)0x08)).ToList();
        check(!PadQuiet.Judge(pressed).Quiet, "the guard blocks an automatic action while a button is pressed");
        var held = Enumerable.Range(0, 40).Select(_ => Report(lx: 200)).ToList();
        check(!PadQuiet.Judge(held).Quiet, "the guard blocks an automatic action while a stick is held away");
        check(!PadQuiet.Judge(still.Take(5).ToList()).Quiet, "the guard refuses to guess from too few reports");
        var shaken = Enumerable.Range(0, 40).Select(i => Report(gyro: (short)(i % 2 == 0 ? 0 : 900))).ToList();
        check(!PadQuiet.Judge(shaken).Quiet, "the guard blocks an automatic action while the pad is moving");

        // Hardware-neutral rules for the Windows link.
        check(HidRules.ModelFor(0x054C, 0x0CE6) == "DualSense" && HidRules.ModelFor(0x054C, 0x0DF2) == "DualSense Edge",
            "only DualSense and DualSense Edge product numbers are accepted");
        check(HidRules.ModelFor(0x054C, 0x09CC) is null && HidRules.ModelFor(0x1234, 0x0CE6) is null,
            "other pads and other makers are refused");
        check(HidRules.LooksUsb("\\\\?\\hid#vid_054c&pid_0df2#7", 64) && !HidRules.LooksUsb("\\\\?\\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c", 78)
            && !HidRules.LooksUsb("\\\\?\\hid#vid_054c&pid_0df2#7", 0), "USB is told from Bluetooth and unknown");
        var usbOne = new HidRules.Candidate("a", "DualSense", false, 78);
        var usbTwo = new HidRules.Candidate("b", "DualSense Edge", true, 64);
        check(HidRules.Prefer([usbOne, usbTwo])?.Path == "b" && HidRules.Prefer([]) is null, "a USB pad is preferred over Bluetooth");
        check(HidRules.Explain(5, true).Contains("HidHide", StringComparison.Ordinal) && !HidRules.Explain(32, false).Contains("HidHide", StringComparison.Ordinal),
            "HidHide is named only when it is installed");
    }

    private static RestWatch.Verdict RestWatchSecond(RestWatch watch)
    {
        RestWatch.Verdict last = default;
        for (var ms = 4004; ms <= 8000; ms += 4) last = watch.Feed(Report(rx: 134), ms);
        return last;
    }

    private static bool RestWatchBadReport() =>
        new RestWatch().Feed([0x31, 1, 2, 3], 0).State != RestWatch.State.Active;
}
