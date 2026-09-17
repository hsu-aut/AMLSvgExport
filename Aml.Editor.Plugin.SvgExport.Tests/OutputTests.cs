using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using Aml.Editor.Plugin.SvgExport.Capture;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

public class OutputTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>A panel with a short list and a lot of empty background below and to the right.</summary>
    private static Grid BuildPanel()
    {
        var root = new Grid { Width = 600, Height = 500, Background = Brushes.LightGray };
        var list = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10) };
        for (int i = 0; i < 3; i++)
            list.Children.Add(new TextBlock { Text = $"Attribute_{i}", FontFamily = new FontFamily("Segoe UI"), FontSize = 13 });
        root.Children.Add(list);
        root.Measure(new Size(600, 500)); root.Arrange(new Rect(0, 0, 600, 500)); root.UpdateLayout();
        return root;
    }

    [Fact]
    public void Crop_to_content_removes_empty_space_right_and_below_but_keeps_the_origin() => Sta.Run(() =>
    {
        var root = BuildPanel();
        var writer = new VisualSvgWriter();
        writer.Begin();
        writer.AddArea(root, new Rect(0, 0, 600, 500), new Point(0, 0));
        var (w, h) = writer.CropToContent(600, 500);

        Assert.True(w < 150, $"width {w}");
        Assert.True(h < 100, $"height {h}");
        Assert.True(w > 60 && h > 40, "content itself must remain");

        var doc = XDocument.Parse(writer.End(w, h));
        Assert.Equal(3, doc.Descendants(Svg + "text").Count());
        Assert.StartsWith("0 0 ", doc.Root!.Attribute("viewBox")!.Value);
    });

    [Fact]
    public void Crop_ignores_the_background_even_when_nothing_else_is_large() => Sta.Run(() =>
    {
        var root = new Grid { Width = 300, Height = 300, Background = Brushes.White };
        root.Measure(new Size(300, 300)); root.Arrange(new Rect(0, 0, 300, 300)); root.UpdateLayout();
        var writer = new VisualSvgWriter();
        writer.Begin();
        writer.AddArea(root, new Rect(0, 0, 300, 300), new Point(0, 0));
        Assert.True(writer.ContentBounds.IsEmpty);
        Assert.Equal((300d, 300d), writer.CropToContent(300, 300));
    });

    [Fact]
    public void Hover_suppression_clears_mouse_over_and_restores_hit_testing() => Sta.Run(() =>
    {
        var button = new Button { Content = "x", Width = 80, Height = 30 };
        var window = new Window { Width = 200, Height = 120, Content = button, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000 };
        window.Show();
        try
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var task = HoverSuppression.BeginAsync(window);
            var frame = new System.Windows.Threading.DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            using (task.Result)
            {
                Assert.False(window.IsHitTestVisible);
                Assert.False(button.IsMouseOver);
            }
            Assert.True(window.IsHitTestVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Edge_renders_svg_to_pdf_and_png_at_the_svg_size()
    {
        if (!EdgeRenderer.IsAvailable) return;   // machine without Edge: nothing to verify
        // Paths with spaces and non-ASCII characters, as in synced document folders.
        var dir = Path.Combine(Path.GetTempPath(), "svgexport test Über " + Guid.NewGuid().ToString("N"), "Documents");
        Directory.CreateDirectory(dir);
        try
        {
            var svg = Path.Combine(dir, "a.svg");
            File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\"><rect width=\"200\" height=\"100\" fill=\"#fff\"/><circle cx=\"50\" cy=\"50\" r=\"20\" fill=\"#1e70c1\"/></svg>");

            Assert.True(EdgeRenderer.ToPdfAsync(svg, Path.Combine(dir, "a.pdf"), 200, 100).Result);
            Assert.True(EdgeRenderer.ToPngAsync(svg, Path.Combine(dir, "a.png"), 200, 100).Result);

            var pdf = File.ReadAllBytes(Path.Combine(dir, "a.pdf"));
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));

            var png = new System.Windows.Media.Imaging.PngBitmapDecoder(new MemoryStream(File.ReadAllBytes(Path.Combine(dir, "a.png"))),
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.Equal(400, png.Frames[0].PixelWidth);
            Assert.Equal(200, png.Frames[0].PixelHeight);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Shortcuts_are_distinct()
    {
        var gestures = new[] { EditorTreeLocator.ExportGesture, EditorTreeLocator.ExportWindowGesture, EditorTreeLocator.ExportRegionGesture, EditorTreeLocator.CopyTreeGesture };
        Assert.Equal(4, gestures.Select(g => (g.Key, g.Modifiers)).Distinct().Count());
        Assert.All(gestures, g => Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, g.Modifiers));

        // Taken by the AML Editor 6.4 (main window and tree view): Copy XML markup, Open AML container, paste.
        var editorShortcuts = new[] { Key.C, Key.O, Key.V };
        Assert.All(gestures, g => Assert.DoesNotContain(g.Key, editorShortcuts));
    }
}
