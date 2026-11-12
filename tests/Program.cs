using System.Diagnostics;
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
Check(withPlugin.Contains("Preamp: 0.0 dB"), "Safe must not add level on top of the plugin");
var loudPlugin = Mix.Config(well with { Level = Level.Loud }, @"C:\Games\JesusKpekpesTune.dll");
Check(loudPlugin.Contains("Preamp: 3.0 dB"), "Loud must reach the file Equalizer APO reads");
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
var dryLow = (float[])low.Clone();
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
Check(roomBand < Energy(dryLow, sr - 4000, 4000), "the low rumble must be turned down");
Check(stepBand < dryBand, "a full-scale tone in the gun band must not be raised");

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

Check(CodProfile.CapUnderRefresh(200) == 199, "200 Hz caps at 199, not on the ceiling");
Check(CodProfile.CapUnderRefresh(144) == 143, "144 Hz caps at 143");
Check(CodProfile.CapUnderRefresh(0) == 0, "an unknown rate must not invent a cap");
Check(CodProfile.CapUnderRefresh(30) == 0, "a rate under 60 is not used");
var gameFile = "NvidiaReflex@0;33761;11445 = Enabled // one of Disabled, Enabled, Enabled + boost\n"
    + "CapFps@0;57752;20945 = false\n"
    + "MaxFpsInGame@0;13305;58861 = 0 // 30 to 300\n"
    + "ResolutionMultiplier@0;64786;30730 = 100 // 0 to 200\n";
var capped = CodProfile.Plan(gameFile, CodProfile.Chosen(false, 200));
Check(capped.NewText.Contains("= Enabled + boost //"), "reflex becomes on + boost and keeps the game's comment");
Check(capped.NewText.Contains("CapFps@0;57752;20945 = true"), "the cap switch is turned on");
Check(capped.NewText.Contains("MaxFpsInGame@0;13305;58861 = 199 //"), "200 Hz writes 199");
Check(capped.NewText.Contains("ResolutionMultiplier@0;64786;30730 = 100"), "render scale is not changed with the cap");
var open = CodProfile.Plan(gameFile, CodProfile.Chosen(false, 0));
Check(open.NewText.Contains("MaxFpsInGame@0;13305;58861 = 0"), "no monitor rate leaves the cap number");
Check(open.NewText.Contains("= Enabled + boost"), "boost is still set when the rate is unknown");

// ---- Linked foot lift: the gap between the ears is kept and nothing is turned down ----
{
    const int rate = 48000;
    double Db(float[] x, int from, int to) { double s = 0; for (var i = from; i < to; i++) s += (double)x[i] * x[i]; return 10 * Math.Log10(s / (to - from) + 1e-20); }
    foreach (var levelDb in new[] { -35.0, -30.0, -20.0, -12.0, -6.0 })
    {
        var aL = (float)(Math.Pow(10, levelDb / 20) * Math.Sqrt(2));
        var aR = (float)(aL * Math.Pow(10, -15.0 / 20));
        var left = new float[rate]; var right = new float[rate];
        var inL = new float[rate]; var inR = new float[rate];
        for (var i = 0; i < rate; i++)
        {
            var wave = MathF.Sin(2 * MathF.PI * 180 * i / rate);
            inL[i] = left[i] = wave * aL;
            inR[i] = right[i] = wave * aR;
        }
        TuneEngine.CompressLinked([left, right], rate);
        var gapIn = Db(inL, rate / 2, rate) - Db(inR, rate / 2, rate);
        var gapOut = Db(left, rate / 2, rate) - Db(right, rate / 2, rate);
        Check(Math.Abs(gapOut - gapIn) < 0.1, $"{levelDb} dBFS: the gap between the ears must stay {gapIn:0.00}, got {gapOut:0.00}");
        Check(Db(left, rate / 2, rate) >= Db(inL, rate / 2, rate) - 0.05, $"{levelDb} dBFS: the louder ear must not be turned down");
    Check(Db(right, rate / 2, rate) >= Db(inR, rate / 2, rate) - 0.05, $"{levelDb} dBFS: the quieter ear must not be turned down");
        if (levelDb <= -30)
            Check(Db(left, rate / 2, rate) - Db(inL, rate / 2, rate) > 9.0, $"{levelDb} dBFS: a quiet foot should be lifted by about 10 dB");
    }
    var hush = new float[rate];
    TuneEngine.CompressLinked([hush], rate);
    Check(hush.All(v => v == 0f), "silence must stay silence");
}

