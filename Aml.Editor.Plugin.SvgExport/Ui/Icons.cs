using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Aml.Editor.Plugin.SvgExport.Ui;

/// <summary>
/// Outline icons on a 24x24 grid, drawn in the foreground color of the surrounding control
/// so they follow the editor theme (light or dark) without own colors.
/// </summary>
public static class Icons
{
    public const string Tree = "M3,3 H9 V8 H3 Z M6,8 V19 H12 M6,13.5 H12 M13,11 H21 V16 H13 Z M13,17 H21 V22 H13 Z";
    public const string Window = "M3,4 H21 V20 H3 Z M3,8 H21 M6,6 H6.5 M8.5,6 H9";
    public const string Region = "M6,2 V18 H22 M2,6 H18 V22";
    public const string Clipboard = "M9,3 H15 V6 H9 Z M9,4.5 H5 V21 H19 V4.5 H15 M8,11 H16 M8,15 H14";
    public const string Folder = "M3,6 H9 L11,8 H21 V19 H3 Z";
    public const string File = "M6,2 H14 L19,7 V22 H6 Z M14,2 V7 H19";
    public const string Refresh = "M19.5,12 A7.5,7.5 0 1 1 17.3,6.7 M19,3 V7.5 H14.5";
    public const string Check = "M4,12.5 L9.5,18 L20,6";
    public const string Warning = "M12,3 L22,20 H2 Z M12,9 V14 M12,16.5 V17.5";

    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        [nameof(Tree)] = Tree, [nameof(Window)] = Window, [nameof(Region)] = Region,
        [nameof(Clipboard)] = Clipboard, [nameof(Folder)] = Folder, [nameof(File)] = File,
        [nameof(Refresh)] = Refresh, [nameof(Check)] = Check, [nameof(Warning)] = Warning,
    };

    /// <summary>An icon element; stroke follows the nearest control's Foreground.</summary>
    public static FrameworkElement Create(string data, double size = 16)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
        };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
            FallbackValue = SystemColors.ControlTextBrush,
        });
        return new Viewbox
        {
            Width = size, Height = size,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
    }

    /// <summary>Icon plus label, e.g. for the editor's plugin toolbar.</summary>
    public static FrameworkElement WithLabel(string data, string label) =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 0, 4, 0),
            Children =
            {
                Create(data, 16),
                new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
            },
        };
}
