using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CS90.Audio;
using CS90.Services;

namespace CS90.Controls;

/// <summary>
/// Waveform renderer + selection + click-to-seek + zoom/scroll, drawn from the multiresolution peak cache
/// (never from raw audio, so long recordings stay cheap).
/// Left-drag = select, click = move playhead, drag a selection edge = adjust,
/// Ctrl+wheel = zoom, wheel/Shift+wheel = scroll, double-click = reset zoom + clear selection.
/// </summary>
public sealed class WaveformControl : FrameworkElement
{
    private enum Drag { None, New, Start, End }

    private RecordingManager? _mgr;
    private double _viewStart;   // frames
    private double _viewFrames;  // frames visible; <= 0 means "fit all"
    private Drag _drag = Drag.None;
    private long _anchor;
    private bool _moved;
    private Point _downPoint;

    private static readonly Brush BarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)));
    private static readonly Brush BarDim = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)));
    private static readonly Brush BarSel = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));
    private static readonly Brush SelFill = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xE8, 0x3A, 0x2A)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x3A, 0x2A)));
    private static readonly Pen RedPen = FreezePen(new Pen(Red, 1.5));
    private static readonly Pen PlayheadPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), 1.2));
    private static readonly Typeface Mono = new("Consolas");

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Brush.Freeze(); p.Freeze(); return p; }

    public WaveformControl()
    {
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        Focusable = false;
    }

    public void Bind(RecordingManager mgr) => _mgr = mgr;

    public void ResetView() { _viewStart = 0; _viewFrames = 0; InvalidateVisual(); }

    // ---- geometry helpers ------------------------------------------------------------------

    private long Total => _mgr?.LiveFrames ?? 0;

    private double VisibleFrames
    {
        get
        {
            double total = Math.Max(1, Total);
            if (_viewFrames <= 0 || _mgr?.State == TransportState.Recording) return total;
            return Math.Min(_viewFrames, total);
        }
    }

    private double ViewStart
    {
        get
        {
            if (_mgr?.State == TransportState.Recording || _viewFrames <= 0) return 0;
            return Math.Clamp(_viewStart, 0, Math.Max(0, Total - VisibleFrames));
        }
    }

    private double XToFrame(double x) => ViewStart + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * VisibleFrames;
    private double FrameToX(double f) => (f - ViewStart) / VisibleFrames * ActualWidth;

    // ---- rendering -------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testing surface
        if (_mgr == null || w < 4 || h < 4) return;

        long total = Total;
        double mid = h / 2;
        const double barW = 3, gap = 2, pitch = barW + gap;

        if (total <= 0)
        {
            // idle placeholder: faint dotted baseline, like the reference image's quiet tail
            for (double x = 0; x < w; x += pitch)
                dc.DrawRectangle(BarDim, null, new Rect(x, mid - 1, barW, 2));
            return;
        }

        double vs = ViewStart, vf = VisibleFrames;
        var sel = _mgr.Selection;
        int cols = (int)(w / pitch);

        // selection background
        if (sel is { } s0)
        {
            double sx = FrameToX(s0.start), ex = FrameToX(s0.end);
            dc.DrawRectangle(SelFill, null, new Rect(Math.Max(0, sx), 0, Math.Max(1, Math.Min(w, ex) - Math.Max(0, sx)), h));
        }

        var peaks = _mgr.Peaks;
        int bins = peaks.BinCount;
        for (int c = 0; c < cols; c++)
        {
            double f0 = vs + vf * c / cols;
            double f1 = vs + vf * (c + 1) / cols;
            int b0 = (int)(f0 / PeakCache.FramesPerBin);
            int b1 = Math.Max(b0 + 1, (int)Math.Ceiling(f1 / PeakCache.FramesPerBin));
            if (b0 >= bins) break;
            var (mn, mx) = peaks.Range(b0, b1);
            double amp = Math.Clamp(Math.Max(Math.Abs(mn), Math.Abs(mx)), 0, 1);
            double bh = Math.Max(2, amp * (h - 6));
            double x = c * pitch;
            bool inSel = sel is { } s && (f0 + f1) / 2 >= s.start && (f0 + f1) / 2 <= s.end;
            dc.DrawRectangle(inSel ? BarSel : (sel.HasValue ? BarDim : BarBrush), null,
                new Rect(x, mid - bh / 2, barW, bh));
        }

        // selection edges + start/end labels
        if (sel is { } s1)
        {
            double sx = FrameToX(s1.start), ex = FrameToX(s1.end);
            if (sx >= 0 && sx <= w) dc.DrawLine(RedPen, new Point(sx, 0), new Point(sx, h));
            if (ex >= 0 && ex <= w) dc.DrawLine(RedPen, new Point(ex, 0), new Point(ex, h));
            DrawLabel(dc, Fmt(s1.start), Math.Clamp(sx + 3, 0, w - 60), 0);
            DrawLabel(dc, Fmt(s1.end), Math.Clamp(ex - 57, 0, w - 60), h - 12);
        }

        // playhead
        double px = FrameToX(_mgr.PositionFrames);
        if (_mgr.State != TransportState.Recording && px >= 0 && px <= w)
            dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
    }

    private void DrawLabel(DrawingContext dc, string text, double x, double y)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 9,
            BarSel, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(x, y));
    }

    private string Fmt(long frame)
    {
        int rate = Math.Max(1, _mgr?.SampleRate ?? 1);
        var t = TimeSpan.FromSeconds((double)frame / rate);
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    // ---- interaction -----------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_mgr == null || _mgr.State == TransportState.Recording || Total <= 0) return;
        Focus();
        CaptureMouse();
        _downPoint = e.GetPosition(this);
        _moved = false;

        if (e.ClickCount == 2) { _mgr.ClearSelection(); ResetView(); _drag = Drag.None; return; }

        // grab an existing edge within 6 px
        if (_mgr.Selection is { } s)
        {
            if (Math.Abs(_downPoint.X - FrameToX(s.start)) <= 6) { _drag = Drag.Start; _anchor = s.end; return; }
            if (Math.Abs(_downPoint.X - FrameToX(s.end)) <= 6) { _drag = Drag.End; _anchor = s.start; return; }
        }
        _drag = Drag.New;
        _anchor = (long)XToFrame(_downPoint.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_mgr == null) return;
        var p = e.GetPosition(this);

        if (_drag == Drag.None)
        {
            bool nearEdge = _mgr.Selection is { } s &&
                (Math.Abs(p.X - FrameToX(s.start)) <= 6 || Math.Abs(p.X - FrameToX(s.end)) <= 6);
            Cursor = nearEdge ? Cursors.SizeWE : Cursors.IBeam;
            return;
        }

        if (!_moved && Math.Abs(p.X - _downPoint.X) < 3) return; // click threshold
        _moved = true;
        long f = (long)XToFrame(p.X);
        _mgr.SetSelection(_anchor, f);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        ReleaseMouseCapture();
        if (!_moved && _drag == Drag.New && _mgr != null)
        {
            // plain click: move the playhead and drop any selection
            _mgr.ClearSelection();
            _mgr.Seek((long)XToFrame(_downPoint.X));
        }
        _drag = Drag.None;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_mgr == null || Total <= 0 || _mgr.State == TransportState.Recording) return;
        double total = Total;
        double vis = VisibleFrames;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            double cursorFrame = XToFrame(e.GetPosition(this).X);
            double ratio = e.Delta > 0 ? 0.8 : 1.25;
            double newVis = Math.Clamp(vis * ratio, Math.Min(total, _mgr.SampleRate * 0.05), total);
            double frac = (cursorFrame - ViewStart) / vis;
            _viewFrames = newVis >= total ? 0 : newVis;
            _viewStart = cursorFrame - frac * newVis;
        }
        else
        {
            _viewFrames = vis >= total ? 0 : vis;
            _viewStart = ViewStart - Math.Sign(e.Delta) * vis * 0.15;
        }
        InvalidateVisual();
        e.Handled = true;
    }
}
