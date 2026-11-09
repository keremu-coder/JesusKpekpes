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

Console.WriteLine("engine ok");
