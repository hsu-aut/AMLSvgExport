using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Aml.Editor.Plugin.SvgExport.Capture;

/// <summary>
/// Converts WPF geometries into SVG path data. WPF's own Geometry.ToString() uses a
/// mini-language with tokens SVG does not understand (F0/F1 fill rule prefix), so the
/// figures and segments are walked explicitly.
/// </summary>
internal static class SvgPathData
{
    public static string Format(double v)
    {
        var r = Math.Round(v, 3);
        if (r == 0) r = 0; // avoid "-0"
        return r.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static string Pt(Point p) => Format(p.X) + "," + Format(p.Y);

    /// <summary>Path data plus the geometry's own transform (null when identity).</summary>
    public static (string Data, bool EvenOdd, Matrix? Transform) From(Geometry geometry)
    {
        var pg = geometry as PathGeometry ?? PathGeometry.CreateFromGeometry(geometry);
        var sb = new StringBuilder();
        foreach (var figure in pg.Figures)
            AppendFigure(sb, figure);

        Matrix? transform = null;
        if (pg.Transform != null && !pg.Transform.Value.IsIdentity)
            transform = pg.Transform.Value;

        return (sb.ToString().TrimEnd(), pg.FillRule == FillRule.EvenOdd, transform);
    }

    private static void AppendFigure(StringBuilder sb, PathFigure figure)
    {
        sb.Append('M').Append(Pt(figure.StartPoint)).Append(' ');
        foreach (var segment in figure.Segments)
        {
            switch (segment)
            {
                case LineSegment l:
                    sb.Append('L').Append(Pt(l.Point)).Append(' ');
                    break;
                case PolyLineSegment pl:
                    foreach (var p in pl.Points) sb.Append('L').Append(Pt(p)).Append(' ');
                    break;
                case BezierSegment b:
                    sb.Append('C').Append(Pt(b.Point1)).Append(' ').Append(Pt(b.Point2)).Append(' ').Append(Pt(b.Point3)).Append(' ');
                    break;
                case PolyBezierSegment pb:
                    for (int i = 0; i + 2 < pb.Points.Count; i += 3)
                        sb.Append('C').Append(Pt(pb.Points[i])).Append(' ').Append(Pt(pb.Points[i + 1])).Append(' ').Append(Pt(pb.Points[i + 2])).Append(' ');
                    break;
                case QuadraticBezierSegment q:
                    sb.Append('Q').Append(Pt(q.Point1)).Append(' ').Append(Pt(q.Point2)).Append(' ');
                    break;
                case PolyQuadraticBezierSegment pq:
                    for (int i = 0; i + 1 < pq.Points.Count; i += 2)
                        sb.Append('Q').Append(Pt(pq.Points[i])).Append(' ').Append(Pt(pq.Points[i + 1])).Append(' ');
                    break;
                case ArcSegment a:
                    // WPF and SVG share the y-down screen space: Clockwise == sweep-flag 1.
                    sb.Append('A').Append(Format(a.Size.Width)).Append(',').Append(Format(a.Size.Height)).Append(' ')
                      .Append(Format(a.RotationAngle)).Append(' ')
                      .Append(a.IsLargeArc ? '1' : '0').Append(',')
                      .Append(a.SweepDirection == SweepDirection.Clockwise ? '1' : '0').Append(' ')
                      .Append(Pt(a.Point)).Append(' ');
                    break;
            }
        }
        if (figure.IsClosed) sb.Append("Z ");
    }
}
