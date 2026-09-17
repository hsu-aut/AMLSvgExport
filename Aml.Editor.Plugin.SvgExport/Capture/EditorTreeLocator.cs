using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Aml.Editor.Plugin.SvgExport.Capture;

/// <summary>An editor tree found in the running editor, with the panel parts around it.</summary>
public sealed class EditorTree
{
    public required FrameworkElement Tree { get; init; }
    public required Window Window { get; init; }
    /// <summary>Infragistics ContentPane hosting the tree (content incl. toolbar, own header if docked alone).</summary>
    public FrameworkElement? ContentPane { get; init; }
    /// <summary>Infragistics TabGroupPane around the ContentPane (adds the tab strip header).</summary>
    public FrameworkElement? TabGroupPane { get; init; }
    public string Title { get; init; } = "";

    public override string ToString() =>
        (string.IsNullOrWhiteSpace(Title) ? "(unnamed tree)" : Title) + (Tree.IsVisible ? "" : "  (hidden, brought to front on export)");
}

/// <summary>
/// Finds the editor's tree controls. The plugin API exposes no view access, so the
/// visual trees of the editor windows are searched by type name. Type names instead of
/// a compile-time reference to Aml.Toolkit keep the plugin independent of the toolkit
/// version shipped with the editor.
/// </summary>
public static class EditorTreeLocator
{
    public const string TreeTypeName = "Aml.Toolkit.View.AMLTreeView";
    private const string ContentPaneTypeName = "Infragistics.Windows.DockManager.ContentPane";
    private const string TabGroupPaneTypeName = "Infragistics.Windows.DockManager.TabGroupPane";

    /// <summary>Keyboard shortcut that exports the focused (or last used) tree.</summary>
    public static readonly KeyGesture ExportGesture = new(Key.E, ModifierKeys.Control | ModifierKeys.Shift);

    /// <summary>Keyboard shortcut that exports the whole editor window as it is shown.</summary>
    public static readonly KeyGesture ExportWindowGesture = new(Key.W, ModifierKeys.Control | ModifierKeys.Shift);

    /// <summary>Keyboard shortcut that lets the user drag a rectangle to export.</summary>
    public static readonly KeyGesture ExportRegionGesture = new(Key.R, ModifierKeys.Control | ModifierKeys.Shift);

    /// <summary>
    /// Keyboard shortcut that copies the focused (or last used) tree to the clipboard.
    /// Not Ctrl+Shift+C: the editor uses that for "Copy XML markup" (also Ctrl+Shift+O and +V).
    /// </summary>
    public static readonly KeyGesture CopyTreeGesture = new(Key.K, ModifierKeys.Control | ModifierKeys.Shift);

    private static bool _hooked;

    /// <summary>
    /// The tree the user last clicked into. Kept although its panel may be hidden later, e.g.
    /// when the plugin's own panel is a tab in the same group and gets brought to front.
    /// </summary>
    public static EditorTree? LastUsed { get; private set; }

    /// <summary>Raised when the user clicks into an editor tree.</summary>
    public static event Action<EditorTree>? TreeUsed;

    /// <summary>Raised when the export shortcut is pressed anywhere in the editor.</summary>
    public static event Action? ExportRequested;

    /// <summary>Raised when the window export shortcut is pressed; carries the window it was pressed in.</summary>
    public static event Action<Window>? WindowExportRequested;

    /// <summary>Raised when the region export shortcut is pressed; carries the window it was pressed in.</summary>
    public static event Action<Window>? RegionExportRequested;

    /// <summary>Raised when the copy shortcut is pressed inside a tree.</summary>
    public static event Action? CopyRequested;

    /// <summary>Tracks clicks into trees and the export shortcut in all editor windows.</summary>
    public static void HookEditorInput()
    {
        if (_hooked) return;
        _hooked = true;
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((_, e) => Remember(e.OriginalSource as DependencyObject)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler((sender, e) =>
            {
                if (ExportWindowGesture.Matches(null, e) && sender is Window window)
                {
                    e.Handled = true;
                    WindowExportRequested?.Invoke(window);
                    return;
                }
                if (ExportRegionGesture.Matches(null, e) && sender is Window regionWindow)
                {
                    e.Handled = true;
                    RegionExportRequested?.Invoke(regionWindow);
                    return;
                }
                if (CopyTreeGesture.Matches(null, e))
                {
                    // Only inside a tree, so the gesture stays free elsewhere (e.g. for text boxes).
                    if (FindAncestor(Keyboard.FocusedElement as DependencyObject, IsTree) == null) return;
                    Remember(Keyboard.FocusedElement as DependencyObject);
                    e.Handled = true;
                    CopyRequested?.Invoke();
                    return;
                }
                if (!ExportGesture.Matches(null, e)) return;
                Remember(Keyboard.FocusedElement as DependencyObject);
                e.Handled = true;
                ExportRequested?.Invoke();
            }), handledEventsToo: true);
    }

    private static void Remember(DependencyObject? source)
    {
        var tree = FindAncestor(source, IsTree);
        if (tree == null || Window.GetWindow(tree) is not Window window) return;
        LastUsed = Build(tree, window);
        TreeUsed?.Invoke(LastUsed);
    }

