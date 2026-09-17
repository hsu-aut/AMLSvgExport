using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Aml.Editor.Plugin.SvgExport.Capture;

/// <summary>
/// Finds visible text that the editor does not show completely: text blocks trimmed with an
/// ellipsis and text boxes whose content is wider than the box (e.g. long GUID values in the
/// attribute details).
/// </summary>
public static class CutTextDetector
{
    /// <param name="area">Only text inside this area of <paramref name="areaRoot"/> counts (e.g. a dragged region).</param>
    public static List<string> Find(IEnumerable<FrameworkElement> roots, Visual? areaRoot = null, Rect? area = null)
    {
        var result = new List<string>();
        foreach (var root in roots)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (d is UIElement { IsVisible: false }) continue;
                switch (d)
                {
                    case TextBox box when IsCut(box) && InArea(box, areaRoot, area):
                        result.Add(box.Text);
                        continue;   // its inner text view is part of the box
                    case TextBlock block when IsCut(block) && InArea(block, areaRoot, area):
                        result.Add(block.Text);
                        break;
                }
                if (d is not Visual) continue;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
            }
        }
        return result.Distinct().ToList();
    }

    private static bool InArea(FrameworkElement element, Visual? root, Rect? area)
    {
        if (root == null || area == null) return true;
        try
        {
            return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)).IntersectsWith(area.Value);
        }
        catch (InvalidOperationException)
        {
            return false;   // not below the root, e.g. another window
        }
    }

    private static bool IsCut(TextBox box) =>
        !string.IsNullOrEmpty(box.Text) && box.ViewportWidth > 0 && box.ExtentWidth > box.ViewportWidth + 1;

    private static bool IsCut(TextBlock block)
    {
        if (block.TextTrimming == TextTrimming.None || string.IsNullOrEmpty(block.Text) || block.ActualWidth <= 0) return false;
        var dpi = VisualTreeHelper.GetDpi(block).PixelsPerDip;
        var full = new FormattedText(block.Text, CultureInfo.CurrentUICulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black, dpi);
        var available = block.ActualWidth - block.Padding.Left - block.Padding.Right;
        return full.WidthIncludingTrailingWhitespace > available + 0.5;
    }
}