// ---- Nova Pro: extra profile. Drop and Well stay. A hit is lifted. A steady tone is not. ----
{
    const int rate = 48000;
    double Db(float[] x, int from, int to)
    {
        double s = 0;
        for (var i = from; i < to; i++) s += (double)x[i] * x[i];
        return 10 * Math.Log10(s / (to - from) + 1e-20);
    }
    var nova = new MixPlan(Preset.Nova, Route.Stereo, 100, 40, endpoint);
    var novaText = Mix.Config(nova);
    Check(novaText.Contains("Fc 3200 Hz Gain 6.0 dB"), "Nova puts the step tick at 3.2 kHz");
    Check(novaText.Contains("Fc 8000 Hz Gain -4.5 dB"), "Nova pulls the 8 kHz peak down");
    Check(!novaText.Contains("Fc 7200 Hz"), "Nova does not use the Drop peak");
    Check(!novaText.Contains("SL SR"), "stereo Nova must not depend on surround speakers");
    Check(Mix.Config(drop).Contains("Fc 7200 Hz") && !Mix.Config(drop).Contains("Fc 8000 Hz"), "Drop keeps its own curve");
    Check(Mix.PreampDb(nova) < -1f, "Nova preamp must hold the hottest band down");
    var novaPlugin = Mix.Config(nova, @"C:\Games\JesusKpekpesTune.dll");
    Check(novaPlugin.Contains("Nova 1 Hit 1.00"), "the plugin must be told this is Nova at full hit");
    Check(!Mix.Config(well, @"C:\Games\JesusKpekpesTune.dll").Contains("Nova "), "Well must not switch the plugin into Nova");
    var novaOff = Mix.Config(new MixPlan(Preset.Nova, Route.Stereo, 0, 0, endpoint));
    Check(novaOff.Contains("Fc 8000 Hz Gain -4.5 dB"), "the 8 kHz cut stays when the hit slider is down");
    Check(novaOff.Contains("Fc 3200 Hz Gain 0.0 dB"), "the tick boost follows the hit slider");

    var tick = new float[rate];
    var tickR = new float[rate];
    for (var i = 0; i < rate; i++) tick[i] = MathF.Sin(2 * MathF.PI * 3200 * i / rate) * 0.03f;
    var tickDry = (float[])tick.Clone();
    TuneEngine.ShapeNova([tick, tickR], rate, 100);
    var tickGain = Energy(tick, rate - rate / 5, rate / 5) / Energy(tickDry, rate - rate / 5, rate / 5);
    Check(tickGain > 0.7f && tickGain < 1.4f, $"a steady tick must not be lifted, gain {tickGain}");

    var burstAt = rate / 3;
    var burstLen = (int)(rate * 0.032f);
    var burst = new float[rate];
    var burstR = new float[rate];
    for (var i = 0; i < burstLen; i++)
    {
        var env = MathF.Sin(i / (float)burstLen * MathF.PI);
        burst[burstAt + i] = MathF.Sin(2 * MathF.PI * 3200 * (burstAt + i) / rate) * 0.03f * env;
    }
    var burstDry = (float[])burst.Clone();
    TuneEngine.ShapeNova([burst, burstR], rate, 100);
    var burstGain = Energy(burst, burstAt, burstLen + 400) / Energy(burstDry, burstAt, burstLen + 400);
    Check(burstGain > tickGain * 2f, $"a step hit {burstGain} should beat the steady tone {tickGain}");

    var quietHit = new float[200];
    TuneEngine.ShapeNova([quietHit, new float[200]], rate, 100);
    Check(quietHit.All(v => v == 0f), "Nova silence must stay silence");

    var gapL = new float[rate];
    var gapR = new float[rate];
    var gapInL = new float[rate];
    var gapInR = new float[rate];
    var loudEar = 0.04f;
    var quietEar = loudEar * MathF.Pow(10f, -15f / 20f);
    for (var i = 0; i < rate; i++)
    {
        var wave = MathF.Sin(2 * MathF.PI * 3200 * i / rate);
        gapInL[i] = gapL[i] = wave * loudEar;
        gapInR[i] = gapR[i] = wave * quietEar;
    }
    Mix.Process(nova, gapL, gapR);
    var gapIn = Db(gapInL, rate / 2, rate) - Db(gapInR, rate / 2, rate);
    var gapOut = Db(gapL, rate / 2, rate) - Db(gapR, rate / 2, rate);
    Check(Math.Abs(gapOut - gapIn) < 1.0, $"Nova must keep the {gapIn:0.00} dB gap between the ears, got {gapOut:0.00}");

    var air = new float[rate];
    var airR = new float[rate];
    for (var i = 0; i < rate; i++) air[i] = MathF.Sin(2 * MathF.PI * 8000 * i / rate) * 0.2f;
    var airDry = (float[])air.Clone();
    Mix.Process(nova, air, airR);
    var airGain = Energy(air, rate - 4000, 4000) / Energy(airDry, rate - 4000, 4000);
    Check(airGain < 0.75f, $"the 8 kHz peak must come down, gain {airGain}");

    var rumble = new float[rate];
    var rumbleR = new float[rate];
    for (var i = 0; i < rate; i++) rumble[i] = MathF.Sin(2 * MathF.PI * 70 * i / rate) * 0.2f;
    var rumbleDry = (float[])rumble.Clone();
    Mix.Process(nova, rumble, rumbleR);
    Check(Energy(rumble, rate - 4000, 4000) < Energy(rumbleDry, rate - 4000, 4000), "low rumble must be turned down");

    var fold = new MixPlan(Preset.Nova, Route.Surround, 100, 40, endpoint);
    var foldText = Mix.Config(fold);
    Check(foldText.Contains("Copy: L=L+0.707*C+0.500*SL+0.320*RL R=R+0.707*C+0.500*SR+0.320*RR"), "Nova 7.1 fold keeps left on the left");
    Check(foldText.Contains("Preamp: -24.0 dB"), "Nova leaves the low speaker out");
    Check(!foldText.Contains("untested"), "Nova 7.1 is not the untested fold");
    var foldPlugin = Mix.Config(fold, @"C:\Games\JesusKpekpesTune.dll");
    Check(foldPlugin.Contains("Channel: L R C SL SR RL RR\r\nVSTPlugin:"), "Nova 7.1 must not let the low speaker lift a step");
    Check(Mix.Config(surroundPlan).Contains("Copy: L=L+0.5*C+0.25*SL+0.14*RL R=R+0.5*C+0.25*SR+0.14*RR"), "Drop 7.1 fold is unchanged");

    float[][] Ears() => [new float[rate], new float[rate], new float[rate], new float[rate], new float[rate], new float[rate]];
    void Hit(float[] ear)
    {
        for (var i = 0; i < burstLen; i++)
        {
            var env = MathF.Sin(i / (float)burstLen * MathF.PI);
            ear[burstAt + i] = MathF.Sin(2 * MathF.PI * 3200 * (burstAt + i) / rate) * 0.05f * env;
        }
    }
    var sideEars = Ears();
    Hit(sideEars[2]);
    Mix.Process(fold, sideEars[0], sideEars[1], sideEars[2], sideEars[3], sideEars[4], sideEars[5]);
    var sideLeftEnergy = Energy(sideEars[0], burstAt, burstLen + 800);
    var sideRightEnergy = Energy(sideEars[1], burstAt, burstLen + 800);
    Check(sideRightEnergy < sideLeftEnergy * 0.05f, $"a left side step leaked to the right, {sideRightEnergy} vs {sideLeftEnergy}");

    var rearEars = Ears();
    Hit(rearEars[5]);
    Mix.Process(fold, rearEars[0], rearEars[1], rearEars[2], rearEars[3], rearEars[4], rearEars[5]);
    var rearLeftEnergy = Energy(rearEars[0], burstAt, burstLen + 800);
    Check(sideLeftEnergy > rearLeftEnergy * 1.5f, $"a side step should beat a rear step, {sideLeftEnergy} vs {rearLeftEnergy}");
    Check(rearLeftEnergy > sideLeftEnergy * 0.05f, "a rear step must still be heard");
    Check(Energy(rearEars[1], burstAt, burstLen + 800) < rearLeftEnergy * 0.05f, "a left rear step leaked to the right");

    var centreEars = Ears();
    Hit(centreEars[4]);
    Mix.Process(fold, centreEars[0], centreEars[1], centreEars[2], centreEars[3], centreEars[4], centreEars[5]);
    var centreL = Energy(centreEars[0], burstAt, burstLen + 800);
    var centreR = Energy(centreEars[1], burstAt, burstLen + 800);
    var centreRatio = centreL / MathF.Max(1e-12f, centreR);
    Check(centreRatio > 0.9f && centreRatio < 1.1f, $"a centre step must stay in the middle, ratio {centreRatio}");
    Check(centreL > 1e-6f, "a centre step must be heard");
}

