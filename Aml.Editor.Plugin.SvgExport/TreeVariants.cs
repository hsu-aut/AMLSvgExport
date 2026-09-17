using System.Windows;

namespace Aml.Editor.Plugin.SvgExport;

/// <summary>Maps the chosen <see cref="TreeVariant"/> to the frames of a tree panel that are captured.</summary>
public static class TreeVariants
{
    /// <returns>Frames with the file name suffix each is written with ("" for a single file).</returns>
    public static List<(FrameworkElement Frame, string Suffix)> Frames(
        FrameworkElement tree, FrameworkElement? content, FrameworkElement? paneWithTabs, TreeVariant variant, out string? note)
    {
        note = null;
        switch (variant)
        {
            case TreeVariant.All:
                var all = new List<(FrameworkElement, string)> { (tree, "_tree") };
                if (content != null) all.Add((content, "_content"));
                if (paneWithTabs != null) all.Add((paneWithTabs, "_pane"));
                return all;

            case TreeVariant.PaneWithTabs when paneWithTabs != null:
                return new() { (paneWithTabs, "") };
            case TreeVariant.PaneWithTabs when content != null:
                note = "This panel has no tab strip; exported the panel content instead.";
                return new() { (content, "") };

            case TreeVariant.PaneContent when content != null:
                return new() { (content, "") };

            case TreeVariant.TreeOnly:
                return new() { (tree, "") };

            default:
                note = "No panel found around the tree; exported the tree only.";
                return new() { (tree, "") };
        }
    }

    public static string Label(TreeVariant v) => v switch
    {
        TreeVariant.PaneWithTabs => "Panel with tab strip",
        TreeVariant.PaneContent => "Panel with toolbar",
        TreeVariant.TreeOnly => "Tree only",
        _ => "All three (+ diagnostics)",
    };
}
