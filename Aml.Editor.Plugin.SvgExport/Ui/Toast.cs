using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Aml.Editor.Plugin.SvgExport.Ui;

/// <summary>
/// Small notification in the bottom-right corner of the editor, for exports started by
/// shortcut while the plugin panel is not visible. Does not take focus, closes by itself.
/// </summary>
public static class Toast
{
    private static Window? _current;

    public static void Show(Window owner, string title, string? detail, string? fileToShow, bool isError)
    {
        _current?.Close();

        var foreground = owner.Foreground ?? SystemColors.ControlTextBrush;
        var background = owner.Background is SolidColorBrush { Color.A: 255 } b ? b : SystemColors.WindowBrush;

        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), MaxWidth = 360 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
            text.Children.Add(new TextBlock { Text = detail, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (fileToShow != null)
        {
            var link = new Hyperlink(new Run("Show in folder"));
            link.Click += (_, __) => ShowInFolder(fileToShow);
            text.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 4, 0, 0) });
        }

        var content = new ContentControl
        {
            Foreground = foreground,
            Content = new Border
            {
                Background = background,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10, 14, 10),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { Icons.Create(isError ? Icons.Warning : Icons.Check, 20), text },
                },
            },
        };

        var toast = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = false,
            SizeToContent = SizeToContent.WidthAndHeight, Owner = owner, Content = content,
            FontFamily = owner.FontFamily, FontSize = owner.FontSize,
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 10 : 6) };
        timer.Tick += (_, __) => { timer.Stop(); toast.Close(); };
        toast.MouseEnter += (_, __) => timer.Stop();
        toast.MouseLeave += (_, __) => timer.Start();
        toast.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Run) toast.Close(); };
        toast.Closed += (_, __) => { timer.Stop(); if (ReferenceEquals(_current, toast)) _current = null; };

        // Start outside the screen; Loaded moves it once its size is known, so it never flashes elsewhere.
        toast.WindowStartupLocation = WindowStartupLocation.Manual;
        toast.Left = -32000;
        toast.Top = -32000;
        toast.Loaded += (_, __) =>
        {
            // Bottom-right of the owner, above the status bar. PointToScreen gives device pixels.
            try
            {
                if (PresentationSource.FromVisual(owner)?.CompositionTarget is { } target)
                {
                    var corner = target.TransformFromDevice.Transform(owner.PointToScreen(new Point(owner.ActualWidth, owner.ActualHeight)));
                    toast.Left = corner.X - toast.ActualWidth - 24;
                    toast.Top = corner.Y - toast.ActualHeight - 56;
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Owner not connected to a presentation source: fall through.
            }
            var area = SystemParameters.WorkArea;
            toast.Left = area.Right - toast.ActualWidth - 24;
            toast.Top = area.Bottom - toast.ActualHeight - 24;
        };

        _current = toast;
        toast.Show();
        timer.Start();
    }

    public static void ShowInFolder(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch { /* nothing sensible to do */ }
    }

    public static void Open(string file)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch { /* no associated program */ }
    }
}
