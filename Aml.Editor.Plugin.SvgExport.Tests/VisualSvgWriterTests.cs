using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using Aml.Editor.Plugin.SvgExport.Capture;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

public class VisualSvgWriterTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>A small panel resembling an editor tree row: gradient bar, icon path, bold + normal text.</summary>
    private static (Canvas Root, FrameworkElement Target, FrameworkElement Outside) BuildScene()
    {
        var root = new Canvas { Width = 400, Height = 200, Background = Brushes.White };

        var panel = new StackPanel { Width = 300 };
        panel.Children.Add(new Border
        {
            Height = 20,
            Background = new LinearGradientBrush(Colors.White, Color.FromRgb(0xcc, 0xe0, 0xf5), 90),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L10,0 L10,10 L0,10 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x1e, 0x70, 0xc1)),
            Width = 10, Height = 10,
        });
        var text = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Margin = new Thickness(4, 0, 0, 0) };
        text.Inlines.Add(new Run("Cylinder {"));
        text.Inlines.Add(new Bold(new Run("Class:")));
        text.Inlines.Add(new Run(" ExampleSystemUnitClass}"));
        row.Children.Add(text);
        panel.Children.Add(row);
        Canvas.SetLeft(panel, 20); Canvas.SetTop(panel, 30);
        root.Children.Add(panel);

        var outside = new Border { Width = 30, Height = 30, Background = Brushes.Red };
        Canvas.SetLeft(outside, 360); Canvas.SetTop(outside, 160);
        root.Children.Add(outside);

        root.Measure(new Size(400, 200));
        root.Arrange(new Rect(0, 0, 400, 200));
        root.UpdateLayout();
        return (root, panel, outside);
    }

    [Fact]
    public void Captures_text_icon_and_gradient_of_the_target_area() => Sta.Run(() =>
    {
        var (root, target, _) = BuildScene();
        var svg = new VisualSvgWriter().Capture(root, target);
        var doc = XDocument.Parse(svg);

        var rootEl = doc.Root!;
        Assert.Equal("300", rootEl.Attribute("width")!.Value);

        var texts = doc.Descendants(Svg + "text").ToList();
        Assert.Contains(texts, t => t.Value.Contains("Cylinder"));
        Assert.Contains(texts, t => t.Value == "Class:" && t.Attribute("font-weight")?.Value == "700");
        Assert.All(texts, t => Assert.Equal("Segoe UI", t.Attribute("font-family")!.Value));

        Assert.Contains(doc.Descendants(Svg + "path"), p => p.Attribute("fill")?.Value == "#1e70c1");
        Assert.Single(doc.Descendants(Svg + "linearGradient"));
    });

    [Fact]
    public void Visuals_outside_the_target_area_are_pruned() => Sta.Run(() =>
    {
        var (root, target, _) = BuildScene();
        var svg = new VisualSvgWriter().Capture(root, target);
        Assert.DoesNotContain("#ff0000", svg);
    });

    [Fact]
    public void Text_as_paths_emits_no_text_elements() => Sta.Run(() =>
    {
        var (root, target, _) = BuildScene();
        var svg = new VisualSvgWriter(new SvgCaptureOptions { TextAsPaths = true }).Capture(root, target);
        var doc = XDocument.Parse(svg);
        Assert.Empty(doc.Descendants(Svg + "text"));
        Assert.True(doc.Descendants(Svg + "path").Count() > 3);
    });

    [Fact]
    public void Text_positions_are_absolute_in_target_coordinates() => Sta.Run(() =>
    {
        var (root, target, _) = BuildScene();
        var doc = XDocument.Parse(new VisualSvgWriter().Capture(root, target));
        // Row starts below the 20px gradient bar; text follows the 10px icon and a 4px margin.
        var text = doc.Descendants(Svg + "text").First(t => t.Value.Contains("Cylinder"));
        Assert.Equal("translate(14,20)", text.Attribute("transform")!.Value);
    });

    [Fact]
    public void Output_is_flat_for_editing() => Sta.Run(() =>
    {
        var (root, target, _) = BuildScene();
        var doc = XDocument.Parse(new VisualSvgWriter().Capture(root, target));
        var shapes = doc.Descendants().Where(e => e.Name == Svg + "path" || e.Name == Svg + "text" || e.Name == Svg + "image")
                        .Where(e => e.Parent?.Name != Svg + "clipPath").ToList();
        Assert.NotEmpty(shapes);
        // Every shape is top-level or directly inside a single clip wrapper; no transform groups.
        Assert.All(shapes, e =>
        {
            var parent = e.Parent!;
            if (parent == doc.Root) return;
            Assert.Equal(Svg + "g", parent.Name);
            Assert.NotNull(parent.Attribute("clip-path"));
            Assert.Null(parent.Attribute("transform"));
            Assert.Equal(doc.Root, parent.Parent);
        });
    });
}
