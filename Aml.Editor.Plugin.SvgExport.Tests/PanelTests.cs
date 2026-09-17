using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aml.Editor.Plugin.SvgExport.Ui;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

public class PanelTests
{
    [Fact]
    public void Settings_survive_a_save_and_load_roundtrip()
    {
        var file = Path.Combine(Path.GetTempPath(), "svgexport_settings_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new PluginSettings { AlsoPdf = true, CropToContent = true, SuppressHover = false, TreeVariant = TreeVariant.All, LastFolder = @"C:\Figures" }.Save(file);
            var loaded = PluginSettings.Load(file);

            Assert.True(loaded.AlsoPdf);
            Assert.True(loaded.CropToContent);
            Assert.False(loaded.SuppressHover);
            Assert.Equal(TreeVariant.All, loaded.TreeVariant);
            Assert.Equal(@"C:\Figures", loaded.LastFolder);
            Assert.Contains("\"All\"", File.ReadAllText(file));   // readable enum names
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Missing_or_corrupt_settings_give_the_defaults()
    {
        var file = Path.Combine(Path.GetTempPath(), "svgexport_settings_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.True(PluginSettings.Load(file).SuppressHover);
            File.WriteAllText(file, "{ not json");
            var loaded = PluginSettings.Load(file);
            Assert.True(loaded.SuppressHover);
            Assert.True(loaded.DiagramsAsVector);
            Assert.Equal(TreeVariant.PaneContent, loaded.TreeVariant);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void All_icons_are_valid_geometry_inside_the_24_grid() => Sta.Run(() =>
    {
        foreach (var (name, data) in Icons.All)
        {
            var bounds = Geometry.Parse(data).Bounds;
            Assert.False(bounds.IsEmpty, name);
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24, $"{name} leaves the 24x24 grid: {bounds}");
        }
    });

    [Fact]
    public void Icons_take_the_foreground_of_the_surrounding_control() => Sta.Run(() =>
    {
        var icon = Icons.Create(Icons.Tree);
        var button = new Button { Content = icon, Foreground = Brushes.OrangeRed };
        button.Measure(new Size(100, 100)); button.Arrange(new Rect(0, 0, 100, 100)); button.UpdateLayout();

        var path = FindPath(icon);
        Assert.Same(Brushes.OrangeRed, path.Stroke);
    });

    private static System.Windows.Shapes.Path FindPath(DependencyObject root)
    {
        if (root is System.Windows.Shapes.Path p) return p;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindPath(child) is { } found) return found;
        return null!;
    }

    [Fact]
    public void Tree_variants_fall_back_to_the_parts_that_exist() => Sta.Run(() =>
    {
        var tree = new Border(); var content = new Border(); var pane = new Border();

        var all = TreeVariants.Frames(tree, content, pane, TreeVariant.All, out var noteAll);
        Assert.Equal(new[] { "_tree", "_content", "_pane" }, all.Select(f => f.Suffix));
        Assert.Null(noteAll);

        var single = Assert.Single(TreeVariants.Frames(tree, content, pane, TreeVariant.PaneWithTabs, out _));
        Assert.Same(pane, single.Frame);
        Assert.Equal("", single.Suffix);

        var noTabs = Assert.Single(TreeVariants.Frames(tree, content, null, TreeVariant.PaneWithTabs, out var noteTabs));
        Assert.Same(content, noTabs.Frame);
        Assert.NotNull(noteTabs);

        var noPanel = Assert.Single(TreeVariants.Frames(tree, null, null, TreeVariant.PaneContent, out var notePanel));
        Assert.Same(tree, noPanel.Frame);
        Assert.NotNull(notePanel);
    });
}
