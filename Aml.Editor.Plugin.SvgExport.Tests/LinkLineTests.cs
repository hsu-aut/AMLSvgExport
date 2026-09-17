using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Aml.Editor.Plugin.SvgExport.Capture;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

/// <summary>
/// The AML Editor draws InternalLinks with an adorner over the tree. As observed, it only knows
/// row positions of realized (not virtualized) rows: with one end realized the line runs to the
/// tree edge, with neither end realized no line is drawn at all. These tests reproduce that
/// behavior.
/// </summary>
public class LinkLineTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private const string LinkColor = "#ff0000";

    public sealed class Node
    {
        public string Name { get; set; } = "";
        public List<Node> Children { get; } = new();
    }

    /// <summary>Mimics the editor's link adorner for one link between two rows.</summary>
    private sealed class EditorLikeLinkAdorner : Adorner
    {
        private readonly TreeView _tree;
        private readonly Func<TreeView, TreeViewItem?> _from, _to;

        public EditorLikeLinkAdorner(TreeView tree, int from, int to)
            : this(tree, t => t.ItemContainerGenerator.ContainerFromIndex(from) as TreeViewItem, t => t.ItemContainerGenerator.ContainerFromIndex(to) as TreeViewItem) { }

        public EditorLikeLinkAdorner(TreeView tree, Func<TreeView, TreeViewItem?> from, Func<TreeView, TreeViewItem?> to) : base(tree)
        {
            _tree = tree; _from = from; _to = to;
            IsHitTestVisible = false;
            // Like the editor: redraw on vertical scrolling only.
            tree.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
            {
                if (e.VerticalChange != 0) InvalidateVisual();
            }));
        }

        protected override void OnRender(DrawingContext dc)
        {
            var a = _from(_tree);
            var b = _to(_tree);
            if (a == null && b == null) return;   // editor: no line at all

            // Editor: the drawing is clipped to the viewport of the tree.
            var viewer = ScrollingCapture.FindTreeScrollViewer(_tree);
            if (viewer != null) dc.PushClip(new RectangleGeometry(new Rect(0, 0, _tree.ActualWidth, viewer.ViewportHeight)));

            const double x = 200;
            double Y(TreeViewItem? item, bool isStart) => item != null
                ? item.TransformToAncestor(_tree).Transform(new Point(0, 9)).Y
                : isStart ? -10 : _tree.ActualHeight;   // editor: to the tree edge

            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0, 0)), 1);
            var top = Y(a, true);
            var bottom = Y(b, false);
            if (top < 0 && a != null)
            {
                // Editor, observed in an export: with the start row scrolled above the viewport the
                // start is placed at the left edge near the top, which draws a stray horizontal line
                // and leaves the part above it empty.
                dc.DrawLine(pen, new Point(0, 5), new Point(x, 5));
                top = 5;
            }
            dc.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            if (viewer != null) dc.Pop();
        }
    }

    private static T RunOnDispatcher<T>(Func<Task<T>> operation)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var frame = new DispatcherFrame();
        var task = operation();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>The editor never draws a link from the left edge; such a line comes from a scrolled page.</summary>
    private static void AssertNoStrayHorizontalLines(string svg)
    {
        var inv = CultureInfo.InvariantCulture;
        var doc = XDocument.Parse(svg);
        foreach (var path in doc.Descendants(Svg + "path").Where(p => p.Attribute("stroke")?.Value == LinkColor))
        {
            var t = Regex.Match(path.Attribute("transform")?.Value ?? "", @"translate\((-?[\d.]+),(-?[\d.]+)\)");
            var dx = t.Success ? double.Parse(t.Groups[1].Value, inv) : 0;
            var xs = Regex.Matches(path.Attribute("d")!.Value, @"(-?[\d.]+),(-?[\d.]+)").Select(m => double.Parse(m.Groups[1].Value, inv) + dx).ToList();
            Assert.True(xs.Min() > 100, $"stray link segment from x={xs.Min():0}: {path}");
        }
    }

    /// <summary>Vertical coverage of the link color in document coordinates, merged into intervals.</summary>
    private static List<(double From, double To)> LinkCoverage(string svg, double lineX)
    {
        var inv = CultureInfo.InvariantCulture;
        var doc = XDocument.Parse(svg);
        var clips = doc.Descendants(Svg + "clipPath").ToDictionary(
            c => c.Attribute("id")!.Value,
            c => Bounds(c.Element(Svg + "path")!.Attribute("d")!.Value));

        var segments = new List<(double, double)>();
        foreach (var path in doc.Descendants(Svg + "path").Where(p => p.Attribute("stroke")?.Value == LinkColor))
        {
            var (dx, dy) = Translate(path.Attribute("transform")?.Value);
            var pts = Regex.Matches(path.Attribute("d")!.Value, @"(-?[\d.]+),(-?[\d.]+)")
                .Select(m => (X: double.Parse(m.Groups[1].Value, inv) + dx, Y: double.Parse(m.Groups[2].Value, inv) + dy)).ToList();
            if (pts.Count < 2 || Math.Abs(pts[0].X - lineX) > 30) continue;
            double from = pts.Min(p => p.Y), to = pts.Max(p => p.Y);

            // Apply the clip of the wrapper group, if any.
            var clipRef = path.Parent?.Attribute("clip-path")?.Value;
            if (clipRef != null)
            {
                var r = clips[clipRef[5..^1]];
                from = Math.Max(from, r.Top); to = Math.Min(to, r.Bottom);
            }
            if (to > from) segments.Add((from, to));
        }

        var merged = new List<(double From, double To)>();
        foreach (var s in segments.OrderBy(s => s.Item1))
        {
            if (merged.Count > 0 && s.Item1 <= merged[^1].To + 1) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, s.Item2));
            else merged.Add(s);
        }
        return merged;

        static (double, double) Translate(string? t)
        {
            if (t == null) return (0, 0);
            var m = Regex.Match(t, @"translate\((-?[\d.]+),(-?[\d.]+)\)");
            return m.Success ? (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : (0, 0);
        }

        static Rect Bounds(string d)
        {
            var nums = Regex.Matches(d, @"(-?[\d.]+),(-?[\d.]+)").Select(m => new Point(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
            return new Rect(new Point(nums.Min(p => p.X), nums.Min(p => p.Y)), new Point(nums.Max(p => p.X), nums.Max(p => p.Y)));
        }
    }

    [Fact]
    public void Link_with_both_ends_out_of_view_is_exported_when_nested_rows_keep_virtualizing() => Sta.Run(() =>
    {
        // As in the editor: the nested items panels virtualize by their own style, so switching
        // virtualization off on the tree does not realize the rows; and neither end of the link is
        // in view before the export.
        var tree = new TreeView { FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Background = Brushes.White };
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetCacheLength(tree, new VirtualizationCacheLength(0));
        var template = new HierarchicalDataTemplate(typeof(Node)) { ItemsSource = new System.Windows.Data.Binding(nameof(Node.Children)) };
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Node.Name)));
        template.VisualTree = text;
        tree.ItemTemplate = template;
        tree.ItemContainerStyle = new Style(typeof(TreeViewItem))
        {
            Setters =
            {
                new Setter(TreeViewItem.IsExpandedProperty, true),
                new Setter(VirtualizingPanel.IsVirtualizingProperty, true),
                new Setter(VirtualizingPanel.CacheLengthProperty, new VirtualizationCacheLength(0)),
            },
        };
        for (int g = 0; g < 3; g++)
        {
            var group = new Node { Name = $"Group_{g}" };
            for (int i = 0; i < 40; i++) group.Children.Add(new Node { Name = $"Row_{g}_{i:00}" });
            tree.Items.Add(group);
        }

        var window = new Window
        {
            Width = 300, Height = 160, Content = tree,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000,
        };
        window.Show();
        window.UpdateLayout();
        try
        {
            static TreeViewItem? Child(TreeView t, int group, int index) =>
                (t.ItemContainerGenerator.ContainerFromIndex(group) as TreeViewItem)?.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;

            Assert.Null(Child(tree, 1, 5));   // precondition: not realized before the export
            AdornerLayer.GetAdornerLayer(tree)!.Add(new EditorLikeLinkAdorner(tree, t => Child(t, 1, 5), t => Child(t, 2, 30)));
            window.UpdateLayout();

            var sv = ScrollingCapture.FindTreeScrollViewer(tree)!;
            var log = new StringBuilder();
            var svg = RunOnDispatcher(() => ScrollingCapture.CaptureAsync(window, sv, new FrameworkElement[] { tree }, new SvgCaptureOptions(), log))[0];

            var doc = XDocument.Parse(svg);
            double RowY(string name) => doc.Descendants(Svg + "text").Where(t => t.Value == name)
                .Select(t => double.Parse(Regex.Match(t.Attribute("transform")!.Value, @",(-?[\d.]+)\)").Groups[1].Value, CultureInfo.InvariantCulture)).Single();
            var top = RowY("Row_1_05"); var bottom = RowY("Row_2_30");

            var coverage = LinkCoverage(svg, 200);
            Assert.True(coverage.Any(c => c.From <= top + 12 && c.To >= bottom),
                $"link line missing or with gaps between {top:0} and {bottom:0}: {string.Join(" ", coverage.Select(c => $"[{c.From:0}-{c.To:0}]"))}\n{log}");
            AssertNoStrayHorizontalLines(svg);

            // The tree has its size and scroll position back.
            Assert.Equal(DependencyProperty.UnsetValue, sv.ReadLocalValue(FrameworkElement.HeightProperty));
            Assert.True(sv.ViewportHeight < sv.ExtentHeight);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Link_between_nested_rows_spanning_several_pages_is_exported_without_gap() => Sta.Run(() =>
    {
        // Hierarchical like the editor: rows are children of expanded parents, each parent has its
        // own nested items panel and container generator.
        var tree = new TreeView { FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Background = Brushes.White };
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);
        VirtualizingPanel.SetCacheLength(tree, new VirtualizationCacheLength(0));
        var template = new HierarchicalDataTemplate(typeof(Node)) { ItemsSource = new System.Windows.Data.Binding(nameof(Node.Children)) };
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Node.Name)));
        template.VisualTree = text;
        tree.ItemTemplate = template;
        tree.ItemContainerStyle = new Style(typeof(TreeViewItem)) { Setters = { new Setter(TreeViewItem.IsExpandedProperty, true) } };

        for (int g = 0; g < 3; g++)
        {
            var group = new Node { Name = $"Group_{g}" };
            for (int i = 0; i < 40; i++) group.Children.Add(new Node { Name = $"Row_{g}_{i:00}" });
            tree.Items.Add(group);
        }

        var window = new Window
        {
            Width = 300, Height = 160, Content = tree,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000,
        };
        window.Show();
        window.UpdateLayout();
        try
        {
            static TreeViewItem? Child(TreeView t, int group, int index) =>
                (t.ItemContainerGenerator.ContainerFromIndex(group) as TreeViewItem)?.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;

            AdornerLayer.GetAdornerLayer(tree)!.Add(new EditorLikeLinkAdorner(tree, t => Child(t, 0, 2), t => Child(t, 2, 30)));
            window.UpdateLayout();

            var sv = ScrollingCapture.FindTreeScrollViewer(tree)!;
            var log = new StringBuilder();
            var svg = RunOnDispatcher(() => ScrollingCapture.CaptureAsync(window, sv, new FrameworkElement[] { tree }, new SvgCaptureOptions(), log))[0];

            var doc = XDocument.Parse(svg);
            double RowY(string name) => doc.Descendants(Svg + "text").Where(t => t.Value == name)
                .Select(t => double.Parse(Regex.Match(t.Attribute("transform")!.Value, @",(-?[\d.]+)\)").Groups[1].Value, CultureInfo.InvariantCulture)).Single();
            var top = RowY("Row_0_02"); var bottom = RowY("Row_2_30");
            Assert.True(bottom - top > sv.ViewportHeight * 3, "the link must span several pages");

            var coverage = LinkCoverage(svg, 200);
            Assert.True(coverage.Any(c => c.From <= top + 12 && c.To >= bottom),
                $"link line has gaps between {top:0} and {bottom:0}: {string.Join(" ", coverage.Select(c => $"[{c.From:0}-{c.To:0}]"))}\n{log}");
            AssertNoStrayHorizontalLines(svg);

            // Virtualization is back on afterwards.
            Assert.True(VirtualizingPanel.GetIsVirtualizing(tree));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Link_spanning_several_pages_is_exported_without_gap() => Sta.Run(() =>
    {
        var tree = new TreeView { FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Background = Brushes.White };
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);
        VirtualizingPanel.SetCacheLength(tree, new VirtualizationCacheLength(0));
        // Data items like the editor's view models: containers exist only while realized.
        // (TreeViewItem objects as items would be their own containers and never virtualized.)
        for (int i = 0; i < 120; i++) tree.Items.Add($"Row_{i:000}");

        var window = new Window
        {
            Width = 300, Height = 160, Content = tree,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000,
        };
        window.Show();
        window.UpdateLayout();
        try
        {
            var from = 3; var to = 100;
            AdornerLayer.GetAdornerLayer(tree)!.Add(new EditorLikeLinkAdorner(tree, from, to));
            window.UpdateLayout();

            var sv = ScrollingCapture.FindTreeScrollViewer(tree)!;
            var log = new StringBuilder();
            var svg = RunOnDispatcher(() => ScrollingCapture.CaptureAsync(window, sv, new FrameworkElement[] { tree }, new SvgCaptureOptions(), log))[0];

            var doc = XDocument.Parse(svg);
            double RowY(int index) => doc.Descendants(Svg + "text").Where(t => t.Value == $"Row_{index:000}")
                .Select(t => double.Parse(Regex.Match(t.Attribute("transform")!.Value, @",(-?[\d.]+)\)").Groups[1].Value, CultureInfo.InvariantCulture)).Single();
            var top = RowY(from); var bottom = RowY(to);
            Assert.True(bottom - top > sv.ViewportHeight * 3, "the link must span several pages");

            var coverage = LinkCoverage(svg, 200);
            Assert.True(coverage.Any(c => c.From <= top + 12 && c.To >= bottom),
                $"link line has gaps between {top:0} and {bottom:0}: {string.Join(" ", coverage.Select(c => $"[{c.From:0}-{c.To:0}]"))}\n{log}");
            AssertNoStrayHorizontalLines(svg);
        }
        finally
        {
            window.Close();
        }
    });
}
