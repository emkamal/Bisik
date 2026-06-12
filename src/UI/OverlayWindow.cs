using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Bisik.UI;

public enum OverlayWindowMode
{
    DotNearCursor,
    BarOverlay,
    TrayIconOnly,
}

public sealed class OverlayWindow : Form
{
    private const int DotSize = 56;
    private const int BarWidth = 320;
    private const int BarHeight = 30;
    private const int BarScreenMargin = 14;
    private const int CursorOffsetX = 18;
    private const int CursorOffsetY = 18;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int HwndTopMost = -1;
    private readonly System.Windows.Forms.Timer _cursorFollowTimer;
    private readonly System.Windows.Forms.Timer _pulseTimer;
    private double _level;
    private float _pulsePhase;
    private bool _barAtBottom;

    public OverlayWindow()
    {
        TopMost = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Size = new Size(DotSize, DotSize);
        DoubleBuffered = true;

        _cursorFollowTimer = new System.Windows.Forms.Timer
        {
            Interval = 33,
        };
        _cursorFollowTimer.Tick += (_, _) => UpdateDotLocation();

        _pulseTimer = new System.Windows.Forms.Timer
        {
            Interval = 33,
        };
        _pulseTimer.Tick += (_, _) =>
        {
            _pulsePhase = (_pulsePhase + 0.12f) % (MathF.PI * 2f);
            Invalidate();
        };
    }

    public OverlayWindowMode Mode { get; private set; } = OverlayWindowMode.DotNearCursor;

