using JesusKpekpes;

const int sr = 48_000;

static float[][] Empty(int length) => Enumerable.Range(0, 8).Select(_ => new float[length]).ToArray();

static float Energy(float[] data, int start, int count)
{
    double sum = 0;
    for (var i = start; i < start + count; i++) sum += data[i] * data[i];
    return (float)(sum / count);
}

static void Check(bool ok, string message)
{
    if (ok) return;
    Console.Error.WriteLine(message);
    Environment.Exit(1);
}

var click = Empty(4000);
for (var i = 2000; i < 2120; i++)
{
    var env = MathF.Sin((i - 2000) / 120f * MathF.PI);
    click[Engine.Sl][i] = MathF.Sin(2 * MathF.PI * 3200 * i / sr) * 0.25f * env;
}
var steady = Empty(sr);
for (var i = 0; i < sr; i++) steady[Engine.Sl][i] = MathF.Sin(2 * MathF.PI * 3200 * i / sr) * 0.25f;

var clickLeft = new float[4000];
var clickRight = new float[4000];
new Engine(sr).Process(click, clickLeft, clickRight);
var steadyLeft = new float[sr];
var steadyRight = new float[sr];
new Engine(sr).Process(steady, steadyLeft, steadyRight);
var clickGain = Energy(clickLeft, 2000, 300) / Energy(click[Engine.Sl], 2000, 300);
var steadyGain = Energy(steadyLeft, sr - 2000, 2000) / Energy(steady[Engine.Sl], sr - 2000, 2000);
Check(clickGain > steadyGain * 1.2f, $"step {clickGain} should beat the steady tone {steadyGain}");
Check(steadyGain < 1f, $"a steady tone should sit under the input, got {steadyGain}");

var step = Empty(1500);
for (var i = 400; i < 520; i++)
{
    var env = MathF.Sin((i - 400) / 120f * MathF.PI);
    step[Engine.Sl][i] = MathF.Sin(2 * MathF.PI * 3200 * i / sr) * 0.3f * env;
}
var withGun = step.Select(row => (float[])row.Clone()).ToArray();
for (var i = 400; i < 900; i++) withGun[Engine.L][i] = MathF.Sin(2 * MathF.PI * 90 * i / sr) * 0.95f;
var alone = new float[1500];
var beside = new float[1500];
new Engine(sr).Process(step, alone, new float[1500]);
new Engine(sr).Process(withGun, beside, new float[1500]);
var aloneEnergy = Energy(alone, 400, 300);
var besideEnergy = Energy(beside, 400, 300);
Check(besideEnergy > aloneEnergy * 0.85f, $"side step fell from {aloneEnergy} to {besideEnergy}");

var quiet = Empty(sr);
var loud = Empty(sr);
for (var i = 0; i < sr; i++)
{
    quiet[Engine.L][i] = MathF.Sin(2 * MathF.PI * 200 * i / sr) * 0.01f;
    loud[Engine.L][i] = MathF.Sin(2 * MathF.PI * 200 * i / sr) * 0.6f;
}
var quietOut = new float[sr];
var loudOut = new float[sr];
new Engine(sr).Process(quiet, quietOut, new float[sr]);
new Engine(sr).Process(loud, loudOut, new float[sr]);
var quietGain = Energy(quietOut, sr - 4000, 4000) / Energy(quiet[Engine.L], sr - 4000, 4000);
var loudGain = Energy(loudOut, sr - 4000, 4000) / Energy(loud[Engine.L], sr - 4000, 4000);
Check(quietGain > loudGain, $"quiet gain {quietGain} should be above loud gain {loudGain}");

