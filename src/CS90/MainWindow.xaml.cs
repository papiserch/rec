using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using CS90.Audio;
using CS90.Services;

namespace CS90;

public partial class MainWindow : Window
{
    private const double AspectRatio = 600.0 / 500.0;

    private record WindowSettings(double Left, double Top, double Width);

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS90", "window.json");

    private readonly RecordingManager _mgr = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };

    private double _level;
    private bool _clearArmed;
    private DateTime _clearArmedAt;
    private Point _dragStart;
    private bool _dragArmed;

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowSettings();

        Wave.Bind(_mgr);
        _mgr.StateChanged += () => Dispatcher.BeginInvoke(UpdateUi);
        _mgr.ErrorRaised += msg => Dispatcher.BeginInvoke(() =>
            MessageBox.Show(this, msg, "CS-90", MessageBoxButton.OK, MessageBoxImage.Warning));

        _timer.Tick += Tick;
        _timer.Start();
        UpdateUi();

        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
    }

    // ---- window ----------------------------------------------------------------------------

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WindowState != WindowState.Normal) return;
        if (Math.Abs(Width / Height - AspectRatio) < 0.005) return;
        if (e.WidthChanged) Height = Width / AspectRatio;
        else Width = Height * AspectRatio;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Max_Click(sender, e); return; }
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void LoadWindowSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(SettingsPath));
            if (s == null || s.Width < MinWidth) return;
            double h = s.Width / AspectRatio;
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!vs.Contains(new Point(s.Left + 40, s.Top + 20))) return; // saved on a monitor that is gone
            Width = s.Width; Height = h; Left = s.Left; Top = s.Top;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        catch { /* ignore corrupt settings */ }
    }

    private void SaveWindowSettings()
    {
        try
        {
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new WindowSettings(b.Left, b.Top, b.Width)));
        }
        catch { }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowSettings();
        _timer.Stop();
        _mgr.Dispose(); // finalizes the WAV if a recording is still running
    }

    // ---- transport -------------------------------------------------------------------------

    private void Rec_Click(object sender, RoutedEventArgs e) { DisarmClear(); Wave.ResetView(); _mgr.Record(); }
    private void Stop_Click(object sender, RoutedEventArgs e) { DisarmClear(); _mgr.Stop(); Wave.ResetView(); }
    private void Play_Click(object sender, RoutedEventArgs e) { DisarmClear(); _mgr.Play(); }

    // CLEAR needs a second click within 3 s: no modal dialog, but no accidental deletion either.
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!_clearArmed)
        {
            _clearArmed = true;
            _clearArmedAt = DateTime.UtcNow;
            ClearLabel.Text = "SURE?";
            ClearLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x32, 0x1F));
            return;
        }
        DisarmClear();
        _mgr.Clear();
        Wave.ResetView();
    }

    private void DisarmClear()
    {
        if (!_clearArmed) return;
        _clearArmed = false;
        ClearLabel.Text = "CLEAR";
        ClearLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    }

    private void UpdateUi()
    {
        var st = _mgr.State;
        bool has = _mgr.HasRecording;
        RecBtn.IsEnabled = st == TransportState.Idle && !has;   // the recording stays intact until CLEAR
        StopBtn.IsEnabled = st != TransportState.Idle;
        PlayBtn.IsEnabled = has && st == TransportState.Idle;
        ClearBtn.IsEnabled = has && st == TransportState.Idle;
        RecBtn.ToolTip = has ? "CLEAR the current recording first" : null;
        DragZone.Opacity = has ? 1.0 : 0.4;
        if (!has) DisarmClear();

        RecDot.Effect = st == TransportState.Recording
            ? new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x30, 0x20), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9 }
            : null;
        Cassette.Active = st != TransportState.Idle;
        Wave.InvalidateVisual();
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_clearArmed && (DateTime.UtcNow - _clearArmedAt).TotalSeconds > 3) DisarmClear();

        int rate = Math.Max(1, _mgr.SampleRate);
        long pos = _mgr.PositionFrames;
        var t = TimeSpan.FromSeconds((double)pos / rate);
        TimerText.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}:{t.Milliseconds / 10:00}"; // MM:SS:CC

        // REC LED: blinks while recording
        bool rec = _mgr.State == TransportState.Recording;
        bool on = rec && (Environment.TickCount64 / 500) % 2 == 0;
        RecLed.Fill = new SolidColorBrush(on ? Color.FromRgb(0xFF, 0x30, 0x20) : Color.FromRgb(0x4A, 0x14, 0x10));

        // reel speed follows the real signal level around the current position
        double raw = 0;
        if (_mgr.State != TransportState.Idle)
        {
            int bin = (int)(pos / PeakCache.FramesPerBin);
            var (mn, mx) = _mgr.Peaks.Range(Math.Max(0, bin - 2), bin + 1);
            raw = Math.Max(Math.Abs(mn), Math.Abs(mx));
        }
        _level = Math.Max(raw, _level * 0.9);
        Cassette.Level = _level;

        if (_mgr.State != TransportState.Idle || Wave.IsMouseCaptured) Wave.InvalidateVisual();
    }

    // ---- DRAG TO DAW (native OLE file drag-and-drop) ---------------------------------------

    private void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mgr.HasRecording) return;
        _dragStart = e.GetPosition(DragZone);
        _dragArmed = true;
        DragZone.CaptureMouse();
    }

    private void Drag_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        DragZone.ReleaseMouseCapture();
    }

    private void Drag_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(DragZone);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragArmed = false;
        DragZone.ReleaseMouseCapture();

        string? path;
        try { path = _mgr.ExportForDrag(); } // selection if any, otherwise the full recording
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export failed: " + ex.Message, "CS-90", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (path == null) return;

        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DragDrop.DoDragDrop(DragZone, data, DragDropEffects.Copy | DragDropEffects.Link);
        // The exported WAV is intentionally kept: the DAW may reference this path.
    }
}
