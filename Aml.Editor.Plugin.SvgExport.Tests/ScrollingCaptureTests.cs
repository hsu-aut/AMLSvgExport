using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Aml.Editor.Plugin.SvgExport.Capture;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

public class ScrollingCaptureTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>Runs an async UI operation inside a dispatcher loop on the current STA thread.</summary>
    private static T RunOnDispatcher<T>(Func<Task<T>> operation)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var frame = new DispatcherFrame();
        var task = operation();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static (Window Window, TreeView Tree, DockPanel Pane) BuildWindow(int rootCount, int childCount, bool withDetails = false)
    {
        var tree = new TreeView { FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Background = Brushes.White };
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        for (int i = 0; i < rootCount; i++)
        {
            var item = new TreeViewItem { Header = $"Element_{i:000}", IsExpanded = true };
            for (int c = 0; c < childCount; c++)
                item.Items.Add(new TreeViewItem { Header = $"Child_{i:000}_{c} with a rather long name to force horizontal scrolling" });
            tree.Items.Add(item);
        }

        var header = new Border { Height = 24, Background = Brushes.LightSteelBlue, Child = new TextBlock { Text = "InstanceHierarchy" } };
        var pane = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        pane.Children.Add(header);
        if (withDetails)
        {
            // Like the attribute details below the attribute list in the editor.
            var details = new Border { Height = 40, Background = Brushes.WhiteSmoke, Child = new TextBlock { Text = "Attribute details: x of position" } };
            DockPanel.SetDock(details, Dock.Bottom);
            pane.Children.Add(details);
        }
        pane.Children.Add(tree);

        var window = new Window
        {
            Width = 260, Height = 220, Content = pane,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Left = -3000, Top = -3000,
        };
        window.Show();
        window.UpdateLayout();
        return (window, tree, pane);
    }

    /// <summary>Baseline y of a text element in document coordinates (flat output: own translate only).</summary>
    private static double AbsoluteY(XElement text)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tr = text.Attribute("transform")?.Value ?? "translate(0,0)";
        Assert.StartsWith("translate(", tr);
        var offsetY = double.Parse(tr[(tr.IndexOf(',') + 1)..^1], inv);
        return offsetY + double.Parse(text.Attribute("y")!.Value, inv);
    }

    [Fact]
    public void Area_below_the_tree_is_placed_once_below_the_complete_tree() => Sta.Run(() =>
    {
        var (window, tree, pane) = BuildWindow(rootCount: 30, childCount: 0, withDetails: true);
        try
        {
            var sv = ScrollingCapture.FindTreeScrollViewer(tree)!;
            var svgs = RunOnDispatcher(() => ScrollingCapture.CaptureAsync(window, sv, new FrameworkElement[] { tree, pane }, new SvgCaptureOptions(), new StringBuilder()));

            Assert.DoesNotContain("Attribute details", svgs[0]);   // tree only

            var doc = XDocument.Parse(svgs[1]);
            var details = Assert.Single(doc.Descendants(Svg + "text"), t => t.Value.StartsWith("Attribute details"));
            var lastRow = doc.Descendants(Svg + "text").Single(t => t.Value == "Element_029");
            Assert.True(AbsoluteY(details) > AbsoluteY(lastRow), "details must follow the last tree row");

            var height = double.Parse(doc.Root!.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(AbsoluteY(details) < height);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Captures_rows_outside_the_viewport_and_restores_scroll_position() => Sta.Run(() =>
    {
        var (window, tree, pane) = BuildWindow(rootCount: 40, childCount: 2);
        try
        {
            var sv = ScrollingCapture.FindTreeScrollViewer(tree)!;
            Assert.NotNull(sv);
            sv.ScrollToVerticalOffset(90);
            window.UpdateLayout();
            var before = sv.VerticalOffset;
            Assert.True(sv.ExtentHeight > sv.ViewportHeight * 3, "scene must need scrolling");

            var log = new StringBuilder();
            var svgs = RunOnDispatcher(() => ScrollingCapture.CaptureAsync(window, sv, new FrameworkElement[] { tree, pane }, new SvgCaptureOptions(), log));

            Assert.Equal(2, svgs.Count);
            foreach (var svg in svgs)
            {
                var texts = XDocument.Parse(svg).Descendants(Svg + "text").Select(t => t.Value).ToList();
                Assert.Contains("Element_000", texts);
                Assert.Contains("Element_039", texts);
                Assert.Contains(texts, t => t.StartsWith("Child_039_1"));

                // Text reaches across page borders, so it must be painted after every page;
                // otherwise the next page's background hides the overhanging part.
                var ordered = XDocument.Parse(svg).Root!.Descendants().ToList();
                var firstText = ordered.FindIndex(e => e.Name == Svg + "text");
                var lastPageClip = ordered.FindLastIndex(e => e.Attribute("clip-path") != null);
                Assert.True(lastPageClip < firstText, "a clipped page is painted over text");
            }

            // The pane variant keeps the header exactly once and is taller than the window.
            var paneDoc = XDocument.Parse(svgs[1]);
            Assert.Single(paneDoc.Descendants(Svg + "text"), t => t.Value == "InstanceHierarchy");
            Assert.True(double.Parse(paneDoc.Root!.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture) > sv.ExtentHeight);

            Assert.Equal(before, sv.VerticalOffset);
            Assert.Contains("Tree capture:", log.ToString());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Each_row_text_appears_exactly_once_and_rows_are_placed_in_order() => Sta.Run(() =>
    {
        var (window, tree, _) = BuildWindow(rootCount: 30, childCount: 0);
        try
        {
            var sv = ScrollingCapture.FindTreeScrollViewer(tree)!;
            var svgs = RunOnDispatcher(() => ScrollingCapture.CaptureAsync(window, sv, new FrameworkElement[] { tree }, new SvgCaptureOptions(), new StringBuilder()));
            var doc = XDocument.Parse(svgs[0]);

            // Text is never duplicated, also not for rows cut by a page border.
            var elementTexts = doc.Descendants(Svg + "text").Where(t => t.Value.StartsWith("Element_")).ToList();
            Assert.Equal(30, elementTexts.Count);

            var positions = new Dictionary<string, HashSet<double>>();
            foreach (var t in doc.Descendants(Svg + "text").Where(t => t.Value.StartsWith("Element_")))
            {
                var y = Math.Round(AbsoluteY(t), 1);
                if (!positions.TryGetValue(t.Value, out var set)) positions[t.Value] = set = new HashSet<double>();
                set.Add(y);
            }

            Assert.Equal(30, positions.Count);
            Assert.All(positions.Values, set => Assert.Single(set));
            var ordered = Enumerable.Range(0, 30).Select(i => positions[$"Element_{i:000}"].Single()).ToList();
            Assert.Equal(ordered.OrderBy(y => y), ordered);
        }
        finally
        {
            window.Close();
        }
    });
}