Check(Installer.SameHash("AB" + new string('0', 62), "ab" + new string('0', 62)), "hash compare must ignore case");
Check(!Installer.SameHash("AB" + new string('0', 62), "ac" + new string('0', 62)), "a different hash must not match");
Check(!Installer.SameHash("", ""), "an empty pin must never match");
Check(Installer.ApoFileFromRelease("{\"platform_releases\":{\"windows\":{\"filename\":\"/Equalizer APO/1.4/setup.exe\"}}}") == "/Equalizer APO/1.4/setup.exe", "release file name");
Check(Installer.ApoFileFromRelease("{\"platform_releases\":{}}") is null, "missing windows entry must give null");
Check(Installer.ApoFileFromRelease("not json") is null, "bad json must give null");
Check(Installer.CanInstall("ReaPlugs") && !Installer.CanInstall("HeSuVi"), "only APO and ReaPlugs download");

// ---- PC report: plain words, real line breaks ----
{
    var raw = "== PC optimizer - state ==\n  [FAIL] Fast startup off  - found 1\n  [FAIL] TcpNoDelay (global)  - not set\n  [WARN] Monitor refresh  - running 60 Hz\n  [PASS] Secure Boot  - on\n  SCORE: 15%  (17 of 113 checks passed, 2 warnings, 6 not applicable)";
    var items = PcReport.Items(raw);
    Check(items.Count == 4, "four result lines expected, got " + items.Count);
    Check(items[0].Name == "Fast startup off" && items[0].Detail == "found 1", "name and detail must split on the dash");
    Check(PcReport.Group("Nagle off: TCPNoDelay (Wi-Fi 2)") == "Internet delay", "network settings group");
    Check(PcReport.Group("Microsoft.XboxGamingOverlay") == "Xbox apps that run in the background", "xbox before game");
    var plain = PcReport.Verify(raw);
    Check(plain.Contains("15% ready"), "the score must be said plainly");
    Check(plain.Contains("Nothing was changed"), "the check must say it only looked");
    Check(plain.Contains("Monitor refresh"), "a warning must be listed for the person to do");
    Check(!plain.Replace("\r\n", "").Contains('\n'), "line breaks must be CRLF for the text box");
    Check(PcReport.Optimized("Done. Restart Windows once.", 0).StartsWith("Finished."), "a clean run says finished");
    Check(PcReport.Optimized("boom", 1).StartsWith("It stopped"), "a failed run says it stopped");
}
Check(NetCheck.Verdict(540, 510, false).Contains("too high"), "a 540 ms line must be called too high");
Check(NetCheck.Verdict(20, 25, true).Contains("very good"), "a 20 ms line is very good");
Check(NetCheck.Verdict(-1, -1, false).Contains("No answer"), "no ping at all must be said");