    public double Level => _level;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            createParams.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            createParams.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return createParams;
        }
    }

    public void SetMode(OverlayWindowMode mode)
    {
        if (mode == OverlayWindowMode.TrayIconOnly)
        {
            Mode = mode;
            HideOverlay();
            Invalidate();
            return;
        }

        Mode = mode;

        switch (mode)
        {
            case OverlayWindowMode.DotNearCursor:
                Size = new Size(DotSize, DotSize);
                UpdateDotLocation();
                break;

            case OverlayWindowMode.BarOverlay:
                Size = new Size(BarWidth, BarHeight);
                UpdateBarLocation();
                break;
        }

        Invalidate();
    }

    public void SetLevel(double level)
    {
        _level = Math.Clamp(level, 0d, 1d);
        Invalidate();
    }

    public void ShowDotNearCursor()
    {
        SetMode(OverlayWindowMode.DotNearCursor);
        UpdateDotLocation();
        _cursorFollowTimer.Start();
        _pulseTimer.Start();
        ShowOverlay();
    }

    public void ShowBarOverlay(bool atBottom = false)
    {
        _barAtBottom = atBottom;
        SetMode(OverlayWindowMode.BarOverlay);
        UpdateBarLocation();
        _cursorFollowTimer.Stop();
        _pulseTimer.Start();
        ShowOverlay();
    }

    public void HideOverlay()
    {
        _cursorFollowTimer.Stop();
        _pulseTimer.Stop();
        Hide();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);

        if (Mode == OverlayWindowMode.DotNearCursor)
        {
            PaintDotOverlay(eventArgs.Graphics);
            return;
        }

        if (Mode == OverlayWindowMode.BarOverlay)
        {
            PaintBarOverlay(eventArgs.Graphics);
            return;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cursorFollowTimer.Dispose();
            _pulseTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowOverlay()
    {
        if (!Visible)
        {
            Show();
        }

        SetWindowPos(
            Handle,
            new IntPtr(HwndTopMost),
            Left,
            Top,
            Width,
            Height,
            SwpNoActivate | SwpShowWindow);
    }

    private void UpdateDotLocation()
    {
        if (Mode != OverlayWindowMode.DotNearCursor)
        {
            return;
        }

        var cursorPosition = Cursor.Position;
        Location = new Point(
            cursorPosition.X + CursorOffsetX,
            cursorPosition.Y + CursorOffsetY);

        if (Visible)
        {
            SetWindowPos(
                Handle,
                new IntPtr(HwndTopMost),
                Left,
                Top,
                Width,
                Height,
                SwpNoActivate | SwpShowWindow);
        }
    }

    private void UpdateBarLocation()
    {
        if (Mode != OverlayWindowMode.BarOverlay)
        {
            return;
        }

        var screen = Screen.FromPoint(Cursor.Position);
        var x = screen.WorkingArea.Left + ((screen.WorkingArea.Width - Width) / 2);
        var y = _barAtBottom
            ? screen.WorkingArea.Bottom - Height - BarScreenMargin
            : screen.WorkingArea.Top + BarScreenMargin;
        Location = new Point(x, y);

        if (Visible)
        {
            SetWindowPos(
                Handle,
                new IntPtr(HwndTopMost),
                Left,
                Top,
                Width,
                Height,
                SwpNoActivate | SwpShowWindow);
        }
    }

    private void PaintDotOverlay(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var pulseScale = 1f + (MathF.Sin(_pulsePhase) * 0.08f);
        var center = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
        var outerRadius = 18f + (float)(_level * 8f) + (pulseScale * 2f);
        var innerRadius = 10f + (float)(_level * 5f);

        using var glowBrush = new SolidBrush(Color.FromArgb(45, 217, 73, 63));
        using var ringPen = new Pen(Color.FromArgb(220, 239, 93, 81), 3f);
        using var dotBrush = new SolidBrush(Color.FromArgb(245, 214, 57, 43));
        using var highlightBrush = new SolidBrush(Color.FromArgb(220, 255, 236, 233));

        graphics.FillEllipse(
            glowBrush,
            center.X - outerRadius,
            center.Y - outerRadius,
            outerRadius * 2f,
            outerRadius * 2f);
        graphics.DrawEllipse(
            ringPen,
            center.X - (innerRadius + 6f),
            center.Y - (innerRadius + 6f),
            (innerRadius + 6f) * 2f,
            (innerRadius + 6f) * 2f);
        graphics.FillEllipse(
            dotBrush,
            center.X - innerRadius,
            center.Y - innerRadius,
            innerRadius * 2f,
            innerRadius * 2f);
        graphics.FillEllipse(
            highlightBrush,
            center.X - 3f,
            center.Y - innerRadius + 5f,
            6f,
            6f);
    }

    private void PaintBarOverlay(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new RectangleF(1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
        using var shellBrush = new SolidBrush(Color.FromArgb(228, 28, 31, 35));
        using var outlinePen = new Pen(Color.FromArgb(210, 72, 77, 86), 1.2f);
        using var levelBrush = new SolidBrush(Color.FromArgb(235, 214, 57, 43));
        using var backgroundBrush = new SolidBrush(Color.FromArgb(125, 68, 74, 82));
        using var highlightBrush = new SolidBrush(Color.FromArgb(175, 255, 236, 233));

        FillRoundedRectangle(graphics, shellBrush, bounds, 14f);
        DrawRoundedRectangle(graphics, outlinePen, bounds, 14f);

        var meterBounds = RectangleF.Inflate(bounds, -14f, -8f);
        FillRoundedRectangle(graphics, backgroundBrush, meterBounds, 8f);

        var pulseBoost = 1f + (MathF.Sin(_pulsePhase) * 0.04f);
        var activeWidth = (float)Math.Max(10d, meterBounds.Width * _level) * pulseBoost;
        var activeBounds = new RectangleF(
            meterBounds.Left,
            meterBounds.Top,
            Math.Min(meterBounds.Width, activeWidth),
            meterBounds.Height);
        FillRoundedRectangle(graphics, levelBrush, activeBounds, 8f);

        var markerX = meterBounds.Left + (meterBounds.Width * (float)_level);
        graphics.FillEllipse(highlightBrush, markerX - 4f, meterBounds.Top - 2f, 8f, meterBounds.Height + 4f);
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = CreateRoundedRectanglePath(bounds, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        using var path = CreateRoundedRectanglePath(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectanglePath(RectangleF bounds, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