var drop = new MixPlan(Preset.Drop, Route.Stereo, 100, 80, "Headphones");
var well = new MixPlan(Preset.Well, Route.Stereo, 100, 100, "Headphones");
var text = Mix.Config(drop);
Check(text.Contains("Channel: L R"), "stereo config must filter left and right");
Check(!text.Contains("SL SR"), "stereo config must not depend on surround speakers");
var last = text.Split("Channel: ").Last();
Check(last.StartsWith("all"), "config must reset the channel at the end");
Check(text.Contains('.') && !text.Contains(",0 dB"), "gains must use a decimal point");
Check(Mix.PreampDb(well) <= -6f, $"preamp {Mix.PreampDb(well)} must cover the step boost");
Check(Mix.Config(new MixPlan(Preset.Drop, Route.Surround, 100, 100, "Headphones")).Contains("Copy:"), "7.1 must fold into left and right");
Check(Mix.Switch(false) == "", "off must not include the filter file");
Check(Mix.Conflicts("Include: HeSuVi").Contains("HeSuVi"), "HeSuVi should be noticed");
Check(Mix.Conflicts("RaraSuppression").Contains("Rara"), "Rara should be noticed");

var tone = new float[sr];
var low = new float[sr];
for (var i = 0; i < sr; i++)
{
    tone[i] = MathF.Sin(2 * MathF.PI * 3400 * i / sr);
    low[i] = MathF.Sin(2 * MathF.PI * 100 * i / sr);
}
var toneR = (float[])tone.Clone();
var lowR = (float[])low.Clone();
Mix.Process(well, tone, toneR);
Mix.Process(well, low, lowR);
var stepBand = Energy(tone, sr - 4000, 4000);
var roomBand = Energy(low, sr - 4000, 4000);
Check(stepBand > roomBand * 2, $"step band {stepBand} should sit well above the room {roomBand}");
Check(stepBand <= 1.05f, $"step band clipped at {stepBand}");

Check(text.Contains("Device: all"), "config must hand the device scope back");
Check(text.TrimEnd().EndsWith("Channel: all"), "config must end with Channel: all");
Check(!text.Contains("(") && Mix.DeviceName("Headphones (Realtek High Definition Audio)") == "Headphones Realtek High Definition Audio", "device name should drop the brackets");
Check(Mix.Switch(true, @"C:\Program Files\EqualizerAPO\config\jesus-kpekpes") == "Include: C:\\Program Files\\EqualizerAPO\\config\\jesus-kpekpes\\on.txt\r\n", "switch should include the full path");
Check(Mix.Conflicts("VSTPlugin: Library x.dll").Contains("VST plugins"), "VST plugins should be noticed");
Check(Mix.Config(new MixPlan(Preset.Drop, Route.Surround, 100, 100, "Headphones")).Contains("0.7*C"), "7.1 must fold the center in");
Check(Mix.PreampDb(drop) < -1f, "preamp should leave real headroom");

// ---- Sound check math ----
{
    var sine = new float[sr * 2];
    for (var i = 0; i < sine.Length; i++) sine[i] = 0.1f * MathF.Sin(2 * MathF.PI * 3400 * i / sr);
    var stats = LoudnessMath.Analyze(sine, sr);
    Check(Math.Abs(stats.ActiveDb - (-23.0)) < 0.5, $"0.1 sine should read about -23 dB, got {stats.ActiveDb}");
    Check(Math.Abs(stats.PeakDb - (-20.0)) < 0.5, $"peak should read about -20 dB, got {stats.PeakDb}");
    Check(stats.StepMinusLowDb > 10, $"a 3.4 kHz tone should sit far above the bass, got {stats.StepMinusLowDb}");
    var bassTone = new float[sr * 2];
    for (var i = 0; i < bassTone.Length; i++) bassTone[i] = 0.1f * MathF.Sin(2 * MathF.PI * 60 * i / sr);
    Check(LoudnessMath.Analyze(bassTone, sr).StepMinusLowDb < -10, "a 60 Hz tone should sit far below");
    Check(LoudnessMath.Analyze(new float[sr], sr).ActiveSeconds == 0, "silence should count as no sound");
    var half = sine.Select(x => x * 0.5f).ToArray();
    Check(Math.Abs(LoudnessMath.Analyze(half, sr).ActiveDb - stats.ActiveDb + 6.0) < 0.3, "half amplitude should read 6 dB lower");
}