// Damaged Call of Duty options files. The parser may only change a real "Key@index = value" line.
{
    CodPlan Feed(string text) => CodProfile.Plan(text, CodProfile.Chosen(false, 200));
    var junk = new[]
    {
        "",
        "\n\n",
        "// only a comment\n",
        "this is not an options file",
        "NvidiaReflex = Enabled\n",
        "NvidiaReflex@ = Enabled\n",
        "@0;1 = Enabled\n",
        "NvidiaReflex@0 = Enabled\n",
        " =  = \n",
        new string('A', 20000) + "\n",
        "NvidiaReflex@0;1;2 = Enabled\0boost\n",
        "// NvidiaReflex@0;1;2 = false\nCapFps@0;1;2 = false\n",
    };
    foreach (var sample in junk)
    {
        try
        {
            var plan = Feed(sample);
            var body = plan.NewText;
            Check(body != null, "a damaged options file must still return text");
            Check(body != null && !body.Contains("MaxFpsInGame@0"), "a missing cap line must not be invented");
        }
        catch (Exception ex)
        {
            Check(false, "damaged options file threw " + ex.GetType().Name);
        }
    }
    var mixed = "// keep me\nNvidiaReflex@0;33761;11445 = Enabled // note\nNOT A LINE\nCapFps@0;1;2 = false\n";
    var mixedPlan = Feed(mixed);
    Check(mixedPlan.NewText.StartsWith("// keep me\n"), "a comment above a real line stays");
    Check(mixedPlan.NewText.Contains("NOT A LINE\n"), "a damaged line between real lines stays");
    Check(mixedPlan.NewText.Contains("= Enabled + boost // note"), "a real reflex line still updates beside damage");
    Check(mixedPlan.NewText.Contains("CapFps@0;1;2 = true"), "a real cap line still updates beside damage");
    var twice = "NvidiaReflex@0;1;2 = Enabled\nNvidiaReflex@0;9;9 = Enabled\n";
    var twicePlan = Feed(twice);
    Check(twicePlan.NewText.StartsWith("NvidiaReflex@0;1;2 = Enabled + boost\n"), "the first copy of a key is the one that changes");
    Check(twicePlan.NewText.EndsWith("NvidiaReflex@0;9;9 = Enabled\n"), "a second copy of the same key is left alone");
    var random = new Random(7);
    for (var n = 0; n < 200; n++)
    {
        var len = random.Next(0, 300);
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = (char)random.Next(32, 64);
        var planted = n % 5 == 0;
        var noise = new string(chars);
        if (planted) noise += "\nNvidiaReflex@0;4;4 = Enabled // x\n";
        try
        {
            var plan = Feed(noise);
            if (planted)
                Check(plan.NewText.Contains("= Enabled + boost // x"), "a real line inside random text still updates");
            else
                Check(plan.Changes.Count == 0, "random text with no real line must not be rewritten");
        }
        catch (Exception ex)
        {
            Check(false, "random options text threw " + ex.GetType().Name);
            break;
        }
    }
}

