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

var center = Empty(2000);
for (var i = 800; i < 920; i++)
{
    var env = MathF.Sin((i - 800) / 120f * MathF.PI);
    center[Engine.C][i] = MathF.Sin(2 * MathF.PI * 3200 * i / sr) * 0.3f * env;
}
var centerLeft = new float[2000];
new Engine(sr).Process(center, centerLeft, new float[2000]);
Check(Energy(centerLeft, 800, 300) > Energy(center[Engine.C], 800, 300) * 0.2f, "a centre step must still be heard");

var endpoint = "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}";
var drop = new MixPlan(Preset.Drop, Route.Stereo, 100, 80, endpoint);
var well = new MixPlan(Preset.Well, Route.Stereo, 100, 100, endpoint);
var text = Mix.Config(drop);
Check(text.Contains("Device: {11111111-2222-3333-4444-555555555555}"), "the headphone line must be the fixed id");
Check(!text.Contains("0.0.0.00000000"), "the headphone line must not be the Windows display name");
Check(text.Contains("Channel: L R"), "stereo config must filter left and right");
Check(!text.Contains("SL SR"), "stereo config must not depend on surround speakers");
var last = text.Split("Channel: ").Last();
Check(last.StartsWith("all"), "config must reset the channel at the end");
Check(text.TrimEnd().EndsWith("Device: all"), "the file must release the headphones so the next setup is not trapped on them");
Check(Mix.Switch(true, @"C:\Program Files\EqualizerAPO\config\jesus-kpekpes\on.txt") == "Include: C:\\Program Files\\EqualizerAPO\\config\\jesus-kpekpes\\on.txt\r\n", "the switch must name the filter file in full");
Check(text.Contains('.') && !text.Contains(",0 dB"), "gains must use a decimal point");
var preamp = Mix.PreampDb(well);
Check(preamp < -1f, $"stereo preamp {preamp} must hold the hottest band to -1 dB");
var surroundPlan = new MixPlan(Preset.Drop, Route.Surround, 100, 100, endpoint);
var surround = Mix.Config(surroundPlan);
Check(surround.Contains("Copy: L=L+0.5*C+0.25*SL+0.14*RL R=R+0.5*C+0.25*SR+0.14*RR"), "7.1 must keep the centre and sit the surround low");
var copyAt = surround.IndexOf("Copy:", StringComparison.Ordinal);
var muteAt = surround.IndexOf("Channel: C LFE", StringComparison.Ordinal);
Check(copyAt >= 0 && muteAt > copyAt, "the centre is copied before it is muted");
var deviceLine = "Realtek(R) Audio Headphones {11111111-2222-3333-4444-555555555555}";
Check(Mix.DeviceMatches(Mix.DeviceSelector(endpoint), deviceLine), "the fixed id must match Equalizer APO's device string");
Check(!Mix.DeviceMatches("Headphones (Realtek(R) Audio)", deviceLine), "the Windows display name must not be treated as a match");
Check(Mix.Switch(false) == "", "off must not include the filter file");
var withPlugin = Mix.Config(well, @"C:\Games\JesusKpekpesTune.dll");
Check(withPlugin.Contains("VSTPlugin: Library \"C:\\Games\\JesusKpekpesTune.dll\""), "our plugin must be named when the file is present");
Check(!Mix.Config(well).Contains("VSTPlugin"), "the equalizer file stays plain when the plugin was not built");
var restored = Mix.StripInclude("Preamp: 0 dB\r\nInclude: jesus-kpekpes\\switch.txt\r\nInclude: rara.txt\r\n");
Check(restored.Contains("rara.txt") && !restored.Contains("jesus-kpekpes"), "turn off must remove only this tune");
Check(Mix.Conflicts("Include: HeSuVi").Contains("HeSuVi"), "HeSuVi should be noticed");
Check(Mix.Conflicts("RaraSuppression").Contains("Rara"), "Rara should be noticed");
Check(!Mix.Conflicts("Ferrara headphones").Contains("Rara"), "an unrelated word must not count as Rara");
Check(Mix.Conflicts("Convolution: hall.wav").Contains("Convolution"), "a convolution line must be noticed");
Check(Mix.Conflicts("VSTPlugin: Library \"Other.dll\"").Contains("VST plugin"), "another plugin must be noticed");
Check(!Mix.Conflicts("VSTPlugin: Library \"JesusKpekpesTune.dll\"").Contains("VST plugin"), "our own plugin is not a conflict");
Check(!Mix.Conflicts("peaceful morning").Contains("Peace"), "peaceful is not Peace");

var tone = new float[sr];
var low = new float[sr];
for (var i = 0; i < sr; i++)
{
    tone[i] = MathF.Sin(2 * MathF.PI * 3400 * i / sr);
    low[i] = MathF.Sin(2 * MathF.PI * 100 * i / sr);
}
var dry = (float[])tone.Clone();
var toneR = (float[])tone.Clone();
var lowR = (float[])low.Clone();
Mix.Process(well, tone, toneR);
Mix.Process(well, low, lowR);
var stepBand = Energy(tone, sr - 4000, 4000);
var dryBand = Energy(dry, sr - 4000, 4000);
var roomBand = Energy(low, sr - 4000, 4000);
var ceiling = MathF.Pow(10f, -1f / 20f);
var peak = 0f;
for (var i = sr / 2; i < sr; i++) peak = MathF.Max(peak, MathF.Abs(tone[i]));
Check(peak <= ceiling * 1.08f, $"a full-scale step band peaked at {peak}, above -1 dB");
Check(stepBand > roomBand * 2, $"step band {stepBand} should sit well above the room {roomBand}");
Check(stepBand < dryBand, "the equalizer must not raise a full-scale tone; quiet sounds need a compressor, and this file does not have one");

