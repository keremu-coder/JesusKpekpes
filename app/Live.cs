using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace JesusKpekpes;

sealed class Live : IDisposable
{
    readonly MMDevice _source;
    readonly MMDevice _headphones;
    readonly WasapiLoopbackCapture _capture;
    readonly WasapiOut _output;
    readonly BufferedWaveProvider _buffer;
    readonly Engine _engine;
    float[][] _input = [];
    float[] _left = [];
    float[] _right = [];
    byte[] _interleaved = [];

    public Live(string sourceId, string headphoneId)
    {
        var enumerator = new MMDeviceEnumerator();
        _source = enumerator.GetDevice(sourceId);
        _headphones = enumerator.GetDevice(headphoneId);
        enumerator.Dispose();
        _capture = new WasapiLoopbackCapture(_source);
        _engine = new Engine(_capture.WaveFormat.SampleRate);
        var wave = WaveFormat.CreateIeeeFloatWaveFormat(_capture.WaveFormat.SampleRate, 2);
        _buffer = new BufferedWaveProvider(wave)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(12),
        };
        _output = new WasapiOut(_headphones, AudioClientShareMode.Shared, true, (int)Math.Max(1, _headphones.AudioClient.DefaultDevicePeriod / 10_000d));
        _output.Init(_buffer);
        _capture.DataAvailable += OnData;
    }

    public void Start()
    {
        _output.Play();
        _capture.StartRecording();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var format = _capture.WaveFormat;
        var channels = format.Channels;
        var bytes = format.BitsPerSample / 8;
        var frames = e.BytesRecorded / (bytes * channels);
        if (frames <= 0) return;
        if (_left.Length != frames)
        {
            _input = Enumerable.Range(0, 8).Select(_ => new float[frames]).ToArray();
            _left = new float[frames];
            _right = new float[frames];
            _interleaved = new byte[frames * 8];
        }
        for (var i = 0; i < frames; i++)
        {
            Span<float> sample = stackalloc float[8];
            for (var c = 0; c < Math.Min(8, channels); c++)
            {
                var offset = (i * channels + c) * bytes;
                sample[c] = format.Encoding == WaveFormatEncoding.IeeeFloat
                    ? BitConverter.ToSingle(e.Buffer, offset)
                    : BitConverter.ToInt16(e.Buffer, offset) / 32768f;
            }
            _input[Engine.L][i] = sample[0];
            _input[Engine.R][i] = channels > 1 ? sample[1] : sample[0];
            _input[Engine.C][i] = channels > 2 ? sample[2] : 0;
            _input[Engine.Lfe][i] = channels > 3 ? sample[3] : 0;
            _input[Engine.Rl][i] = channels > 4 ? sample[4] : 0;
            _input[Engine.Rr][i] = channels > 5 ? sample[5] : 0;
            _input[Engine.Sl][i] = channels > 6 ? sample[6] : 0;
            _input[Engine.Sr][i] = channels > 7 ? sample[7] : 0;
        }
        _engine.Process(_input, _left, _right);
        for (var i = 0; i < frames; i++)
        {
            BitConverter.TryWriteBytes(_interleaved.AsSpan(i * 8, 4), _left[i]);
            BitConverter.TryWriteBytes(_interleaved.AsSpan(i * 8 + 4, 4), _right[i]);
        }
        _buffer.AddSamples(_interleaved, 0, frames * 8);
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { /* already stopped */ }
        try { _output.Stop(); } catch { /* already stopped */ }
        _capture.Dispose();
        _output.Dispose();
        _source.Dispose();
        _headphones.Dispose();
    }
}