// The C plugin and the C# preview are given the same samples. They have to come out the same.
{
    var root = FindTuneRoot();
    var pipe = Environment.GetEnvironmentVariable("TUNE_PIPE");
    string? bin = null;
    if (!string.IsNullOrWhiteSpace(pipe))
    {
        Check(File.Exists(pipe), "TUNE_PIPE was set but the C program is not there");
        bin = File.Exists(pipe) ? pipe : null;
    }
    else if (root != null)
    {
        bin = CompileTunePipe(root);
    }
    Check(bin != null, "the C sound code was not compiled, so it was not compared with C#");
    if (bin != null)
    {
        const int rate = 48000;
        float[] Tone(int n, float hz, float amp)
        {
            var a = new float[n];
            for (var i = 0; i < n; i++) a[i] = MathF.Sin(2f * MathF.PI * hz * i / rate) * amp;
            return a;
        }
        float[] Step(int n, float hz, float amp)
        {
            var a = new float[n];
            var at = n / 5;
            var len = (int)(rate * 0.032f);
            for (var i = 0; i < len && at + i < n; i++)
            {
                var env = MathF.Sin(i / (float)len * MathF.PI);
                a[at + i] = MathF.Sin(2f * MathF.PI * hz * (at + i) / rate) * amp * env;
            }
            return a;
        }
        float[] Noise(int n, float amp, uint seed)
        {
            var a = new float[n];
            var s = seed;
            for (var i = 0; i < n; i++)
            {
                s = s * 1664525u + 1013904223u;
                a[i] = (((s >> 8) & 0xFFFFFF) / 16777216f * 2f - 1f) * amp;
            }
            return a;
        }
        var worst = 0f;
        var worstName = "";
        void Same(string name, int mode, float hit, params float[][] ears)
        {
            var diff = TuneGap(bin, mode, hit, rate, ears);
            if (worstName.Length == 0 || diff > worst) { worst = diff; worstName = name; }
            Check(diff >= 0f && diff < 0.0001f, $"{name}: C and C# differ by {diff}");
        }
        var quietRight = Tone(8192, 180f, 0.02f * MathF.Pow(10f, -15f / 20f));
        Same("classic quiet foot", 0, 1f, Tone(8192, 180f, 0.02f), quietRight);
        Same("classic loud gun", 0, 1f, Tone(4096, 1400f, 0.95f), Tone(4096, 1000f, 0.95f));
        Same("classic silence", 0, 1f, new float[256], new float[256]);
        Same("classic one ear", 0, 1f, Tone(2048, 180f, 0.03f));
        Same("classic noise", 0, 1f, Noise(4096, 0.08f, 3), Noise(4096, 0.08f, 9));
        Same("nova step", 1, 1f, Step(16384, 3200f, 0.03f), Step(16384, 220f, 0.01f));
        Same("nova steady", 1, 1f, Tone(24000, 3200f, 0.03f), Tone(24000, 220f, 0.02f));
        Same("nova hit down", 1, 0f, Step(8192, 3200f, 0.03f), new float[8192]);
        Same("nova noise", 1, 0.6f, Noise(8192, 0.05f, 11), Noise(8192, 0.05f, 13));
        var dryFoot = Tone(8192, 180f, 0.02f);
        var wetFoot = (float[])dryFoot.Clone();
        TuneEngine.CompressLinked([wetFoot, (float[])quietRight.Clone()], rate);
        Check(Energy(wetFoot, 6000, 2000) > Energy(dryFoot, 6000, 2000) * 1.5f, "the compared foot sample was not actually lifted");
        if (worst < 0.0001f) Console.WriteLine($"C and C# match. Largest sample gap {worst:0.00000000} on {worstName}.");
    }
}

