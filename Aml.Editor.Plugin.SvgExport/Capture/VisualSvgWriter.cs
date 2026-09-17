using System.IO;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Aml.Editor.Plugin.SvgExport.Capture.SvgPathData;

namespace Aml.Editor.Plugin.SvgExport.Capture;

public sealed class SvgCaptureOptions
{
    /// <summary>Emit text as glyph outlines instead of &lt;text&gt; elements.</summary>
    public bool TextAsPaths { get; set; }
}

/// <summary>
/// A natively rendered area (HwndHost, e.g. a WebView2 of another plugin) inside the capture.
/// WPF has no drawing for it; the writer leaves a placeholder that <see cref="HostedContentResolver"/>
/// fills with content obtained from the host itself.
/// </summary>
public sealed class HostedContent
{
    public required int Id { get; init; }
    public required Visual Host { get; init; }
    /// <summary>Area of the host in output document coordinates.</summary>
    public required Rect OutputRect { get; init; }
    public string Placeholder => $"<!--hosted:{Id}-->";
}

/// <summary>
/// Vectorizes what WPF actually renders. Starting from a capture root (usually the
/// window, so adorners such as the InternalLink lines are included), every visual whose
/// bounds intersect the target rectangle is translated into SVG: its render content
/// (VisualTreeHelper.GetDrawing) with offset, transform, clip and opacity applied.
/// <para>
/// The output is flat so it can be edited, e.g. in Inkscape: every path, text and image is a
/// top-level element with an absolute transform. A clip wrapper group is only added where an
/// element actually reaches beyond its clip.
/// </para>
/// </summary>
/// <summary>How adorners (drawn over elements, e.g. the editor's InternalLink lines) are captured.</summary>
public enum AdornerCapture
{
    /// <summary>As rendered, with their own clips.</summary>
    AsRendered,
    /// <summary>Not captured.</summary>
    Skip,
    /// <summary>
    /// Without any clip, painted above the areas. An adorner that draws in content coordinates can
    /// thus contribute parts outside the viewport, e.g. link lines to rows scrolled out of view.
    /// </summary>
    Unclipped,
}

public sealed class VisualSvgWriter
{
    private readonly SvgCaptureOptions _options;
    private readonly StringBuilder _defs = new();
    private readonly StringBuilder _body = new();
    private readonly StringBuilder _adornerLayer = new(); // unclipped adorners, painted above the areas
    private readonly StringBuilder _textLayer = new();   // owned text of all areas, painted last
    private readonly Dictionary<(Rect, Geometry?), string> _clipIds = new();
    private int _idCounter;

    // State of the area being captured.
    private Rect _target;
    private Rect? _textOwnArea;
    private Matrix _placement = Matrix.Identity;         // capture root -> output document
    private Matrix _ctm = Matrix.Identity;               // current drawing space -> capture root
    private Rect _clipRect;                              // accumulated axis-aligned clip (root coordinates)
    private Geometry? _clipGeometry;                     // accumulated non-rectangular clip (root coordinates)
    private double _opacity = 1;
    private AdornerCapture _adorners = AdornerCapture.AsRendered;
    private bool _inUnclippedAdorner;
    private HashSet<DependencyObject>? _includedRoots;   // null: everything in the area
    private HashSet<DependencyObject>? _pathToIncluded;  // ancestors of the included roots
    private bool _inIncluded;

    private StringBuilder Body => _inUnclippedAdorner ? _adornerLayer : _body;

    /// <summary>Drawing/brush kinds that could not be converted, for diagnostics.</summary>
    public Dictionary<string, int> Unsupported { get; } = new();

    /// <summary>Natively rendered areas found during capture; their placeholders are in the output.</summary>
    public List<HostedContent> HostedContents { get; } = new();

    /// <summary>
    /// Bounds (output coordinates) of everything drawn except backgrounds and frames, i.e. of
    /// text, icons, lines and small shapes. Used to crop empty space.
    /// </summary>
    public Rect ContentBounds { get; private set; } = Rect.Empty;

