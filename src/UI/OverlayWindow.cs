using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Bisik.UI;

public enum OverlayWindowMode
{
    DotNearCursor,
    BarOverlay,
    TrayIconOnly,
    Processing,
}

public enum OverlayProcessingPhase
{
    Transcribing,
    PostProcessing,
}

/// <summary>
/// Frameless, always-on-top recording / processing indicator.
/// Rendered as a true per-pixel-alpha layered window (UpdateLayeredWindow) so that
/// soft shadows, feathered glows and anti-aliased rounded corners blend cleanly over
/// any background instead of being chroma-keyed. The window is click-through and never
/// steals focus from the active typing target.
/// </summary>
public sealed class OverlayWindow : Form
{
    // --- Geometry (content sizes exclude the shadow padding that surrounds them) ---
    private const int ShadowPadding = 8;
    private const int BarScreenMargin = 10;
    private const int CursorOffsetX = 20;
    private const int CursorOffsetY = 20;

    private static readonly Size DotContentSize = new(152, 30);
    private static readonly Size BarContentSize = new(184, 30);
    private static readonly Size ProcessingContentSize = new(152, 30);

    // --- Animation tuning ---
    private const int FrameIntervalMs = 16; // ~60 fps
    private const double LevelAttackRate = 22d;
    private const double LevelReleaseRate = 9d;
    private const double OpacityRate = 16d;
    private const double WaveShiftIntervalSeconds = 0.045d;
    private const int ShadowLayers = 4;
    private const double ShadowBaseAlpha = 18d;

    // --- Win32 layered-window plumbing ---
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const int HwndTopMost = -1;
    private const int UlwAlpha = 0x02;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _timeSeconds;
    private long _lastFrameTicks;
    private double _targetLevel;
    private double _smoothedLevel;
    private double _opacity;
    private double _targetOpacity;
    private bool _hiding;
    private double _waveAccumulator;
    private double[] _waveBars = Array.Empty<double>();
    private bool _barAtBottom;
    private OverlayProcessingPhase _processingPhase;
    private Point _frozenLocation;
    private bool _locationFrozen;
    private readonly Font _statusFont = new("Segoe UI", 9f);
    public string StatusText => Mode == OverlayWindowMode.Processing
        ? _processingPhase == OverlayProcessingPhase.Transcribing ? "Transcribing…" : "Cleaning up…"
        : "Recording";

    public OverlayWindow()
    {
        TopMost = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = AddPadding(DotContentSize);

        _renderTimer = new System.Windows.Forms.Timer
        {
            Interval = FrameIntervalMs,
        };
        _renderTimer.Tick += (_, _) => OnFrame();
    }

    public OverlayWindowMode Mode { get; private set; } = OverlayWindowMode.DotNearCursor;