static string? FindTuneRoot()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "plugin", "tune.c"))) return dir.FullName;
            dir = dir.Parent;
        }
    }
    return null;
}

static string? CompileTunePipe(string root)
{
    string? gcc = null;
    foreach (var name in new[] { "gcc", "cc" })
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(name, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (probe == null) continue;
            probe.WaitForExit(8000);
            if (probe.ExitCode == 0) { gcc = name; break; }
        }
        catch { }
    }
    if (gcc == null) return null;
    var bin = Path.Combine(Path.GetTempPath(), "kpekpes-tunepipe");
    using var built = new Process();
    built.StartInfo.FileName = gcc;
    built.StartInfo.UseShellExecute = false;
    built.StartInfo.RedirectStandardError = true;
    built.StartInfo.ArgumentList.Add("-DTUNE_CHECK");
    built.StartInfo.ArgumentList.Add("-O2");
    built.StartInfo.ArgumentList.Add("-o");
    built.StartInfo.ArgumentList.Add(bin);
    built.StartInfo.ArgumentList.Add(Path.Combine(root, "plugin", "tune.c"));
    built.StartInfo.ArgumentList.Add("-lm");
    built.Start();
    var err = built.StandardError.ReadToEnd();
    built.WaitForExit();
    if (built.ExitCode != 0)
    {
        Check(false, "the C sound code did not compile: " + err.Trim());
        return null;
    }
    return bin;
}

static float TuneGap(string bin, int mode, float hit, int rate, float[][] ears)
{
    var frames = ears.Min(e => e.Length);
    var ms = new MemoryStream();
    using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
    {
        w.Write(System.Text.Encoding.ASCII.GetBytes("TNP1"));
        w.Write(ears.Length);
        w.Write(frames);
        w.Write(mode);
        w.Write(rate);
        w.Write(hit);
        foreach (var ear in ears)
            for (var i = 0; i < frames; i++) w.Write(ear[i]);
    }
    using var proc = new Process();
    proc.StartInfo.FileName = bin;
    proc.StartInfo.UseShellExecute = false;
    proc.StartInfo.RedirectStandardInput = true;
    proc.StartInfo.RedirectStandardOutput = true;
    proc.StartInfo.RedirectStandardError = true;
    proc.StartInfo.ArgumentList.Add("pipe");
    proc.Start();
    proc.StandardInput.BaseStream.Write(ms.ToArray());
    proc.StandardInput.Close();
    var need = ears.Length * frames * sizeof(float);
    var raw = new byte[need];
    var got = 0;
    while (got < need)
    {
        var n = proc.StandardOutput.BaseStream.Read(raw, got, need - got);
        if (n == 0) break;
        got += n;
    }
    var err = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0 || got != need)
    {
        Check(false, "the C sound code did not return samples" + (err.Length == 0 ? "" : ": " + err.Trim()));
        return -1f;
    }
    var copy = ears.Select(e =>
    {
        var a = new float[frames];
        Array.Copy(e, a, frames);
        return a;
    }).ToArray();
    if (mode == 0) TuneEngine.CompressLinked(copy, rate);
    else TuneEngine.ShapeNova(copy, rate, (int)MathF.Round(hit * 100f));
    var worst = 0f;
    for (var c = 0; c < ears.Length; c++)
    {
        for (var i = 0; i < frames; i++)
        {
            var sample = BitConverter.ToSingle(raw, (c * frames + i) * sizeof(float));
            var gap = MathF.Abs(sample - copy[c][i]);
            if (float.IsNaN(gap)) return 1f;
            if (gap > worst) worst = gap;
        }
    }
    return worst;
}

Console.WriteLine("engine ok");