    /// <summary>Output size cropped to the content on the right and bottom (never on the left/top).</summary>
    public (double Width, double Height) CropToContent(double width, double height, double padding = 8)
    {
        if (ContentBounds.IsEmpty) return (width, height);
        return (Math.Min(width, Math.Max(1, ContentBounds.Right + padding)),
                Math.Min(height, Math.Max(1, ContentBounds.Bottom + padding)));
    }

    private void TrackContent(Rect boundsInRoot, bool isBackgroundCandidate)
    {
        if (boundsInRoot.IsEmpty) return;
        // Panel backgrounds, borders and selection bars span (nearly) the whole capture area.
        if (isBackgroundCandidate && (boundsInRoot.Width >= _target.Width * 0.6 || boundsInRoot.Height >= _target.Height * 0.6)) return;
        var r = Rect.Transform(boundsInRoot, _placement);
        r.Intersect(Rect.Transform(_target, _placement));
        if (r.IsEmpty) return;
        ContentBounds = ContentBounds.IsEmpty ? r : Rect.Union(ContentBounds, r);
    }

    public VisualSvgWriter(SvgCaptureOptions? options = null) => _options = options ?? new SvgCaptureOptions();

    /// <summary>Captures the area of <paramref name="target"/> as rendered inside <paramref name="captureRoot"/>.</summary>
    public string Capture(Visual captureRoot, FrameworkElement target)
    {
        var bounds = target.TransformToAncestor(captureRoot).TransformBounds(new Rect(target.RenderSize));
        return Capture(captureRoot, bounds);
    }

    /// <summary>Captures <paramref name="area"/> (in the coordinate space of <paramref name="captureRoot"/>).</summary>
    public string Capture(Visual captureRoot, Rect area)
    {
        Begin();
        AddArea(captureRoot, area, new Point(0, 0));
        return End(area.Width, area.Height);
    }

    // ── Composition: several captured areas placed into one document ───────

    public void Begin()
    {
        _defs.Clear(); _body.Clear(); _adornerLayer.Clear(); _textLayer.Clear(); _clipIds.Clear();
        Unsupported.Clear(); HostedContents.Clear(); ContentBounds = Rect.Empty; _idCounter = 0;
    }

    /// <summary>
    /// Renders <paramref name="area"/> of <paramref name="captureRoot"/> clipped to that area and
    /// places its top-left corner at <paramref name="placeAt"/> in the output document.
    /// </summary>
    /// <param name="textOwnArea">
    /// When set, text is not clipped: a text run is written, complete, only if its baseline origin
    /// lies inside this area (capture root coordinates). Pages of a scrolled capture pass disjoint
    /// areas, so text cut by a page border appears exactly once and without seams. Such text is
    /// painted after all areas, otherwise the background of the next page would cover the part of
    /// the text that reaches into it.
    /// </param>
    /// <param name="adorners">How adorners in the area are captured.</param>
    /// <param name="onlyWithin">
    /// When set, only these subtrees are drawn. Their ancestors contribute neither drawings nor
    /// clips, so a subtree that was temporarily made larger than its parent is captured in full
    /// without the elements it now overlaps.
    /// </param>
    public void AddArea(Visual captureRoot, Rect area, Point placeAt, Rect? textOwnArea = null,
        AdornerCapture adorners = AdornerCapture.AsRendered, IReadOnlyCollection<Visual>? onlyWithin = null)
    {
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return;
        _target = area;
        _textOwnArea = textOwnArea;
        _adorners = adorners;
        _includedRoots = null;
        _pathToIncluded = null;
        _inIncluded = false;
        if (onlyWithin != null)
        {
            _includedRoots = new HashSet<DependencyObject>(onlyWithin);
            _pathToIncluded = new HashSet<DependencyObject>();
            foreach (var root in onlyWithin)
                for (var d = VisualTreeHelper.GetParent(root); d != null; d = VisualTreeHelper.GetParent(d))
                    _pathToIncluded.Add(d);
        }
        _placement = new Matrix(1, 0, 0, 1, placeAt.X - area.X, placeAt.Y - area.Y);
        _ctm = Matrix.Identity;
        _clipRect = area;
        _clipGeometry = null;
        _opacity = 1;
        _clipIds.Clear();   // clip ids are only valid for one placement

        WriteVisual(captureRoot, Matrix.Identity, isRoot: true);

        _textOwnArea = null;
        _adorners = AdornerCapture.AsRendered;
        _includedRoots = null;
        _pathToIncluded = null;
    }