var centerOnly = new float[sr];
var silent = new float[sr];
for (var i = 0; i < sr; i++) centerOnly[i] = MathF.Sin(2 * MathF.PI * 1000 * i / sr) * 0.4f;
var foldedL = new float[sr];
var foldedR = new float[sr];
Mix.Process(surroundPlan, foldedL, foldedR, silent, silent, centerOnly);
Check(Energy(foldedL, sr - 4000, 4000) > 0.0001f, "a centre sound must still be heard after the 7.1 fold");

var crack = new float[800];
var crackR = new float[800];
var crackDry = new float[800];
for (var i = 80; i < 200; i++)
{
    var env = MathF.Sin((i - 80) / 120f * MathF.PI);
    crack[i] = crackR[i] = crackDry[i] = MathF.Sin(2 * MathF.PI * 3400 * i / sr) * 0.2f * env;
}
Mix.Process(well, crack, crackR);
Check(Energy(crack, 80, 200) > Energy(crackDry, 80, 200) * 0.5f, "a quiet crack in the step band must not be deleted");

static void Burst(float[] data, int at, float hz, float amp)
{
    var len = (int)(sr * 0.032f);
    for (var i = 0; i < len && at + i < data.Length; i++)
        data[at + i] += MathF.Sin(2 * MathF.PI * hz * (at + i) / sr) * amp * MathF.Sin(i / (float)len * MathF.PI);
}

static float Band(float[] data, float hz, int start, int count)
{
    var w0 = 2 * MathF.PI * hz / sr;
    var alpha = MathF.Sin(w0) / (2 * 1.2f);
    var cos = MathF.Cos(w0);
    var a0 = 1 + alpha;
    var b0 = alpha / a0;
    var b2 = -alpha / a0;
    var a1 = (-2 * cos) / a0;
    var a2 = (1 - alpha) / a0;
    float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
    double sum = 0;
    var end = Math.Min(data.Length, start + count);
    for (var i = 0; i < end; i++)
    {
        var y = b0 * data[i] + b2 * x2 - a1 * y1 - a2 * y2;
        x2 = x1;
        x1 = data[i];
        y2 = y1;
        y1 = y;
        if (i >= start) sum += y * y;
    }
    return (float)(sum / count);
}

var scene = sr;
var stepAtSample = (int)(sr * 0.35f);
var cleanL = new float[scene];
var cleanR = new float[scene];
Burst(cleanL, stepAtSample, 180, 0.03f);
var cleanDry = (float[])cleanL.Clone();
var cleanDryR = (float[])cleanR.Clone();
Suppress.Apply(cleanL, cleanR, sr);
var window = (int)(sr * 0.06f);
var tunedStep = Band(cleanL, 180, stepAtSample, window);
var dryStep = Band(cleanDry, 180, stepAtSample, window);
Check(tunedStep > dryStep * 1.4f, $"a quiet foot should come up, got {tunedStep / dryStep}");

var gunAt = (int)(sr * 0.55f);
var gunL = (float[])cleanDry.Clone();
var gunR = (float[])cleanDryR.Clone();
for (var i = 0; i < (int)(sr * 0.08f); i++)
{
    var env = MathF.Exp(-i / (sr * 0.04f));
    gunL[gunAt + i] += MathF.Sin(2 * MathF.PI * 90 * (gunAt + i) / sr) * 0.85f * env;
    gunR[gunAt + i] += MathF.Sin(2 * MathF.PI * 90 * (gunAt + i) / sr) * 0.85f * env;
}
var after = gunAt + (int)(sr * 0.05f);
Burst(gunL, after, 180, 0.03f);
Suppress.Apply(gunL, gunR, sr);
var kept = Band(gunL, 180, after, window) / tunedStep;
Check(kept > 0.7f, $"a step after a shot was eaten, kept {kept}");

var leftStep = new float[scene];
var rightSilent = new float[scene];
Burst(leftStep, stepAtSample, 180, 0.03f);
Suppress.Apply(leftStep, rightSilent, sr);
var leftBand = Band(leftStep, 180, stepAtSample, window);
var rightBand = Band(rightSilent, 180, stepAtSample, window);
Check(rightBand < leftBand * 0.001f, "a foot on the left was copied to the right");

var farL = new float[scene];
var farR = new float[scene];
var nearL = new float[scene];
var nearR = new float[scene];
Burst(farL, stepAtSample, 180, 0.02f);
Burst(nearL, stepAtSample, 180, 0.06f);
var farDry = Band((float[])farL.Clone(), 180, stepAtSample, window);
var nearDry = Band((float[])nearL.Clone(), 180, stepAtSample, window);
Suppress.Apply(farL, farR, sr);
Suppress.Apply(nearL, nearR, sr);
var dryDistance = nearDry / farDry;
var tunedDistance = Band(nearL, 180, stepAtSample, window) / Band(farL, 180, stepAtSample, window);
Check(tunedDistance > dryDistance * 0.7f, $"near and far steps collapsed, {tunedDistance} vs dry {dryDistance}");

var hotPeak = 0f;
for (var i = 0; i < gunL.Length; i++) hotPeak = MathF.Max(hotPeak, MathF.Max(MathF.Abs(gunL[i]), MathF.Abs(gunR[i])));
Check(hotPeak <= 1f, $"the shot clipped at {hotPeak}");

Console.WriteLine("engine ok");
