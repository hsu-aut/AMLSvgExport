using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Aml.Editor.Plugin.SvgExport.Capture;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

public class SelectionAndCutTextTests
{
    private static T RunOnDispatcher<T>(Func<Task<T>> operation)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var frame = new DispatcherFrame();
        var task = operation();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static HashSet<string> Fills(string svg) =>
        System.Text.RegularExpressions.Regex.Matches(svg, "fill=\"(#[0-9a-f]{6})\"").Select(m => m.Groups[1].Value).ToHashSet();

    private static Window Show(UIElement content, double width = 300, double height = 200)
    {
        var window = new Window
        {
            Width = width, Height = height, Content = content,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    [Fact]
    public void Selection_is_hidden_for_the_capture_and_restored_afterwards() => Sta.Run(() =>
    {
        var tree = new TreeView();
        // A distinctive bar color, active and inactive, so the bar can be recognized in the SVG.
        var bar = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56));
        tree.Resources[SystemColors.HighlightBrushKey] = bar;
        tree.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = bar;
        foreach (var name in new[] { "Plant", "Station", "Sensor" }) tree.Items.Add(name);
        var window = Show(tree);
        try
        {
            var unselected = Fills(new VisualSvgWriter().Capture(window, new Rect(window.RenderSize)));

            ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(2)).IsSelected = true;
            RunOnDispatcher(async () => { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); return true; });
            window.UpdateLayout();
            Assert.Equal("Sensor", tree.SelectedItem);
            var selected = Fills(new VisualSvgWriter().Capture(window, new Rect(window.RenderSize)));
            var barColors = selected.Except(unselected).ToList();
            Assert.NotEmpty(barColors);   // the selection bar is really drawn (active or inactive color)

            var hider = RunOnDispatcher(() => SelectionHider.HideAsync(new[] { window }));
            Assert.Equal(1, hider.Count);
            Assert.Null(tree.SelectedItem);

            var hidden = Fills(new VisualSvgWriter().Capture(window, new Rect(window.RenderSize)));
            Assert.Empty(barColors.Intersect(hidden));

            RunOnDispatcher(async () => { await hider.RestoreAsync(); return true; });
            Assert.Equal("Sensor", tree.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Without_selection_nothing_is_hidden() => Sta.Run(() =>
    {
        var tree = new TreeView();
        tree.Items.Add("IH");
        var window = Show(tree);
        try
        {
            var hider = RunOnDispatcher(() => SelectionHider.HideAsync(new[] { window }));
            Assert.Equal(0, hider.Count);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Cut_off_values_are_found_but_complete_ones_are_not() => Sta.Run(() =>
    {
        const string guid = "0f9d3c6a-7b1e-4c2d-9a8f-5e6b7c8d9e0f";
        var panel = new StackPanel();
        panel.Children.Add(new TextBox { Text = guid, Width = 90 });                                           // cut: box too narrow
        panel.Children.Add(new TextBox { Text = "605", Width = 90 });                                          // complete
        panel.Children.Add(new TextBlock { Text = "Description of Conveyor_Station_1", Width = 80, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = "x", Width = 80, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = "long text without trimming is clipped, not ellipsized", Width = 40 });
        var window = Show(panel);
        try
        {
            var cut = CutTextDetector.Find(new FrameworkElement[] { window });
            Assert.Contains(guid, cut);
            Assert.Contains("Description of Conveyor_Station_1", cut);
            Assert.DoesNotContain("605", cut);
            Assert.DoesNotContain("x", cut);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Cut_off_values_outside_the_region_do_not_count() => Sta.Run(() =>
    {
        const string guid = "0f9d3c6a-7b1e-4c2d-9a8f-5e6b7c8d9e0f";
        var panel = new StackPanel();
        panel.Children.Add(new Border { Height = 100 });
        panel.Children.Add(new TextBox { Text = guid, Width = 90 });
        var window = Show(panel);
        try
        {
            Assert.Empty(CutTextDetector.Find(new FrameworkElement[] { window }, window, new Rect(0, 0, 300, 50)));
            Assert.Single(CutTextDetector.Find(new FrameworkElement[] { window }, window, new Rect(0, 90, 300, 60)));
        }
        finally
        {
            window.Close();
        }
    });
}
