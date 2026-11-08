using JesusKpekpes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Jesus Kpekpes");
    Console.WriteLine("  test     Play a side step and a front shot through the engine.");
    Console.WriteLine("  devices  List playback devices and their channel count.");
    Console.WriteLine("  listen   Read the default device. Needs a second device for the headphones.");
    Console.WriteLine("           listen --out \"Headphone name\"");
    return;
}

switch (args[0])
{
    case "devices":
        ListDevices();
        break;
    case "test":
        PlayTest();
        break;
    case "listen":
        Listen(Arg(args, "--out"));
        break;
    default:
        Console.WriteLine("Unknown command. Run with no arguments for help.");
        break;
}

static string? Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void ListDevices()
{
    using var enumerator = new MMDeviceEnumerator();
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        var format = device.AudioClient.MixFormat;
        Console.WriteLine($"{device.FriendlyName}  {format.SampleRate} Hz  {format.Channels} speakers");
    }
}

static void PlayTest()
{
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var format = device.AudioClient.MixFormat;
    var period = SmallestPeriodMs(device);
    Console.WriteLine($"Headphones: {device.FriendlyName}");
    Console.WriteLine($"Smallest buffer Windows gave this device: {period:0.0} ms at {format.SampleRate} Hz.");

    const int sr = 48000;
    const int n = sr * 2;
    var input = Enumerable.Range(0, 8).Select(_ => new float[n]).ToArray();
    for (var i = sr / 2; i < sr / 2 + 120; i++)
    {
        var env = MathF.Sin((i - sr / 2) / 120f * MathF.PI);
        input[Engine.Sl][i] = MathF.Sin(2 * MathF.PI * 3200 * i / sr) * 0.25f * env;
    }
    for (var i = sr; i < sr + (int)(sr * 0.15); i++)
    {
        input[Engine.L][i] = MathF.Sin(2 * MathF.PI * 90 * i / sr) * 0.8f;
    }

    var left = new float[n];
    var right = new float[n];
    new Engine(sr).Process(input, left, right);

    var wave = WaveFormat.CreateIeeeFloatWaveFormat(sr, 2);
    var buffer = new BufferedWaveProvider(wave) { DiscardOnBufferOverflow = true };
    var interleaved = new byte[n * 8];
    for (var i = 0; i < n; i++)
    {
        WriteFloat(interleaved, i * 8, left[i] * 0.4f);
        WriteFloat(interleaved, i * 8 + 4, right[i] * 0.4f);
    }
    buffer.AddSamples(interleaved, 0, interleaved.Length);

    using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, (int)Math.Max(1, period));
    output.Init(buffer);
    output.Play();
    Console.WriteLine("Playing. A step on the left, then a short front shot.");
    Thread.Sleep(2200);
    output.Stop();
}

static void Listen(string? outName)
{
    using var enumerator = new MMDeviceEnumerator();
    using var source = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var mix = source.AudioClient.MixFormat;
    Console.WriteLine($"Reading: {source.FriendlyName}  {mix.SampleRate} Hz  {mix.Channels} speakers");
    Console.WriteLine($"Smallest buffer: {SmallestPeriodMs(source):0.0} ms");
    if (mix.Channels < 8)
    {
        Console.WriteLine("This device is not 7.1. Windows has already folded the side speakers into stereo.");
        Console.WriteLine("The engine cannot hear a side step on this device. Set it to 7.1, or use a device that has 8 speakers.");
    }

    MMDevice? headphones = null;
    if (outName is not null)
    {
        headphones = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(outName, StringComparison.OrdinalIgnoreCase));
    }
    if (headphones is null || headphones.ID == source.ID)
    {
        Console.WriteLine("Name a different headphone device with --out. Playing back into the same device howls.");
        headphones?.Dispose();
        return;
    }

    using var capture = new WasapiLoopbackCapture(source);
    var engine = new Engine(capture.WaveFormat.SampleRate);
    var wave = WaveFormat.CreateIeeeFloatWaveFormat(capture.WaveFormat.SampleRate, 2);
    var buffer = new BufferedWaveProvider(wave) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(80) };
    using var output = new WasapiOut(headphones, AudioClientShareMode.Shared, true, (int)Math.Max(1, SmallestPeriodMs(headphones)));
    output.Init(buffer);
    output.Play();

    capture.DataAvailable += (_, e) =>
    {
        var channels = capture.WaveFormat.Channels;
        var bytesPerSample = capture.WaveFormat.BitsPerSample / 8;
        var frames = e.BytesRecorded / (bytesPerSample * channels);
        if (frames <= 0) return;
        var input = Enumerable.Range(0, 8).Select(_ => new float[frames]).ToArray();
        for (var i = 0; i < frames; i++)
        {
            Span<float> windows = stackalloc float[8];
            for (var c = 0; c < Math.Min(8, channels); c++)
            {
                windows[c] = capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                    ? BitConverter.ToSingle(e.Buffer, (i * channels + c) * bytesPerSample)
                    : BitConverter.ToInt16(e.Buffer, (i * channels + c) * bytesPerSample) / 32768f;
            }
            input[Engine.L][i] = windows[0];
            input[Engine.R][i] = channels > 1 ? windows[1] : windows[0];
            input[Engine.C][i] = channels > 2 ? windows[2] : 0;
            input[Engine.Lfe][i] = channels > 3 ? windows[3] : 0;
            input[Engine.Rl][i] = channels > 4 ? windows[4] : 0;
            input[Engine.Rr][i] = channels > 5 ? windows[5] : 0;
            input[Engine.Sl][i] = channels > 6 ? windows[6] : 0;
            input[Engine.Sr][i] = channels > 7 ? windows[7] : 0;
        }
        var left = new float[frames];
        var right = new float[frames];
        engine.Process(input, left, right);
        var interleaved = new byte[frames * 8];
        for (var i = 0; i < frames; i++)
        {
            WriteFloat(interleaved, i * 8, left[i]);
            WriteFloat(interleaved, i * 8 + 4, right[i]);
        }
        buffer.AddSamples(interleaved, 0, interleaved.Length);
    };

    capture.StartRecording();
    Console.WriteLine($"Playing into {headphones.FriendlyName}. Press Enter to stop.");
    Console.ReadLine();
    capture.StopRecording();
    output.Stop();
    headphones.Dispose();
}

static double SmallestPeriodMs(MMDevice device)
{
    var period = device.AudioClient.DefaultDevicePeriod;
    return period / 10_000d;
}

static void WriteFloat(byte[] target, int offset, float value)
{
    BitConverter.TryWriteBytes(target.AsSpan(offset, 4), Math.Clamp(value, -1, 1));
}