    /// <summary>
    /// Visible trees, plus the last used tree if its panel is currently hidden (marked in the title).
    /// </summary>
    public static List<EditorTree> FindTrees()
    {
        var result = new List<EditorTree>();
        var windows = Application.Current?.Windows.Cast<Window>() ?? Enumerable.Empty<Window>();
        foreach (var window in windows)
        {
            foreach (var tree in FindDescendants(window, IsTree))
            {
                if (!tree.IsVisible) continue;
                var found = Build(tree, window);
                result.Add(found);
                if (LastUsed != null && ReferenceEquals(LastUsed.Tree, tree)) LastUsed = found;   // fresh title
            }
        }
        if (LastUsed != null && !result.Any(t => ReferenceEquals(t.Tree, LastUsed.Tree)))
            result.Insert(0, LastUsed);
        return result;
    }

    private static EditorTree Build(FrameworkElement tree, Window window)
    {
        var contentPane = FindAncestor(tree, d => IsType(d, ContentPaneTypeName));
        var tabGroup = contentPane != null ? FindAncestor(VisualTreeHelper.GetParent(contentPane), d => IsType(d, TabGroupPaneTypeName) || IsType(d, ContentPaneTypeName)) : null;
        if (tabGroup != null && !IsType(tabGroup, TabGroupPaneTypeName)) tabGroup = null;
        return new EditorTree
        {
            Tree = tree,
            Window = window,
            ContentPane = contentPane,
            TabGroupPane = tabGroup,
            Title = contentPane != null ? HeaderText(contentPane) : tree.Name,
        };
    }

    /// <summary>The dock pane hosting <paramref name="element"/> (e.g. the plugin's own panel).</summary>
    public static FrameworkElement? FindContentPane(DependencyObject element) =>
        FindAncestor(element, d => IsType(d, ContentPaneTypeName));

    /// <summary>Brings a dock pane to front (Infragistics ContentPane.Activate), via reflection.</summary>
    public static bool TryActivatePane(FrameworkElement? pane)
    {
        var activate = pane?.GetType().GetMethod("Activate", Type.EmptyTypes);
        if (activate == null) return false;
        try { activate.Invoke(pane, null); return true; }
        catch { return false; }
    }

    public static bool IsTree(DependencyObject d) => IsType(d, TreeTypeName);

    private static bool IsType(DependencyObject d, string fullName)
    {
        for (var t = d.GetType(); t != null; t = t.BaseType)
            if (t.FullName == fullName) return true;
        return false;
    }

    private static string HeaderText(FrameworkElement pane)
    {
        var header = pane.GetType().GetProperty("Header")?.GetValue(pane);
        return header switch
        {
            null => "",
            string s => s,
            DependencyObject d => string.Join(" ", FindDescendants(d, x => x is TextBlock).Cast<TextBlock>().Select(tb => tb.Text).Where(s => !string.IsNullOrWhiteSpace(s))),
            _ => header.ToString() ?? "",
        };
    }

    private static FrameworkElement? FindAncestor(DependencyObject? start, Func<DependencyObject, bool> match)
    {
        for (var d = start; d != null; d = Parent(d))
            if (d is FrameworkElement fe && match(d)) return fe;
        return null;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    private static IEnumerable<FrameworkElement> FindDescendants(DependencyObject root, Func<DependencyObject, bool> match)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d != root && d is FrameworkElement fe && match(d)) { yield return fe; continue; }
            if (d is not (Visual or System.Windows.Media.Media3D.Visual3D)) continue;
            var n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = n - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    /// <summary>Ancestor chain and pane subtree, to understand the editor's layout from a log file.</summary>
    public static string Describe(EditorTree t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Tree: {t.Title}");
        sb.AppendLine("Ancestors (tree -> window):");
        for (var d = (DependencyObject?)t.Tree; d != null; d = Parent(d))
        {
            var fe = d as FrameworkElement;
            sb.AppendLine($"  {d.GetType().FullName}  name='{fe?.Name}'  size={fe?.ActualWidth:0}x{fe?.ActualHeight:0}");
        }
        var top = (DependencyObject?)t.TabGroupPane ?? t.ContentPane;
        if (top != null)
        {
            sb.AppendLine("Pane subtree (until tree):");
            DumpSubtree(sb, top, 1, 14);
        }
        return sb.ToString();
    }

    private static void DumpSubtree(StringBuilder sb, DependencyObject d, int depth, int maxDepth)
    {
        var fe = d as FrameworkElement;
        var extra = d is TextBlock tb ? $" text='{tb.Text}'" : "";
        sb.AppendLine($"{new string(' ', depth * 2)}{d.GetType().Name} name='{fe?.Name}' size={fe?.ActualWidth:0}x{fe?.ActualHeight:0} vis={fe?.Visibility}{extra}");
        if (IsTree(d) || depth >= maxDepth || d is not Visual) return;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            DumpSubtree(sb, VisualTreeHelper.GetChild(d, i), depth + 1, maxDepth);
    }
}
