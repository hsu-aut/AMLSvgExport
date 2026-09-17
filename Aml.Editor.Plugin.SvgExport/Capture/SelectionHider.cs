using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Aml.Editor.Plugin.SvgExport.Capture;

/// <summary>
/// Removes the selection highlight of tree views for a capture by unselecting the selected
/// row, and selects it again afterwards. This is a real selection change: panels that follow
/// the selection (e.g. the editor's attribute details) update accordingly.
/// </summary>
public sealed class SelectionHider
{
    private readonly List<(TreeView Tree, object Item)> _hidden = new();

    public int Count => _hidden.Count;

    public static async Task<SelectionHider> HideAsync(IEnumerable<DependencyObject> roots)
    {
        var hider = new SelectionHider();
        foreach (var tree in roots.SelectMany(TreeViews).Distinct())
        {
            if (tree.SelectedItem is not { } item) continue;
            var container = FindContainer(tree, item);
            if (container == null) continue;
            container.IsSelected = false;
            hider._hidden.Add((tree, item));
        }
        if (hider._hidden.Count > 0)
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        return hider;
    }

    /// <summary>Selects the rows again. Call after scroll position and virtualization are restored.</summary>
    public async Task RestoreAsync()
    {
        foreach (var (tree, item) in _hidden)
        {
            tree.UpdateLayout();
            var container = FindContainer(tree, item);
            if (container != null) container.IsSelected = true;
        }
        if (_hidden.Count > 0)
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        _hidden.Clear();
    }

    private static IEnumerable<TreeView> TreeViews(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is TreeView tv) { yield return tv; continue; }
            if (d is not Visual) continue;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    /// <summary>The realized container of <paramref name="item"/> at any depth.</summary>
    private static TreeViewItem? FindContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct) return direct;
        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is TreeViewItem child && child.IsExpanded
                && FindContainer(child, item) is { } found)
                return found;
        }
        return null;
    }
}