// ---- Call of Duty options file ----
{
    const string file =
        "1\r\n\r\n//\r\n// Audio\r\n\r\n// Adjusts the volume of the music\r\nMusicVolume@0;14317;21371 = 0.500000 // 0.000000 to 1.000000\r\n\r\n" +
        "// Number of output channels\r\nAudioWantedChannelsNumber@0;59395;43096 = 8 // 0 to 16\r\n\r\n" +
        "// Speaker device requested by the user\r\nSoundOutputDevice@0;2059;35888 = {0.0.0.00000000}.{30e2667c-40d1-4f30-8570-b900a3c8f089}\r\n\r\n" +
        "// Monitor\r\nMonitor@0;2059;35888 = MAG 255F E20\r\n\r\n" +
        "// Shadows\r\nShadowQuality@0;3981;58629 = High // one of Very_Low, Low, Medium, High, Very_High\r\n" +
        "VSync@0;55075;7764 = disabled // one of disabled, 100%, 50%, 33%, 25%\r\n" +
        "ParticleQuality@0;60198;58629 = very low\r\n";
    var plan = CodProfile.Plan(file, CodProfile.Core);
    Check(plan.NewText.Contains("MusicVolume@0;14317;21371 = 0.000000 // 0.000000 to 1.000000"), "value changes, key suffix and comment stay");
    Check(plan.NewText.Contains("AudioWantedChannelsNumber@0;59395;43096 = 2 // 0 to 16"), "channels set to 2");
    Check(plan.NewText.Contains("ShadowQuality@0;3981;58629 = Very_Low // one of"), "shadow value changes, comment stays");
    Check(plan.NewText.Contains("SoundOutputDevice@0;2059;35888 = {0.0.0.00000000}.{30e2667c-40d1-4f30-8570-b900a3c8f089}"), "device id must not change");
    Check(plan.NewText.Contains("Monitor@0;2059;35888 = MAG 255F E20"), "monitor must not change");
    Check(plan.NewText.Contains("VSync@0;55075;7764 = disabled // one of disabled, 100%"), "already-right lines stay as they were");
    Check(plan.NewText.Contains("ParticleQuality@0;60198;58629 = very low"), "line without comment stays intact");
    Check(plan.NewText.Contains("\r\n") && !plan.NewText.Replace("\r\n", "").Contains("\r"), "line endings must stay");
    Check(plan.Changes.Count == 3, $"expected 3 changes, got {plan.Changes.Count}");
    Check(plan.Unchanged >= 2, "unchanged lines should be counted");
    Check(plan.Missing.Count > 10, "settings not in this file should be reported missing");
    var again = CodProfile.Plan(plan.NewText, CodProfile.Core);
    Check(again.Changes.Count == 0 && again.NewText == plan.NewText, "applying twice must change nothing");
    Check(!CodProfile.Core.Any(s => s.Key.Contains("Device") || s.Key is "Monitor" or "Resolution" or "RefreshRate" or "GPUName" or "MaxFpsInGame"), "hardware keys must never be in the profile");
    Check(CodProfile.Chosen(true).Length == CodProfile.Core.Length + CodProfile.Lowest.Length, "lowest option adds its own keys");
}

// ---- Loudness levels ----
{
    var safeDrop = new MixPlan(Preset.Drop, Route.Stereo, 100, 100, "Headphones", Level.Safe);
    var balDrop = safeDrop with { Level = Level.Balanced };
    var loudDrop = safeDrop with { Level = Level.Loud };
    var s = Mix.PreampDb(safeDrop);
    var b = Mix.PreampDb(balDrop);
    var l = Mix.PreampDb(loudDrop);
    Check(s < -1f, $"Safe should keep real headroom, got {s}");
    Check(b >= s && l >= b, $"each level should be at least as loud as the one before: {s}, {b}, {l}");
    Check(b <= 0f && l <= 0f, "no level may ever add gain");
    Check(b - s <= 4.01f && l - s <= 8.01f, "levels are capped so they stay near the safe value");
    Check(l > s, $"Loud should be louder than Safe, got {s} and {l}");
    Check(Mix.Config(loudDrop).Contains("Preamp: "), "level config still writes a preamp");
    Check(Mix.Config(drop).Contains("Fc 6500"), "the scuff band should be in the footstep tune");
}

Console.WriteLine("engine ok");
