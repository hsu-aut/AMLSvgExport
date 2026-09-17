using System.Windows;
using System.Windows.Media;
using Aml.Editor.Plugin.SvgExport.Capture;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

public class SvgPathDataTests
{
    [Fact]
    public void Rectangle_becomes_closed_move_line_path() => Sta.Run(() =>
    {
        var (data, evenOdd, transform) = SvgPathData.From(new RectangleGeometry(new Rect(1, 2, 10, 5)));
        Assert.Equal("M1,2 L11,2 L11,7 L1,7 Z", data);
        Assert.True(evenOdd);   // RectangleGeometry converts with the WPF default EvenOdd rule
        Assert.Null(transform);
    });

    [Fact]
    public void Wpf_mini_language_is_translated_to_svg_tokens() => Sta.Run(() =>
    {
        var geometry = Geometry.Parse("F1 M0,0 C1,1 2,2 3,3 Q4,4 5,5 A2,2 0 1 1 7,7 z");
        var (data, evenOdd, _) = SvgPathData.From(geometry);
        Assert.Equal("M0,0 C1,1 2,2 3,3 Q4,4 5,5 A2,2 0 1,1 7,7 Z", data);
        Assert.False(evenOdd);
        Assert.DoesNotContain("F1", data);
    });

    [Fact]
    public void Geometry_transform_is_reported_separately() => Sta.Run(() =>
    {
        var geometry = new RectangleGeometry(new Rect(0, 0, 1, 1)) { Transform = new TranslateTransform(5, 6) };
        var (_, _, transform) = SvgPathData.From(geometry);
        Assert.NotNull(transform);
        Assert.Equal(5, transform!.Value.OffsetX);
        Assert.Equal(6, transform.Value.OffsetY);
    });

    [Theory]
    [InlineData(1.23456, "1.235")]
    [InlineData(-0.0001, "0")]
    [InlineData(10.0, "10")]
    public void Numbers_use_invariant_culture_and_three_decimals(double value, string expected)
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try { Assert.Equal(expected, SvgPathData.Format(value)); }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
