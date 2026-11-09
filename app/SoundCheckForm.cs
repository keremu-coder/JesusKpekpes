using NAudio.Wave;

namespace JesusKpekpes;

sealed class SoundCheckForm : Form
{
    readonly TextBox _out = new();
    readonly List<SoundStats> _runs = [];
    readonly Button _record = new();
    bool _busy;

    public SoundCheckForm()
    {
        Text = "Sound check";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 560);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 10f);

        var note = new Label
        {
            Text = "Play the same game clip each time, with the same volume. Record once with one tune, change the tune, record again. The second result is compared with the first.",
            Bounds = new Rectangle(16, 12, 588, 64),
            ForeColor = Color.FromArgb(180, 184, 198),
        };
        _record.Text = "Record 10 seconds";
        _record.Bounds = new Rectangle(16, 84, 220, 40);
        _record.FlatStyle = FlatStyle.Flat;
        _record.BackColor = Color.FromArgb(88, 101, 242);
        _record.ForeColor = Color.White;
        _record.FlatAppearance.BorderSize = 0;
        _record.Click += async (_, _) => await RecordOnce();
        var clear = new Button
        {
            Text = "Start over",
            Bounds = new Rectangle(250, 84, 160, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 44, 60),
            ForeColor = Color.White,
        };
        clear.FlatAppearance.BorderSize = 0;
        clear.Click += (_, _) => { _runs.Clear(); _out.Text = "Cleared."; };
        _out.SetBounds(16, 140, 588, 404);
        _out.Multiline = true;
        _out.ReadOnly = true;
        _out.ScrollBars = ScrollBars.Vertical;
        _out.BackColor = Color.FromArgb(18, 20, 28);
        _out.ForeColor = Color.FromArgb(242, 243, 245);
        _out.BorderStyle = BorderStyle.None;
        _out.Font = new Font("Consolas", 10f);
        Controls.AddRange([note, _record, clear, _out]);
    }

    async Task RecordOnce()
    {
        if (_busy) return;
        _busy = true;
        _record.Enabled = false;
        try
        {
            _out.Text += "\r\nRecording 10 seconds. Play the clip now.\r\n";
            var (mono, rate) = await Capture(10);
            var stats = LoudnessMath.Analyze(mono, rate);
            _runs.Add(stats);
            var text = new System.Text.StringBuilder();
            text.AppendLine($"Run {_runs.Count}");
            text.AppendLine(stats.Describe());
            if (stats.ActiveSeconds < 1)
                text.AppendLine("Almost nothing was heard. Check that the sound plays on your default playback device.");
            if (_runs.Count >= 2)
            {
                text.AppendLine();
                text.AppendLine(SoundStats.Compare(_runs[^2], _runs[^1]));
                if (Math.Abs(_runs[^1].ActiveDb - _runs[^2].ActiveDb) < 0.05 && Math.Abs(_runs[^1].StepMinusLowDb - _runs[^2].StepMinusLowDb) < 0.05)
                    text.AppendLine("The two runs match almost exactly. Either the tune did not change, or this recording does not include the tune (Windows may record the sound before Equalizer APO).");
            }
            _out.Text += "\r\n" + text;
        }
        catch (Exception ex)
        {
            _out.Text += "\r\nRecording failed: " + ex.Message + "\r\n";
        }
        finally
        {
            _record.Enabled = true;
            _busy = false;
        }
    }

    static async Task<(float[] Mono, int Rate)> Capture(int seconds)
    {
        using var capture = new WasapiLoopbackCapture();
        var format = capture.WaveFormat;
        if (format.BitsPerSample != 32 || (format.Encoding != WaveFormatEncoding.IeeeFloat && format.Encoding != WaveFormatEncoding.Extensible))
            throw new InvalidOperationException("The playback format is not 32-bit float, which this check needs.");
        var channels = Math.Max(1, format.Channels);
        var samples = new List<float>(format.SampleRate * seconds);
        var stopped = new TaskCompletionSource();
        capture.DataAvailable += (_, e) =>
        {
            var frames = e.BytesRecorded / (4 * channels);
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += BitConverter.ToSingle(e.Buffer, (f * channels + c) * 4);
                samples.Add(sum / channels);
            }
        };
        capture.RecordingStopped += (_, _) => stopped.TrySetResult();
        capture.StartRecording();
        await Task.Delay(seconds * 1000);
        capture.StopRecording();
        await stopped.Task;
        return (samples.ToArray(), format.SampleRate);
    }
}