    /// <summary>Fills a rectangle of the output document, e.g. to extend a background.</summary>
    public void AddRect(Rect rect, Brush brush)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;
        var (data, _, _) = From(new RectangleGeometry(rect));
        _body.Append("<path d=\"").Append(data).Append('"').Append(PaintAttrs("fill", brush)).Append(" stroke=\"none\"/>\n");
    }

    public string End(double width, double height)
    {
        var w = Format(width); var h = Format(height);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">\n");
        if (_defs.Length > 0) sb.Append("<defs>\n").Append(_defs).Append("</defs>\n");
        sb.Append(_body);
        sb.Append(_adornerLayer);
        sb.Append(_textLayer);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // ── Visual tree ─────────────────────────────────────────────────────────

    private void WriteVisual(Visual visual, Matrix parentToRoot, bool isRoot)
    {
        if (visual is UIElement ui && ui.Visibility != Visibility.Visible) return;

        var local = Matrix.Identity;
        if (!isRoot)
        {
            var t = VisualTreeHelper.GetTransform(visual);
            if (t != null) local = t.Value;
            var offset = VisualTreeHelper.GetOffset(visual);
            local.Translate(offset.X, offset.Y);
        }
        var toRoot = local * parentToRoot;

        // Natively rendered content (WebView2 etc.) has no WPF drawing: leave a placeholder.
        if (visual is System.Windows.Interop.HwndHost host)
        {
            if (_includedRoots != null && !_inIncluded && !_includedRoots.Contains(visual)) return;
            var hostRect = Rect.Transform(new Rect(host.RenderSize), toRoot);
            if (hostRect.IntersectsWith(_target) && hostRect.Width > 0 && hostRect.Height > 0)
            {
                var hosted = new HostedContent { Id = HostedContents.Count + 1, Host = host, OutputRect = Rect.Transform(hostRect, _placement) };
                HostedContents.Add(hosted);
                _body.Append(hosted.Placeholder).Append('\n');
            }
            return;
        }

        // Restricted to some subtrees: ancestors are only passed through, everything else is left out.
        var startsIncluded = false;
        if (_includedRoots != null && !_inIncluded)
        {
            if (_includedRoots.Contains(visual))
            {
                startsIncluded = true;
            }
            else
            {
                if (!_pathToIncluded!.Contains(visual)) return;
                var passCount = VisualTreeHelper.GetChildrenCount(visual);
                for (int i = 0; i < passCount; i++)
                    if (VisualTreeHelper.GetChild(visual, i) is Visual child) WriteVisual(child, toRoot, isRoot: false);
                return;
            }
        }

        var startsUnclippedAdorner = false;
        if (visual is System.Windows.Documents.Adorner && !_inUnclippedAdorner)
        {
            if (_adorners == AdornerCapture.Skip) return;
            startsUnclippedAdorner = _adorners == AdornerCapture.Unclipped;
        }

        // Prune everything that cannot intersect the target area (not unclipped adorners: their
        // bounds are limited by their own clip, the drawing is not).
        if (!_inUnclippedAdorner && !startsUnclippedAdorner)
        {
            var bounds = VisualTreeHelper.GetDescendantBounds(visual);
            bounds.Union(VisualTreeHelper.GetContentBounds(visual));
            if (bounds.IsEmpty) return;
            if (!Rect.Transform(bounds, toRoot).IntersectsWith(_target)) return;
        }

        var opacity = VisualTreeHelper.GetOpacity(visual);
        if (opacity <= 0) return;

        var saved = SaveState();
        var wasUnclipped = _inUnclippedAdorner;
        var wasIncluded = _inIncluded;
        if (startsIncluded) _inIncluded = true;
        _ctm = toRoot;
        _opacity *= opacity;
        if (startsUnclippedAdorner)
        {
            _inUnclippedAdorner = true;
            _clipRect = new Rect(-1e7, -1e7, 2e7, 2e7);
            _clipGeometry = null;
        }
        var clip = _inUnclippedAdorner ? null : VisualTreeHelper.GetClip(visual);
        if (clip != null && !PushClip(clip)) { _inIncluded = wasIncluded; _inUnclippedAdorner = wasUnclipped; RestoreState(saved); return; }

        var drawing = VisualTreeHelper.GetDrawing(visual);
        if (drawing != null) WriteDrawing(drawing);

        var count = VisualTreeHelper.GetChildrenCount(visual);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(visual, i) is Visual child)
                WriteVisual(child, toRoot, isRoot: false);
            else
                Count("Visual3D");
        }

        _inUnclippedAdorner = wasUnclipped;
        _inIncluded = wasIncluded;
        RestoreState(saved);
    }

    private (Matrix, Rect, Geometry?, double) SaveState() => (_ctm, _clipRect, _clipGeometry, _opacity);

    private void RestoreState((Matrix Ctm, Rect ClipRect, Geometry? ClipGeometry, double Opacity) s)
    {
        _ctm = s.Ctm; _clipRect = s.ClipRect; _clipGeometry = s.ClipGeometry; _opacity = s.Opacity;
    }

    /// <summary>Intersects the current clip with <paramref name="clip"/> (in the current drawing space).</summary>
    /// <returns>False if nothing remains visible.</returns>
    private bool PushClip(Geometry clip)
    {
        var m = (clip.Transform?.Value ?? Matrix.Identity) * _ctm;
        if (clip is RectangleGeometry { RadiusX: 0, RadiusY: 0 } rg && m.M12 == 0 && m.M21 == 0)
        {
            var r = Rect.Transform(rg.Rect, m);
            r.Intersect(_clipRect);
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return false;
            _clipRect = r;
            return true;
        }

        var inRoot = PathGeometry.CreateFromGeometry(clip);
        inRoot.Transform = new MatrixTransform(m);
        _clipGeometry = _clipGeometry == null
            ? inRoot
            : Geometry.Combine(_clipGeometry, inRoot, GeometryCombineMode.Intersect, null);
        return !_clipGeometry.IsEmpty();
    }

    // ── Drawings ────────────────────────────────────────────────────────────

    private void WriteDrawing(Drawing drawing)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                WriteDrawingGroup(group);
                break;
            case GeometryDrawing gd:
                WriteGeometry(gd.Geometry, gd.Brush, gd.Pen);
                break;
            case GlyphRunDrawing grd:
                WriteGlyphRun(grd.GlyphRun, grd.ForegroundBrush);
                break;
            case ImageDrawing id:
                WriteImage(id.ImageSource, id.Rect);
                break;
            default:
                Count(drawing.GetType().Name);
                break;
        }
    }

    private void WriteDrawingGroup(DrawingGroup group)
    {
        if (group.Children.Count == 0 || group.Opacity <= 0) return;
        if (group.OpacityMask != null) Count("OpacityMask");

        var saved = SaveState();
        if (group.Transform != null) _ctm = group.Transform.Value * _ctm;
        _opacity *= group.Opacity;
        if (group.ClipGeometry == null || _inUnclippedAdorner || PushClip(group.ClipGeometry))
            foreach (var child in group.Children) WriteDrawing(child);
        RestoreState(saved);
    }

    private void WriteGeometry(Geometry? geometry, Brush? brush, Pen? pen)
    {
        if (geometry == null || geometry.IsEmpty()) return;
        if (brush == null && (pen == null || pen.Brush == null || pen.Thickness <= 0)) return;

        // Tile brushes (DrawingBrush, ImageBrush) are expanded into clipped content.
        if (brush is TileBrush tile)
        {
            WriteTileBrushFill(geometry, tile);
            brush = null;
            if (pen == null || pen.Brush == null) return;
        }

        var (data, evenOdd, transform) = From(geometry);
        if (data.Length == 0) return;
        var toOutput = (transform ?? Matrix.Identity) * _ctm * _placement;

        var sb = new StringBuilder("<path d=\"").Append(data).Append('"');
        AppendTransform(sb, toOutput);
        sb.Append(PaintAttrs("fill", brush));
        if (brush != null && evenOdd) sb.Append(" fill-rule=\"evenodd\"");

        var stroked = pen != null && pen.Brush != null && pen.Thickness > 0;
        if (stroked)
        {
            sb.Append(PaintAttrs("stroke", pen!.Brush));
            sb.Append($" stroke-width=\"{Format(pen.Thickness)}\"");
            if (pen.StartLineCap != PenLineCap.Flat)
                sb.Append($" stroke-linecap=\"{(pen.StartLineCap == PenLineCap.Round ? "round" : "square")}\"");
            if (pen.LineJoin != PenLineJoin.Miter)
                sb.Append($" stroke-linejoin=\"{(pen.LineJoin == PenLineJoin.Round ? "round" : "bevel")}\"");
            else if (pen.MiterLimit != 4)
                sb.Append($" stroke-miterlimit=\"{Format(pen.MiterLimit)}\"");
            if (pen.DashStyle != null && pen.DashStyle.Dashes.Count > 0)
            {
                var dashes = string.Join(",", pen.DashStyle.Dashes.Select(d => Format(Math.Max(d * pen.Thickness, 0.001))));
                sb.Append($" stroke-dasharray=\"{dashes}\"");
                if (pen.DashStyle.Offset != 0) sb.Append($" stroke-dashoffset=\"{Format(pen.DashStyle.Offset * pen.Thickness)}\"");
            }
        }
        else
        {
            sb.Append(" stroke=\"none\"");
        }
        AppendOpacity(sb);
        sb.Append("/>\n");

        var inRoot = Rect.Transform(geometry.Bounds, (transform ?? Matrix.Identity) * _ctm);
        if (stroked) inRoot.Inflate(pen!.Thickness, pen.Thickness);
        EmitClipped(Body, sb.ToString(), inRoot);
    }

    private void WriteTileBrushFill(Geometry geometry, TileBrush tile)
    {
        var area = geometry.Bounds;
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return;

        Drawing? content = tile switch
        {
            DrawingBrush db => db.Drawing,
            ImageBrush ib when ib.ImageSource != null => new ImageDrawing(ib.ImageSource, new Rect(0, 0, ib.ImageSource.Width, ib.ImageSource.Height)),
            _ => null,
        };
        if (content == null) { Count(tile.GetType().Name); return; }
        if (tile.TileMode != TileMode.None) Count("TileMode." + tile.TileMode);

        var viewbox = tile.ViewboxUnits == BrushMappingMode.Absolute
            ? tile.Viewbox
            : ScaleRelative(tile.Viewbox, content.Bounds);
        var viewport = tile.ViewportUnits == BrushMappingMode.Absolute
            ? tile.Viewport
            : ScaleRelative(tile.Viewport, area);
        if (viewbox.Width <= 0 || viewbox.Height <= 0) return;

        var sx = viewport.Width / viewbox.Width;
        var sy = viewport.Height / viewbox.Height;
        switch (tile.Stretch)
        {
            case Stretch.None: sx = sy = 1; break;
            case Stretch.Uniform: sx = sy = Math.Min(sx, sy); break;
            case Stretch.UniformToFill: sx = sy = Math.Max(sx, sy); break;
        }
        var w = viewbox.Width * sx; var h = viewbox.Height * sy;
        var dx = viewport.X + (tile.AlignmentX == AlignmentX.Left ? 0 : tile.AlignmentX == AlignmentX.Center ? 0.5 : 1) * (viewport.Width - w);
        var dy = viewport.Y + (tile.AlignmentY == AlignmentY.Top ? 0 : tile.AlignmentY == AlignmentY.Center ? 0.5 : 1) * (viewport.Height - h);
        var m = new Matrix(sx, 0, 0, sy, dx - viewbox.X * sx, dy - viewbox.Y * sy);

        var saved = SaveState();
        _opacity *= tile.Opacity;
        if (PushClip(geometry))
        {
            _ctm = m * _ctm;
            WriteDrawing(content);
        }
        RestoreState(saved);

        static Rect ScaleRelative(Rect r, Rect basis) =>
            new(basis.X + r.X * basis.Width, basis.Y + r.Y * basis.Height, r.Width * basis.Width, r.Height * basis.Height);
    }

    private void WriteGlyphRun(GlyphRun? run, Brush? brush)
    {
        if (run == null || brush == null) return;

        var target = Body;
        var clipped = true;
        if (_textOwnArea is Rect own)
        {
            var origin = _ctm.Transform(run.BaselineOrigin);
            const double eps = 0.01;
            if (origin.X < own.Left - eps || origin.X >= own.Right - eps || origin.Y < own.Top - eps || origin.Y >= own.Bottom - eps)
                return;
            target = _textLayer;
            clipped = false;
        }

        var toOutput = _ctm * _placement;
        string element;
        if (_options.TextAsPaths || run.Characters == null || run.Characters.Count == 0)
        {
            var (data, _, transform) = From(run.BuildGeometry());
            if (data.Length == 0) return;
            var sb = new StringBuilder("<path d=\"").Append(data).Append('"');
            AppendTransform(sb, (transform ?? Matrix.Identity) * toOutput);
            sb.Append(PaintAttrs("fill", brush)).Append(" stroke=\"none\"");
            AppendOpacity(sb);
            element = sb.Append("/>\n").ToString();
        }
        else
        {
            element = TextElement(run, brush, toOutput);
        }

        var textBounds = Rect.Transform(run.BuildGeometry().Bounds, _ctm);
        if (clipped)
        {
            EmitClipped(target, element, textBounds, isText: true);
        }
        else
        {
            target.Append(element);
            TrackContent(textBounds, isBackgroundCandidate: false);
        }
    }

    private string TextElement(GlyphRun run, Brush brush, Matrix toOutput)
    {
        var chars = run.Characters;
        var tf = run.GlyphTypeface;
        var baseline = run.BaselineOrigin;

        // Per-glyph x positions keep the exact WPF layout independent of the SVG viewer's shaping.
        var glyphX = new double[run.GlyphIndices.Count];
        double x = baseline.X;
        for (int i = 0; i < glyphX.Length; i++)
        {
            var offset = run.GlyphOffsets != null && i < run.GlyphOffsets.Count ? run.GlyphOffsets[i].X : 0;
            glyphX[i] = x + offset;
            x += run.AdvanceWidths[i];
        }

        string xAttr;
        if (run.ClusterMap == null && chars.Count == glyphX.Length)
            xAttr = string.Join(" ", glyphX.Select(Format));
        else if (run.ClusterMap != null && run.ClusterMap.Count == chars.Count)
            xAttr = string.Join(" ", run.ClusterMap.Select(g => Format(glyphX[Math.Min(g, glyphX.Length - 1)])));
        else
            xAttr = Format(baseline.X);

        var sb = new StringBuilder("<text xml:space=\"preserve\"");
        sb.Append($" x=\"{xAttr}\" y=\"{Format(baseline.Y)}\"");
        AppendTransform(sb, toOutput);
        sb.Append($" font-family=\"{Escape(FamilyName(tf))}\" font-size=\"{Format(run.FontRenderingEmSize)}\"");
        var weight = tf.Weight.ToOpenTypeWeight();
        if (weight != 400) sb.Append($" font-weight=\"{weight}\"");
        if (tf.Style != FontStyles.Normal) sb.Append(" font-style=\"italic\"");
        sb.Append(PaintAttrs("fill", brush));
        AppendOpacity(sb);
        sb.Append('>').Append(Escape(new string(chars.ToArray()))).Append("</text>\n");
        return sb.ToString();
    }

    private static string FamilyName(GlyphTypeface tf)
    {
        var en = System.Globalization.CultureInfo.GetCultureInfo("en-us");
        if (tf.FamilyNames.TryGetValue(en, out var name)) return name;
        return tf.FamilyNames.Values.FirstOrDefault() ?? "sans-serif";
    }

    private void WriteImage(ImageSource? source, Rect rect)
    {
        if (source == null || rect.IsEmpty) return;
        switch (source)
        {
            case DrawingImage di when di.Drawing != null:
            {
                var b = di.Drawing.Bounds;
                if (b.Width <= 0 || b.Height <= 0) return;
                var m = new Matrix(rect.Width / b.Width, 0, 0, rect.Height / b.Height,
                                   rect.X - b.X * rect.Width / b.Width, rect.Y - b.Y * rect.Height / b.Height);
                var saved = SaveState();
                _ctm = m * _ctm;
                WriteDrawing(di.Drawing);
                RestoreState(saved);
                break;
            }
            case BitmapSource bmp:
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                var sb = new StringBuilder($"<image x=\"{Format(rect.X)}\" y=\"{Format(rect.Y)}\" width=\"{Format(rect.Width)}\" height=\"{Format(rect.Height)}\" preserveAspectRatio=\"none\"");
                AppendTransform(sb, _ctm * _placement);
                AppendOpacity(sb);
                sb.Append($" xlink:href=\"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}\"/>\n");
                EmitClipped(Body, sb.ToString(), Rect.Transform(rect, _ctm));
                break;
            }
            default:
                Count(source.GetType().Name);
                break;
        }
    }

    // ── Clipping, paint servers, helpers ────────────────────────────────────

    /// <summary>Writes an element, wrapped in a clip group only if it reaches beyond the current clip.</summary>
    private void EmitClipped(StringBuilder target, string element, Rect boundsInRoot, bool isText = false)
    {
        var needsClip = _clipGeometry != null || !Contains(_clipRect, boundsInRoot);
        if (!needsClip) { target.Append(element); TrackContent(boundsInRoot, !isText); return; }
        if (!boundsInRoot.IsEmpty && !boundsInRoot.IntersectsWith(_clipRect)) return;

        target.Append($"<g clip-path=\"url(#{ClipId()})\">\n").Append(element).Append("</g>\n");
        var visible = boundsInRoot; visible.Intersect(_clipRect);
        TrackContent(visible, !isText);

        static bool Contains(Rect outer, Rect inner)
        {
            const double eps = 0.01;
            return !inner.IsEmpty && inner.Left >= outer.Left - eps && inner.Top >= outer.Top - eps
                && inner.Right <= outer.Right + eps && inner.Bottom <= outer.Bottom + eps;
        }
    }

    /// <summary>Clip path of the current clip state, in output document coordinates.</summary>
    private string ClipId()
    {
        var key = (_clipRect, _clipGeometry);
        if (_clipIds.TryGetValue(key, out var id)) return id;

        var rectId = NextId("clip");
        var (rectData, _, _) = From(new RectangleGeometry(Rect.Transform(_clipRect, _placement)));
        _defs.Append($"<clipPath id=\"{rectId}\" clipPathUnits=\"userSpaceOnUse\"><path d=\"{rectData}\"/></clipPath>\n");
        id = rectId;

        if (_clipGeometry != null)
        {
            id = NextId("clip");
            var (data, evenOdd, transform) = From(_clipGeometry);
            _defs.Append($"<clipPath id=\"{id}\" clipPathUnits=\"userSpaceOnUse\" clip-path=\"url(#{rectId})\"><path d=\"{(data.Length == 0 ? "M0,0" : data)}\"");
            _defs.Append($" transform=\"{MatrixAttr((transform ?? Matrix.Identity) * _placement)}\"");
            if (evenOdd) _defs.Append(" clip-rule=\"evenodd\"");
            _defs.Append("/></clipPath>\n");
        }

        _clipIds[key] = id;
        return id;
    }

    private void AppendTransform(StringBuilder sb, Matrix m)
    {
        if (!m.IsIdentity) sb.Append($" transform=\"{MatrixAttr(m)}\"");
    }

    private void AppendOpacity(StringBuilder sb)
    {
        if (_opacity < 1) sb.Append($" opacity=\"{Format(_opacity)}\"");
    }

    private string PaintAttrs(string attr, Brush? brush)
    {
        switch (brush)
        {
            case null:
                return $" {attr}=\"none\"";
            case SolidColorBrush s:
            {
                var a = s.Color.A / 255.0 * s.Opacity;
                if (a <= 0) return $" {attr}=\"none\"";
                var r = $" {attr}=\"{Hex(s.Color)}\"";
                if (a < 1) r += $" {attr}-opacity=\"{Format(a)}\"";
                return r;
            }
            case LinearGradientBrush lg:
            {
                var id = NextId("lg");
                var units = lg.MappingMode == BrushMappingMode.Absolute ? "userSpaceOnUse" : "objectBoundingBox";
                _defs.Append($"<linearGradient id=\"{id}\" gradientUnits=\"{units}\" x1=\"{Format(lg.StartPoint.X)}\" y1=\"{Format(lg.StartPoint.Y)}\" x2=\"{Format(lg.EndPoint.X)}\" y2=\"{Format(lg.EndPoint.Y)}\"{Spread(lg.SpreadMethod)}>\n");
                AppendStops(lg);
                _defs.Append("</linearGradient>\n");
                return GradientRef(attr, id, lg);
            }
            case RadialGradientBrush rg:
            {
                var id = NextId("rg");
                var units = rg.MappingMode == BrushMappingMode.Absolute ? "userSpaceOnUse" : "objectBoundingBox";
                if (Math.Abs(rg.RadiusX - rg.RadiusY) > 1e-6) Count("RadialGradient.Elliptic");
                _defs.Append($"<radialGradient id=\"{id}\" gradientUnits=\"{units}\" cx=\"{Format(rg.Center.X)}\" cy=\"{Format(rg.Center.Y)}\" r=\"{Format(rg.RadiusX)}\" fx=\"{Format(rg.GradientOrigin.X)}\" fy=\"{Format(rg.GradientOrigin.Y)}\"{Spread(rg.SpreadMethod)}>\n");
                AppendStops(rg);
                _defs.Append("</radialGradient>\n");
                return GradientRef(attr, id, rg);
            }
            default:
                Count(brush.GetType().Name);
                return $" {attr}=\"none\"";
        }
    }

    private string GradientRef(string attr, string id, GradientBrush brush)
    {
        if (brush.Transform != null && !brush.Transform.Value.IsIdentity) Count("GradientBrush.Transform");
        if (brush.RelativeTransform != null && !brush.RelativeTransform.Value.IsIdentity) Count("GradientBrush.RelativeTransform");
        var r = $" {attr}=\"url(#{id})\"";
        if (brush.Opacity < 1) r += $" {attr}-opacity=\"{Format(brush.Opacity)}\"";
        return r;
    }

    private void AppendStops(GradientBrush brush)
    {
        foreach (var stop in brush.GradientStops.OrderBy(s => s.Offset))
        {
            var a = stop.Color.A / 255.0;
            _defs.Append($"  <stop offset=\"{Format(stop.Offset)}\" stop-color=\"{Hex(stop.Color)}\"");
            if (a < 1) _defs.Append($" stop-opacity=\"{Format(a)}\"");
            _defs.Append("/>\n");
        }
    }

    private static string Spread(GradientSpreadMethod m) => m switch
    {
        GradientSpreadMethod.Reflect => " spreadMethod=\"reflect\"",
        GradientSpreadMethod.Repeat => " spreadMethod=\"repeat\"",
        _ => "",
    };

    private string NextId(string prefix) => prefix + (++_idCounter);

    private void Count(string kind) => Unsupported[kind] = Unsupported.TryGetValue(kind, out var n) ? n + 1 : 1;

    private static string MatrixAttr(Matrix m)
    {
        if (m.M11 == 1 && m.M12 == 0 && m.M21 == 0 && m.M22 == 1)
            return $"translate({Format(m.OffsetX)},{Format(m.OffsetY)})";
        return $"matrix({Format(m.M11)},{Format(m.M12)},{Format(m.M21)},{Format(m.M22)},{Format(m.OffsetX)},{Format(m.OffsetY)})";
    }

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static string Escape(string s)
    {
        // Control characters are not allowed in XML 1.0 and would make the file unreadable.
        var clean = new string(s.Where(c => c >= 0x20 || c == '\t').ToArray());
        return SecurityElement.Escape(clean) ?? "";
    }
}
