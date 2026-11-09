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
    float[] _pendingIn = new float[Engine.BlockFrames * 8];
    int _pending;
    readonly float[][] _block = Enumerable.Range(0, 8).Select(_ => new float[Engine.BlockFrames]).ToArray();
    readonly float[] _blockLeft = new float[Engine.BlockFrames];
    readonly float[] _blockRight = new float[Engine.BlockFrames];
    readonly byte[] _blockBytes = new byte[Engine.BlockFrames * 8];

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
            BufferDuration = TimeSpan.FromMilliseconds(3),
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

    public void SetMix(float loudness, float surroundQuiet)
    {
        _engine.Loudness = loudness;
        _engine.SurroundQuiet = surroundQuiet;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var format = _capture.WaveFormat;
        var channels = format.Channels;
        var bytes = format.BitsPerSample / 8;
        var frames = e.BytesRecorded / (bytes * channels);
        if (frames <= 0) return;
        Span<float> sample = stackalloc float[8];
        for (var i = 0; i < frames; i++)
        {
            sample.Clear();
            for (var c = 0; c < Math.Min(8, channels); c++)
            {
                var offset = (i * channels + c) * bytes;
                sample[c] = format.Encoding == WaveFormatEncoding.IeeeFloat
                    ? BitConverter.ToSingle(e.Buffer, offset)
                    : BitConverter.ToInt16(e.Buffer, offset) / 32768f;
            }
            var at = _pending * 8;
            _pendingIn[at] = sample[0];
            _pendingIn[at + 1] = channels > 1 ? sample[1] : sample[0];
            _pendingIn[at + 2] = channels > 2 ? sample[2] : 0;
            _pendingIn[at + 3] = channels > 3 ? sample[3] : 0;
            _pendingIn[at + 4] = channels > 6 ? sample[6] : 0;
            _pendingIn[at + 5] = channels > 7 ? sample[7] : 0;
            _pendingIn[at + 6] = channels > 4 ? sample[4] : 0;
            _pendingIn[at + 7] = channels > 5 ? sample[5] : 0;
            _pending++;
            if (_pending < Engine.BlockFrames) continue;
            for (var f = 0; f < Engine.BlockFrames; f++)
            {
                _block[Engine.L][f] = _pendingIn[f * 8];
                _block[Engine.R][f] = _pendingIn[f * 8 + 1];
                _block[Engine.C][f] = _pendingIn[f * 8 + 2];
                _block[Engine.Lfe][f] = _pendingIn[f * 8 + 3];
                _block[Engine.Sl][f] = _pendingIn[f * 8 + 4];
                _block[Engine.Sr][f] = _pendingIn[f * 8 + 5];
                _block[Engine.Rl][f] = _pendingIn[f * 8 + 6];
                _block[Engine.Rr][f] = _pendingIn[f * 8 + 7];
            }
            _engine.Process(_block, _blockLeft, _blockRight);
            for (var f = 0; f < Engine.BlockFrames; f++)
            {
                BitConverter.TryWriteBytes(_blockBytes.AsSpan(f * 8, 4), _blockLeft[f]);
                BitConverter.TryWriteBytes(_blockBytes.AsSpan(f * 8 + 4, 4), _blockRight[f]);
            }
            _buffer.AddSamples(_blockBytes, 0, _blockBytes.Length);
            _pending = 0;
        }
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
