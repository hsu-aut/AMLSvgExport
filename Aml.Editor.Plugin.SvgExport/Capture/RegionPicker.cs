using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Aml.Editor.Plugin.SvgExport.Capture;

/// <summary>
/// Lets the user drag a rectangle over a window, like a snipping tool. A translucent
/// overlay covers the window; the result is in the window's coordinate space.
/// </summary>
public sealed class RegionPicker
{
    private readonly TaskCompletionSource<Rect?> _result = new();
    private readonly Window _overlay;
    private readonly Canvas _canvas;
    private readonly Rectangle _band;
    private readonly TextBlock _hint;
    private Point? _start;

    /// <summary>The overlay window, for tests.</summary>
    internal Window Overlay => _overlay;

    private RegionPicker(Window target)
    {
        // Overlay exactly over the target's client area (device independent units).
        var source = PresentationSource.FromVisual(target);
        var origin = target.PointToScreen(new Point(0, 0));
        if (source?.CompositionTarget != null) origin = source.CompositionTarget.TransformFromDevice.Transform(origin);

        _canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), Cursor = Cursors.Cross };
        _band = new Rectangle { Stroke = Brushes.DodgerBlue, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x1e, 0x90, 0xff)), Visibility = Visibility.Collapsed };
        _hint = new TextBlock { Text = "Drag a rectangle to export, Esc to cancel", Foreground = Brushes.White, FontSize = 14, Background = new SolidColorBrush(Color.FromArgb(0xa0, 0, 0, 0)), Padding = new Thickness(8, 4, 8, 4) };
        Canvas.SetLeft(_hint, 12); Canvas.SetTop(_hint, 12);
        _canvas.Children.Add(_band); _canvas.Children.Add(_hint);

        _overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Owner = target,
            Left = origin.X, Top = origin.Y, Width = target.ActualWidth, Height = target.ActualHeight,
            Content = _canvas,
        };

        _overlay.MouseLeftButtonDown += (_, e) => Begin(e.GetPosition(_canvas));
        _overlay.MouseMove += (_, e) => Move(e.GetPosition(_canvas));
        _overlay.MouseLeftButtonUp += (_, e) => End(e.GetPosition(_canvas));
        _overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        // Closed by other means (Alt+F4, owner closed): counts as cancelled.
        _overlay.Closed += (_, __) => _result.TrySetResult(null);
    }

    public static Task<Rect?> PickAsync(Window target) => Start(target)._result.Task;

    internal static RegionPicker Start(Window target)
    {
        var picker = new RegionPicker(target);
        picker._overlay.Show();
        picker._overlay.Activate();
        picker._overlay.Focus();
        return picker;
    }

    internal Task<Rect?> Result => _result.Task;

    internal void Begin(Point position)
    {
        _start = position;
        _hint.Visibility = Visibility.Collapsed;
        _band.Visibility = Visibility.Visible;
        _canvas.CaptureMouse();
    }

    internal void Move(Point position)
    {
        if (_start == null) return;
        var r = Between(_start.Value, position);
        Canvas.SetLeft(_band, r.X); Canvas.SetTop(_band, r.Y); _band.Width = r.Width; _band.Height = r.Height;
    }

    internal void End(Point position)
    {
        if (_start == null) return;
        var r = Between(_start.Value, position);
        _canvas.ReleaseMouseCapture();
        Finish(r.Width >= 2 && r.Height >= 2 ? r : null);
    }

    private void Finish(Rect? result)
    {
        // The result first: closing raises Closed synchronously, which would otherwise report
        // the selection as cancelled before it is set.
        _result.TrySetResult(result);
        _overlay.Close();
    }

    private static Rect Between(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