    public double Level => _smoothedLevel;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            createParams.ExStyle |= 0x00080000; // WS_EX_LAYERED  (per-pixel alpha)
            createParams.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            createParams.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW (hidden from Alt+Tab)
            createParams.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (click-through)
            return createParams;
        }
    }

    public void SetMode(OverlayWindowMode mode)
    {
        if (mode == OverlayWindowMode.TrayIconOnly)
        {
            Mode = mode;
            HideImmediate();
            return;
        }

        Mode = mode;
    }

    public void SetLevel(double level)
    {
        _targetLevel = DisplayLevel(level);
    }

    internal static double DisplayLevel(double peak)
    {
        if (!double.IsFinite(peak) || peak <= 0) return 0;
        // Display quiet speech on a dB scale. Capture gain and VAD still use the original samples.
        return Math.Clamp((20 * Math.Log10(Math.Min(peak, 1)) + 60) / 45, 0, 1);
    }

    public void ShowDotNearCursor()
    {
        Mode = OverlayWindowMode.DotNearCursor;
        ResetMeter();
        BeginShow(DotContentSize);
        UpdateDotLocation();
        RenderFrame();
    }

    public void ShowBarOverlay(bool atBottom = false)
    {
        _barAtBottom = atBottom;
        Mode = OverlayWindowMode.BarOverlay;
        ResetMeter();
        BeginShow(BarContentSize);
        UpdateBarLocation();
        RenderFrame();
    }

    public void ShowProcessingOverlay(OverlayProcessingPhase phase)
    {
        _processingPhase = phase;

        if (!_locationFrozen)
        {
            _frozenLocation = Cursor.Position;
            _locationFrozen = true;
        }

        Mode = OverlayWindowMode.Processing;
        BeginShow(ProcessingContentSize);
        UpdateProcessingLocation();
        RenderFrame();
    }

    public void HideOverlay()
    {
        _locationFrozen = false;

        if (!Visible)
        {
            return;
        }

        // Fade out smoothly, then hide once transparent.
        _hiding = true;
        _targetOpacity = 0d;
        if (!_renderTimer.Enabled)
        {
            _renderTimer.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderTimer.Dispose();
            _statusFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private Size AddPadding(Size content)
        => new((int)Math.Ceiling((content.Width + ShadowPadding * 2) * DeviceDpi / 96d),
            (int)Math.Ceiling((content.Height + ShadowPadding * 2) * DeviceDpi / 96d));

    private void ResetMeter()
    {
        _targetLevel = _smoothedLevel = _waveAccumulator = 0;
        Array.Clear(_waveBars);
    }

    private void BeginShow(Size contentSize)
    {
        Size = AddPadding(contentSize);
        _hiding = false;
        _targetOpacity = 1d;

        if (!Visible)
        {
            // Start from fully transparent so the appearance fades in.
            _opacity = 0d;
            EnsureVisibleTopMost();
        }

        if (!_renderTimer.Enabled)
        {
            _lastFrameTicks = _clock.ElapsedTicks;
            _renderTimer.Start();
        }
    }

    private void HideImmediate()
    {
        _renderTimer.Stop();
        _hiding = false;
        _opacity = 0d;
        _targetOpacity = 0d;
        _locationFrozen = false;
        Hide();
    }

    private void OnFrame()
    {
        var now = _clock.ElapsedTicks;
        var dt = (now - _lastFrameTicks) / (double)Stopwatch.Frequency;
        _lastFrameTicks = now;
        if (dt <= 0d)
        {
            dt = FrameIntervalMs / 1000d;
        }

        dt = Math.Min(dt, 0.1d); // clamp after a stall so easing never jumps
        _timeSeconds += dt;

        _opacity = Approach(_opacity, _targetOpacity, OpacityRate, dt);

        var levelRate = _targetLevel > _smoothedLevel ? LevelAttackRate : LevelReleaseRate;
        _smoothedLevel = Approach(_smoothedLevel, _targetLevel, levelRate, dt);

        AdvanceWaveform(dt);

        if (Mode == OverlayWindowMode.DotNearCursor)
        {
            UpdateDotLocation();
        }

        if (_hiding && _opacity <= 0.02d)
        {
            HideImmediate();
            return;
        }

        RenderFrame();
    }

    private void EnsureVisibleTopMost()
    {
        if (!Visible)
        {
            Show();
        }

        SetWindowPos(
            Handle,
            new IntPtr(HwndTopMost),
            0,
            0,
            0,
            0,
            SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);
    }

    private void UpdateDotLocation()
    {
        if (Mode != OverlayWindowMode.DotNearCursor)
        {
            return;
        }

        var cursor = Cursor.Position;
        var area = Screen.FromPoint(cursor).WorkingArea;
        Location = new Point(Math.Clamp(cursor.X + CursorOffsetX, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(cursor.Y + CursorOffsetY, area.Top, Math.Max(area.Top, area.Bottom - Height)));
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
    }

    private void UpdateProcessingLocation()
    {
        var screen = Screen.FromPoint(_locationFrozen ? _frozenLocation : Cursor.Position);
        var x = screen.WorkingArea.Left + ((screen.WorkingArea.Width - Width) / 2);
        var y = screen.WorkingArea.Bottom - Height - BarScreenMargin;
        Location = new Point(x, y);
    }

    // ----------------------------------------------------------------------------------
    //  Layered render pipeline
    // ----------------------------------------------------------------------------------

    private void RenderFrame()
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        var width = Width;
        var height = Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            var scale = DeviceDpi / 96f;
            graphics.ScaleTransform(scale, scale);

            var content = new RectangleF(
                ShadowPadding,
                ShadowPadding,
                width / scale - (ShadowPadding * 2),
                height / scale - (ShadowPadding * 2));

            switch (Mode)
            {
                case OverlayWindowMode.DotNearCursor:
                case OverlayWindowMode.BarOverlay:
                    RenderRecording(graphics, content);
                    break;
                case OverlayWindowMode.Processing when _processingPhase == OverlayProcessingPhase.Transcribing:
                    RenderTranscribing(graphics, content);
                    break;
                case OverlayWindowMode.Processing:
                    RenderPostProcessing(graphics, content);
                    break;
            }
        }

        PushLayeredBitmap(bitmap);
    }

    private void RenderRecording(Graphics graphics, RectangleF content)
    {
        var accent = Color.FromArgb(235, 226, 78, 70); // warm coral-red
        DrawShell(graphics, content, accent);

        DrawStatus(graphics, content, 68f);
        var meter = new RectangleF(content.Left + 10, content.Top + 6, 48, content.Height - 12);
        EnsureWaveBars(meter.Width);

        var barCount = _waveBars.Length;
        if (barCount == 0)
        {
            return;
        }

        var gap = 3f;
        var barWidth = Math.Max(2.4f, (meter.Width - (gap * (barCount - 1))) / barCount);
        var centerY = meter.Top + (meter.Height / 2f);
        var maxHalf = (meter.Height / 2f) - 1f;

        for (var i = 0; i < barCount; i++)
        {
            var magnitude = (float)Math.Clamp(_waveBars[i], 0d, 1d);
            var half = Math.Max(1f, magnitude * maxHalf);
            var x = meter.Left + (i * (barWidth + gap));
            var rect = new RectangleF(x, centerY - half, barWidth, half * 2f);

            // Brighter at the leading edge to read as a live signal.
            var brightness = 0.62f + (magnitude * 0.38f);
            var color = Scale(accent, brightness, 232);
            using var brush = new SolidBrush(color);
            using var path = RoundedRect(rect, barWidth / 2f);
            graphics.FillPath(brush, path);
        }
    }

    private void RenderTranscribing(Graphics graphics, RectangleF content)
    {
        var accent = Color.FromArgb(235, 224, 168, 78); // amber
        DrawShell(graphics, content, accent);

        DrawStatus(graphics, content, 38f);
        var center = new PointF(content.Left + 21f, content.Top + (content.Height / 2f));
        var radius = (Math.Min(content.Width, content.Height) / 2f) - 8f;
        var diameter = radius * 2f;
        var arcRect = new RectangleF(center.X - radius, center.Y - radius, diameter, diameter);

        // Faint full track.
        using (var trackPen = new Pen(Color.FromArgb(40, 255, 255, 255), 3.2f))
        {
            graphics.DrawEllipse(trackPen, arcRect);
        }

        // Continuously rotating arc with round caps.
        var startAngle = (float)((_timeSeconds * 220d) % 360d);
        using var arcPen = new Pen(accent, 3.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawArc(arcPen, arcRect, startAngle, 100f);

        // Soft trailing comet head.
        var headAngle = (startAngle + 100f) * Math.PI / 180d;
        var hx = center.X + ((float)Math.Cos(headAngle) * radius);
        var hy = center.Y + ((float)Math.Sin(headAngle) * radius);
        using var headBrush = new SolidBrush(Scale(accent, 1.1f, 235));
        graphics.FillEllipse(headBrush, hx - 2.6f, hy - 2.6f, 5.2f, 5.2f);
    }

    private void RenderPostProcessing(Graphics graphics, RectangleF content)
    {
        var accent = Color.FromArgb(235, 96, 178, 180); // teal
        DrawShell(graphics, content, accent);

        DrawStatus(graphics, content, 38f);
        var center = new PointF(content.Left + 21f, content.Top + (content.Height / 2f));
        var orbit = (Math.Min(content.Width, content.Height) / 2f) - 9f;

        // Pulsing core.
        var corePulse = 0.5f + (0.5f * (float)Math.Sin(_timeSeconds * 3.2d));
        var coreRadius = 2.4f + (corePulse * 1.6f);
        using (var coreBrush = new SolidBrush(Color.FromArgb(120 + (int)(corePulse * 90f), accent)))
        {
            graphics.FillEllipse(coreBrush, center.X - coreRadius, center.Y - coreRadius, coreRadius * 2f, coreRadius * 2f);
        }

        // Three orbiting dots, 120 deg apart, each pulsing on its own phase.
        for (var i = 0; i < 3; i++)
        {
            var baseAngle = (_timeSeconds * 150d) + (i * 120d);
            var rad = baseAngle * Math.PI / 180d;
            var dx = center.X + ((float)Math.Cos(rad) * orbit);
            var dy = center.Y + ((float)Math.Sin(rad) * orbit);

            var pulse = 0.5f + (0.5f * (float)Math.Sin((_timeSeconds * 4d) + (i * 2.094d)));
            var dotRadius = 2.2f + (pulse * 1.8f);
            var color = Scale(accent, 0.7f + (pulse * 0.3f), 235);
            using var dotBrush = new SolidBrush(color);
            graphics.FillEllipse(dotBrush, dx - dotRadius, dy - dotRadius, dotRadius * 2f, dotRadius * 2f);
        }
    }

    private void DrawShell(Graphics graphics, RectangleF content, Color accent)
    {
        const float radius = 14f;

        DrawSoftShadow(graphics, content, radius);

        using (var bodyBrush = new SolidBrush(Color.FromArgb(232, 26, 28, 32)))
        using (var bodyPath = RoundedRect(content, radius))
        {
            graphics.FillPath(bodyBrush, bodyPath);

            // Subtle top gloss for depth.
            using var gloss = new LinearGradientBrush(
                new RectangleF(content.Left, content.Top, content.Width, content.Height * 0.55f),
                Color.FromArgb(28, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                LinearGradientMode.Vertical);
            var glossRegion = new RectangleF(content.Left, content.Top, content.Width, content.Height * 0.55f);
            using var glossPath = RoundedRect(glossRegion, radius);
            graphics.FillPath(gloss, glossPath);

            // Thin hairline border + a faint accent-tinted inner edge.
            using var borderPen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f);
            graphics.DrawPath(borderPen, bodyPath);
            using var accentPen = new Pen(Color.FromArgb(45, accent), 1f);
            using var accentPath = RoundedRect(RectangleF.Inflate(content, -1.4f, -1.4f), radius - 1.4f);
            graphics.DrawPath(accentPen, accentPath);
        }
    }

    private void DrawStatus(Graphics graphics, RectangleF content, float left)
    {
        using var brush = new SolidBrush(Color.FromArgb(245, 239, 241, 245));
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(StatusText, _statusFont, brush,
            new RectangleF(content.Left + left, content.Top, content.Width - left - 8, content.Height), format);
    }

    private static void DrawSoftShadow(Graphics graphics, RectangleF content, float radius)
    {
        for (var i = ShadowLayers; i >= 1; i--)
        {
            var spread = i * 1.7f;
            var rect = RectangleF.Inflate(content, spread, spread);
            rect.Offset(0, i * 0.5f); // bias the shadow slightly downward
            var alpha = (int)Math.Round(ShadowBaseAlpha * (1d - ((double)i / (ShadowLayers + 1))));
            if (alpha <= 0)
            {
                continue;
            }

            using var shadowBrush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            using var path = RoundedRect(rect, radius + spread);
            graphics.FillPath(shadowBrush, path);
        }
    }

    private void AdvanceWaveform(double dt)
    {
        if (_waveBars.Length == 0)
        {
            return;
        }

        _waveAccumulator += dt;
        if (_waveAccumulator < WaveShiftIntervalSeconds)
        {
            return;
        }

        _waveAccumulator = 0d;

        // Scroll the buffer left and push a new sample on the right.
        for (var i = 0; i < _waveBars.Length - 1; i++)
        {
            _waveBars[i] = _waveBars[i + 1];
        }

        _waveBars[^1] = _smoothedLevel;
    }

    private void EnsureWaveBars(float meterWidth)
    {
        var desired = Math.Max(8, (int)(meterWidth / 6f));
        if (_waveBars.Length == desired)
        {
            return;
        }

        var resized = new double[desired];
        var copy = Math.Min(_waveBars.Length, desired);
        Array.Copy(_waveBars, _waveBars.Length - copy, resized, desired - copy, copy);
        _waveBars = resized;
    }

    private void PushLayeredBitmap(Bitmap bitmap)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;

        try
        {
            // GetHbitmap with a zero background returns premultiplied ARGB, matching AC_SRC_ALPHA.
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var source = new POINT { x = 0, y = 0 };
            var topLeft = new POINT { x = Left, y = Top };

            var blend = new BLENDFUNCTION
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Clamp((int)Math.Round(_opacity * 255d), 0, 255),
                AlphaFormat = AcSrcAlpha,
            };

            UpdateLayeredWindow(
                Handle,
                screenDc,
                ref topLeft,
                ref size,
                memDc,
                ref source,
                0,
                ref blend,
                UlwAlpha);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
            }

            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static double Approach(double current, double target, double rate, double dt)
        => current + ((target - current) * (1d - Math.Exp(-rate * dt)));

    private static Color Scale(Color color, float factor, int alpha)
    {
        var r = (int)Math.Clamp(color.R * factor, 0f, 255f);
        var g = (int)Math.Clamp(color.G * factor, 0f, 255f);
        var b = (int)Math.Clamp(color.B * factor, 0f, 255f);
        return Color.FromArgb(Math.Clamp(alpha, 0, 255), r, g, b);
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var diameter = Math.Max(0.1f, radius * 2f);
        diameter = Math.Min(diameter, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ----------------------------------------------------------------------------------
    //  Win32 interop
    // ----------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
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

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pprSrc,
        int crKey,
        ref BLENDFUNCTION pblend,
        int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
