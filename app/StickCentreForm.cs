namespace JesusKpekpes;

sealed class StickCentreForm : Form
{
    readonly TextBox _out = new();
    readonly Button _test = new();
    readonly Button _apply = new();
    readonly Button _restore = new();
    bool _shown;

    public StickCentreForm()
    {
        Window.Prepare(this);
        Text = "Stick centre";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 640);
        Font = new Font("Segoe UI", 10f);
        var note = new Label
        {
            Bounds = new Rectangle(16, 12, 728, 78),
            Text = "Hands off both sticks. USB only: Bluetooth is refused. DS4Windows can stay open. The centre is written in the pad's memory and is not saved to its flash. The handle is closed when the text appears, so this window is not in the input path while you play.",
        };
        _test.Text = "Read resting sticks (5 s)";
        _test.SetBounds(16, 100, 230, 40);
        _test.Click += (_, _) => Begin(StickJob.Test);
        _apply.Text = "Apply centre in memory";
        _apply.SetBounds(258, 100, 230, 40);
        _apply.Click += (_, _) => Begin(StickJob.Apply);
        _restore.Text = "Restore backup";
        _restore.SetBounds(500, 100, 200, 40);
        _restore.Click += (_, _) => Begin(StickJob.Restore);
        _out.SetBounds(16, 156, 728, 468);
        _out.Multiline = true;
        _out.ReadOnly = true;
        _out.ScrollBars = ScrollBars.Vertical;
        _out.Font = new Font("Consolas", 9.5f);
        _out.Text = "Press Read with your hands off the sticks. The result says whether the rest point is a wrong centre or a worn stick, and it prints the report rate and the worst gap.";
        Controls.AddRange([note, _test, _apply, _restore, _out]);
        Shown += (_, _) =>
        {
            if (_shown) return;
            _shown = true;
            if (!StickStore.Default.HasCorrection) return;
            Begin(StickJob.Reapply);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        StickLink.Listen(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (StickLink.Arrival(m.Msg, m.WParam, m.LParam))
            StickLink.ReapplyStoredAsync(outcome =>
            {
                try { BeginInvoke(() => _out.AppendText("\r\n" + outcome.Text)); }
                catch (Exception) { /* window closed */ }
            });
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (Owner is { IsHandleCreated: true } owner) StickLink.Listen(owner.Handle);
        base.OnFormClosed(e);
    }

    void Begin(StickJob job)
    {
        if (!StickGate.Enter())
        {
            _out.Text = "A read or write is already running. It closes the handle when it finishes.";
            return;
        }
        _test.Enabled = false;
        _apply.Enabled = false;
        _restore.Enabled = false;
        _out.Text = job == StickJob.Test
            ? "Reading USB reports for 5 seconds. Leave both sticks alone. DS4Windows may miss reports until this finishes and the handle closes."
            : "The pad is open for a short in-memory read. It will close as soon as this finishes.";
        Task.Run(() =>
        {
            StickOutcome outcome;
            try { outcome = StickLink.Run(job); }
            catch (Exception ex) { outcome = new StickOutcome { Text = ex.Message }; }
            finally { StickGate.Leave(); }
            try
            {
                BeginInvoke(() =>
                {
                    _out.Text = outcome.Text;
                    _test.Enabled = true;
                    _apply.Enabled = true;
                    _restore.Enabled = true;
                });
            }
            catch (Exception)
            {
                /* window closed */
            }
        });
    }
}
