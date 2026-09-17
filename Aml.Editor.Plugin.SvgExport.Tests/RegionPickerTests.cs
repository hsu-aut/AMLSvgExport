using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.SvgExport.Capture;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

public class RegionPickerTests
{
    private static Window ShowTarget()
    {
        var window = new Window
        {
            Width = 400, Height = 300, Content = new Grid(),
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    [Fact]
    public void Dragged_rectangle_is_the_result_and_not_reported_as_cancelled() => Sta.Run(() =>
    {
        var target = ShowTarget();
        try
        {
            var picker = RegionPicker.Start(target);
            picker.Begin(new Point(120, 80));
            picker.Move(new Point(60, 150));
            picker.End(new Point(40, 200));

            Assert.True(picker.Result.IsCompleted);
            Assert.Equal(new Rect(40, 80, 80, 120), picker.Result.Result);
            Assert.False(picker.Overlay.IsVisible);
        }
        finally
        {
            target.Close();
        }
    });

    [Fact]
    public void A_click_without_dragging_or_closing_the_overlay_cancels() => Sta.Run(() =>
    {
        var target = ShowTarget();
        try
        {
            var click = RegionPicker.Start(target);
            click.Begin(new Point(50, 50));
            click.End(new Point(51, 50));
            Assert.Null(click.Result.Result);

            var closed = RegionPicker.Start(target);
            closed.Overlay.Close();
            Assert.Null(closed.Result.Result);
        }
        finally
        {
            target.Close();
        }
    });
}
