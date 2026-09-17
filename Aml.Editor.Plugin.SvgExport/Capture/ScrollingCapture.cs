using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Aml.Editor.Plugin.SvgExport.Capture;

/// <summary>
/// Captures the complete content of a scrollable tree, including the parts outside the viewport.
/// <para>
/// The tree's scroll viewer is made as large as its content for the capture, so nothing needs to
/// be scrolled: every row is in view, and the editor realizes all rows and draws every
/// InternalLink as it does for visible rows. (Scrolling page by page failed for links: the editor
/// finds a row only while its container exists, and places a line end that is scrolled above the
/// view at the wrong position.) The enlarged content overlaps the elements below the tree, so
/// only the scroll viewer and the tree's adorner layer are captured from it. The frame parts
/// above and below the tree (header, toolbar, attribute details) are captured in the original
/// layout. Size and scroll position are restored afterwards.
/// </para>
/// </summary>
public static class ScrollingCapture
{
    private const int MaxResizeRounds = 12;

    /// <summary>Returns one SVG document per frame (e.g. tree only, pane content, pane with tabs).</summary>
    public static async Task<List<string>> CaptureAsync(
        Visual captureRoot, ScrollViewer scrollViewer, IReadOnlyList<FrameworkElement> frames,
        SvgCaptureOptions options, StringBuilder log, HostedContentMode hostedMode = HostedContentMode.PreferVector,
        bool cropToContent = false, IProgress<int>? pagesDone = null)
    {
        var writers = frames.Select(_ => new VisualSvgWriter(options)).ToList();
        foreach (var w in writers) w.Begin();

        var originalH = scrollViewer.HorizontalOffset;
        var originalV = scrollViewer.VerticalOffset;
        var originalHeight = scrollViewer.ReadLocalValue(FrameworkElement.HeightProperty);
        var originalWidth = scrollViewer.ReadLocalValue(FrameworkElement.WidthProperty);
        var resized = false;

        try
        {
            await ScrollToAsync(scrollViewer, 0, 0);

            // Original layout: where the content sits in each frame, and what surrounds it.
            var viewport = ViewportRect(scrollViewer, captureRoot);
            var scrollViewerRect = BoundsIn(scrollViewer, captureRoot);
            var frameRects = frames.Select(f => BoundsIn(f, captureRoot)).ToList();
            var background = FindBackground(scrollViewer);

            // Header band: the frame part above the scroll viewer's content.
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frameRects[i];
                var top = new Rect(f.X, f.Y, f.Width, Math.Max(0, viewport.Y - f.Y));
                writers[i].AddArea(captureRoot, top, new Point(0, 0), textOwnArea: top);
            }

            // Everything of the frame below the scroll viewer, e.g. the attribute details.
            var belowBands = frameRects
                .Select(f => new Rect(f.X, scrollViewerRect.Bottom, f.Width, Math.Max(0, f.Bottom - scrollViewerRect.Bottom)))
                .ToList();
            var belowWriters = frames.Select(_ => new VisualSvgWriter(options)).ToList();

            // Enlarge until the viewport holds the whole content (row heights of unrealized rows are
            // estimates, so the extent can change once they are realized).
            var started = DateTime.Now;
            for (int round = 0; round < MaxResizeRounds; round++)
            {
                var missingHeight = scrollViewer.ExtentHeight - scrollViewer.ViewportHeight;
                var missingWidth = scrollViewer.ExtentWidth - scrollViewer.ViewportWidth;
                if (missingHeight <= 0.5 && missingWidth <= 0.5) break;
                if (missingHeight > 0.5) scrollViewer.Height = scrollViewer.ActualHeight + missingHeight;
                if (missingWidth > 0.5) scrollViewer.Width = scrollViewer.ActualWidth + missingWidth;
                resized = true;
                await WaitForLayoutAsync(scrollViewer);
                pagesDone?.Report(round + 1);
            }
            await ScrollToAsync(scrollViewer, 0, 0);

            var content = ViewportRect(scrollViewer, captureRoot);
            var adornerLayer = AdornerLayer.GetAdornerLayer(scrollViewer);
            var included = new List<Visual> { scrollViewer };
            if (adornerLayer != null) included.Add(adornerLayer);

            for (int i = 0; i < frames.Count; i++)
            {
                var f = frameRects[i];
                var place = new Point(viewport.X - f.X, viewport.Y - f.Y);
                writers[i].AddArea(captureRoot, content, place, textOwnArea: content,
                    adorners: AdornerCapture.Unclipped, onlyWithin: included);
            }
            log.AppendLine($"Tree capture: content {content.Width:0}x{content.Height:0}, viewport before {viewport.Width:0}x{viewport.Height:0}, enlarged in {(DateTime.Now - started).TotalMilliseconds:0} ms.");

            // Back to the original size before the parts below the tree are taken.
            RestoreSize(scrollViewer, originalHeight, originalWidth);
            resized = false;
            await WaitForLayoutAsync(scrollViewer);

            var results = new List<string>();
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frameRects[i];
                var contentLeft = viewport.X - f.X;
                var contentTop = viewport.Y - f.Y;
                var contentBottom = contentTop + content.Height;
                var below = belowBands[i];
                if (below.Height > 0.5)
                    writers[i].AddArea(captureRoot, below, new Point(0, contentBottom), textOwnArea: below);

                var width = Math.Max(f.Width, contentLeft + content.Width);
                var height = contentBottom + (below.Height > 0.5 ? below.Height : 0);

                // Fill the strips beside the content (borders and scrollbars in the live view).
                if (background != null)
                {
                    writers[i].AddRect(new Rect(0, contentTop, contentLeft, content.Height), background);
                    writers[i].AddRect(new Rect(contentLeft + content.Width, contentTop, width - contentLeft - content.Width, content.Height), background);
                }
                if (cropToContent) (width, height) = writers[i].CropToContent(width, height);
                var svg = writers[i].End(width, height);
                if (writers[i].HostedContents.Count > 0)
                    svg = await HostedContentResolver.ResolveAsync(svg, writers[i].HostedContents, hostedMode, log);
                results.Add(svg);

                var unsupported = writers[i].Unsupported;
                log.AppendLine($"Frame {frames[i].GetType().Name}: {width:0}x{height:0}, unsupported: {(unsupported.Count == 0 ? "none" : string.Join(", ", unsupported.Select(kv => $"{kv.Key}={kv.Value}")))}");
            }
            return results;
        }
        finally
        {
            if (resized)
            {
                RestoreSize(scrollViewer, originalHeight, originalWidth);
                await WaitForLayoutAsync(scrollViewer);
            }
            await ScrollToAsync(scrollViewer, originalH, originalV);
        }
    }

    private static void RestoreSize(FrameworkElement element, object height, object width)
    {
        Restore(element, FrameworkElement.HeightProperty, height);
        Restore(element, FrameworkElement.WidthProperty, width);
    }

    private static void Restore(DependencyObject target, DependencyProperty property, object original)
    {
        if (original == DependencyProperty.UnsetValue) target.ClearValue(property);
        else target.SetValue(property, original);
    }

    private static Rect BoundsIn(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    private static async Task WaitForLayoutAsync(FrameworkElement element)
    {
        element.UpdateLayout();
        InvalidateAdorners(element);
        await element.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        element.UpdateLayout();
        await element.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Adorners over the tree (the editor's InternalLink lines) redraw only on some changes.
    /// Force them to redraw for the current layout.
    /// </summary>
    private static void InvalidateAdorners(DependencyObject start)
    {
        for (DependencyObject? d = start; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is not UIElement element || AdornerLayer.GetAdornerLayer(element) is not { } layer) continue;
            foreach (var adorner in layer.GetAdorners(element) ?? Array.Empty<Adorner>())
                adorner.InvalidateVisual();
        }
    }

    /// <summary>The scroll viewer whose content is the tree: the one with the largest viewport.</summary>
    public static ScrollViewer? FindTreeScrollViewer(DependencyObject tree)
    {
        ScrollViewer? best = null;
        var stack = new Stack<DependencyObject>();
        stack.Push(tree);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is ScrollViewer sv && sv.IsVisible && (best == null || sv.ViewportHeight * sv.ViewportWidth > best.ViewportHeight * best.ViewportWidth))
                best = sv;
            if (d is not Visual) continue;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return best;
    }

    private static Rect ViewportRect(ScrollViewer sv, Visual root)
    {
        // The ScrollContentPresenter is exactly the visible content area (without scrollbars).
        FrameworkElement area = FindPresenter(sv) ?? (FrameworkElement)sv;
        return area.TransformToAncestor(root).TransformBounds(new Rect(area.RenderSize));
    }

    private static ScrollContentPresenter? FindPresenter(ScrollViewer sv)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(sv);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is ScrollContentPresenter p) return p;
            if (d != sv && d is ScrollViewer) continue;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    private static Brush? FindBackground(DependencyObject start)
    {
        for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
        {
            var brush = d switch
            {
                Control c => c.Background,
                Panel p => p.Background,
                Border b => b.Background,
                _ => null,
            };
            if (brush is SolidColorBrush s && s.Color.A == 255) return brush;
            if (brush != null && brush is not SolidColorBrush) return brush;
        }
        return null;
    }

    private static async Task ScrollToAsync(ScrollViewer sv, double h, double v)
    {
        sv.ScrollToHorizontalOffset(h);
        sv.ScrollToVerticalOffset(v);
        await WaitForLayoutAsync(sv);
    }
}
