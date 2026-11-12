using System.Windows;
using System.Windows.Media;

namespace CS90.Controls;

/// <summary>Vector cassette with two reels. Reel rotation speed follows <see cref="Activity"/> (0..1).</summary>
public sealed class CassetteAnimation : FrameworkElement
{
    private double _angle;
    private DateTime _last = DateTime.UtcNow;
    private bool _hooked;

    public static readonly DependencyProperty ActiveProperty =
        DependencyProperty.Register(nameof(Active), typeof(bool), typeof(CassetteAnimation),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CassetteAnimation)d).Hook()));

    /// <summary>True while recording or playing.</summary>
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

    /// <summary>Recent audio level 0..1; speeds the reels up slightly with real signal.</summary>
    public double Level { get; set; }

    private static readonly Brush Metal = Freeze(new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4)));
    private static readonly Pen Outline = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), 1.2));
    private static readonly Pen Thin = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E)), 1));
    private static readonly Brush Dark = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)));
    private static readonly Brush Window = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)));
    private static readonly Brush Tape = Freeze(new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Brush.Freeze(); p.Freeze(); return p; }

    private void Hook()
    {
        if (Active && !_hooked)
        {
            _last = DateTime.UtcNow;
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
        else if (!Active && _hooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    private void OnRendering(object? s, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = (now - _last).TotalSeconds;
        _last = now;
        double speed = 90 + 160 * Math.Clamp(Level, 0, 1); // deg/s: subtle, a bit faster with real signal
        _angle = (_angle + speed * dt) % 360;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;

        var frame = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRoundedRectangle(null, Outline, frame, 4, 4);

        // corner screws
        foreach (var p in new[] { new Point(w * 0.035, h * 0.09), new Point(w * 0.965, h * 0.09),
                                  new Point(w * 0.035, h * 0.91), new Point(w * 0.965, h * 0.91) })
        {
            dc.DrawEllipse(Dark, Thin, p, h * 0.03, h * 0.03);
            dc.DrawEllipse(Metal, null, p, h * 0.012, h * 0.012);
        }

        double reelR = h * 0.31;
        double cy = h * 0.44;
        var left = new Point(w * 0.235, cy);
        var right = new Point(w * 0.765, cy);

        // tape window between reels
        var win = new Rect(w * 0.33, cy - reelR * 0.78, w * 0.34, reelR * 1.56);
        dc.DrawRectangle(Dark, Thin, win);
        dc.DrawRectangle(Window, null, new Rect(win.X + win.Width * 0.3, win.Y + 4, win.Width * 0.4, win.Height - 8));
        dc.DrawLine(Thin, new Point(win.X, win.Y + win.Height * 0.2), new Point(win.Right, win.Y + win.Height * 0.2));
        dc.DrawLine(Thin, new Point(win.X, win.Bottom - win.Height * 0.2), new Point(win.Right, win.Bottom - win.Height * 0.2));

        DrawReel(dc, left, reelR, _angle);
        DrawReel(dc, right, reelR, _angle);

        // bottom trapezoid
        var fig = new PathFigure(new Point(w * 0.385, h * 0.99), new[]
        {
            new LineSegment(new Point(w * 0.415, h * 0.80), true),
            new LineSegment(new Point(w * 0.585, h * 0.80), true),
            new LineSegment(new Point(w * 0.615, h * 0.99), true)
        }, false);
        var geo = new PathGeometry(new[] { fig }); geo.Freeze();
        dc.DrawGeometry(null, Thin, geo);
    }

    private static void DrawReel(DrawingContext dc, Point c, double r, double angle)
    {
        dc.DrawEllipse(Dark, Outline, c, r, r);                 // outer ring
        dc.DrawEllipse(Tape, null, c, r * 0.80, r * 0.80);      // tape pack
        dc.DrawEllipse(Dark, null, c, r * 0.66, r * 0.66);
        dc.DrawEllipse(null, Outline, c, r * 0.60, r * 0.60);   // hub ring

        dc.PushTransform(new RotateTransform(angle, c.X, c.Y));
        for (int i = 0; i < 6; i++)                              // six hub teeth
        {
            dc.PushTransform(new RotateTransform(i * 60, c.X, c.Y));
            dc.DrawRectangle(Metal, null, new Rect(c.X - r * 0.07, c.Y - r * 0.60, r * 0.14, r * 0.20));
            dc.Pop();
        }
        dc.Pop();
        dc.DrawEllipse(Dark, Thin, c, r * 0.22, r * 0.22);
    }
}
