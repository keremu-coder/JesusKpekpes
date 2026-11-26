using System.Drawing.Drawing2D;

namespace JesusKpekpes;

// A round stick display for the Stick centre window: the circle, a cross, a dashed ring for the healthy zone, and a dot
// where the stick reads right now. It draws only the numbers it is given. It never touches the controller.
sealed class StickDial : Control
{
    private float _x;
    private float _y;
    private float _colorX;
    private float _colorY;
    private bool _live;
    private float _zoom = 1f;

    public StickDial()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(18, 20, 28);
        TabStop = false;
    }

    // How much bigger small movements are drawn. 1 is true scale. The numbers shown next to the dial are never zoomed.
    public float Zoom
    {
        get => _zoom;
        set
        {
            var next = Math.Max(1f, value);
            if (next == _zoom) return;
            _zoom = next;
            Invalidate();
        }
    }

    // x and y are the latest report, -1 to 1. colorX and colorY are the recent mean, used only for the dot colour,
    // so one noisy sample does not flash the stick between green and amber.
    public void SetPosition(float x, float y, bool live, float colorX, float colorY)
    {
        x = Math.Clamp(x, -1f, 1f);
        y = Math.Clamp(y, -1f, 1f);
        colorX = Math.Clamp(colorX, -1f, 1f);
        colorY = Math.Clamp(colorY, -1f, 1f);
        if (x == _x && y == _y && colorX == _colorX && colorY == _colorY && live == _live) return;
        _x = x;
        _y = y;
        _colorX = colorX;
        _colorY = colorY;
        _live = live;
        Invalidate();
    }

    public void SetPosition(float x, float y, bool live) => SetPosition(x, y, live, x, y);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var size = Math.Min(Width, Height) - 10f;
        if (size < 30f) return;
        var left = (Width - size) / 2f;
        var top = (Height - size) / 2f;
        var radius = size / 2f;
        var cx = left + radius;
        var cy = top + radius;
        var reach = radius - 8f;

        using var rim = new Pen(Color.FromArgb(88, 101, 242), 2f);
        using var grid = new Pen(Color.FromArgb(48, 53, 72), 1f);
        using var ring = new Pen(Color.FromArgb(60, 216, 124), 1f) { DashStyle = DashStyle.Dash };
        g.DrawEllipse(rim, left, top, size, size);
        g.DrawLine(grid, cx, top + 2f, cx, top + size - 2f);
        g.DrawLine(grid, left + 2f, cy, left + size - 2f, cy);

        // The dashed ring is one USB count from 127.5. Inside it, the mean is still on a centre code.
        var healthy = StickMath.OffsetWarnPercent / 100f * _zoom * reach;
        if (healthy >= 3f && healthy < reach)
            g.DrawEllipse(ring, cx - healthy, cy - healthy, healthy * 2f, healthy * 2f);
        if (_zoom >= 8f && healthy >= 3f && healthy < reach)
        {
            using var tick = new Pen(Color.FromArgb(120, 126, 148), 1f);
            g.DrawLine(tick, cx - healthy, cy - 3f, cx - healthy, cy + 3f);
            g.DrawLine(tick, cx + healthy, cy - 3f, cx + healthy, cy + 3f);
            g.DrawLine(tick, cx - 3f, cy - healthy, cx + 3f, cy - healthy);
            g.DrawLine(tick, cx - 3f, cy + healthy, cx + 3f, cy + healthy);
        }

        var dx = _x * _zoom;
        var dy = _y * _zoom;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length > 1f)
        {
            dx /= length;
            dy /= length;
        }
        var px = cx + dx * reach;
        var py = cy + dy * reach;

        var offsetPercent = MathF.Sqrt(_colorX * _colorX + _colorY * _colorY) * 100f;
        Color dot;
        if (!_live) dot = Color.FromArgb(120, 126, 148);
        else if (offsetPercent < StickMath.OffsetWarnPercent) dot = Color.FromArgb(60, 216, 124);
        else if (offsetPercent < 15f) dot = Color.FromArgb(255, 176, 0);
        else dot = Color.FromArgb(242, 243, 245);
        using var brush = new SolidBrush(dot);
        g.FillEllipse(brush, px - 6f, py - 6f, 12f, 12f);
    }
}
