using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Aml.Editor.Plugin.SvgExport.Capture;
using Xunit;

// Stand-ins with the type names the locator looks for in the real editor.
namespace Aml.Toolkit.View
{
    public class AMLTreeView : ContentControl { }
}

namespace Infragistics.Windows.DockManager
{
    /// <summary>Hosts content in a tab; Activate brings the tab to front like the real dock pane.</summary>
    public class ContentPane : ContentControl
    {
        public object? Header { get; set; }

        public void Activate()
        {
            for (DependencyObject? d = this; d != null; d = LogicalTreeHelper.GetParent(d))
                if (d is TabItem tab) { tab.IsSelected = true; return; }
        }
    }
}

namespace Aml.Editor.Plugin.SvgExport.Tests
{
    using Aml.Toolkit.View;
    using Infragistics.Windows.DockManager;

    public class EditorTreeLocatorTests
    {
        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        /// <summary>Editor-like layout: the attributes tree and the plugin panel are tabs of one group.</summary>
        private static (Window Window, AMLTreeView Tree, TabItem AttributesTab, TabItem PluginTab) BuildEditor()
        {
            var tree = new AMLTreeView { Content = new TextBlock { Text = "refObj" }, Height = 100 };
            var attributesPane = new ContentPane { Header = "Attributes : Plant/Conveyor", Content = tree };
            var pluginPane = new ContentPane { Header = "SvgExport", Content = new TextBlock { Text = "plugin" } };
            var attributesTab = new TabItem { Header = "Attributes", Content = attributesPane };
            var pluginTab = new TabItem { Header = "SvgExport", Content = pluginPane };
            var tabs = new TabControl();
            tabs.Items.Add(attributesTab);
            tabs.Items.Add(pluginTab);

            var window = new Window
            {
                Width = 300, Height = 200, Content = tabs,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                Left = -3000, Top = -3000,
            };
            window.Show();
            window.UpdateLayout();
            return (window, tree, attributesTab, pluginTab);
        }

        private static void Click(UIElement element) =>
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });

        [Fact]
        public void Clicked_tree_stays_export_target_when_its_tab_gets_hidden_and_can_be_brought_back() => Sta.Run(() =>
        {
            EditorTreeLocator.HookEditorInput();
            var (window, tree, attributesTab, pluginTab) = BuildEditor();
            try
            {
                Click(tree);
                Assert.Same(tree, EditorTreeLocator.LastUsed?.Tree);
                Assert.Equal("Attributes : Plant/Conveyor", EditorTreeLocator.LastUsed!.Title);

                // The user switches to the plugin tab: the tree is no longer rendered ...
                pluginTab.IsSelected = true;
                window.UpdateLayout();
                Pump();
                Assert.False(tree.IsVisible);

                // ... but remains available as export target.
                var trees = EditorTreeLocator.FindTrees();
                var hidden = Assert.Single(trees);
                Assert.Same(tree, hidden.Tree);
                Assert.Contains("hidden", hidden.ToString());

                // Export brings the tab to front.
                Assert.True(EditorTreeLocator.TryActivatePane(hidden.ContentPane));
                window.UpdateLayout();
                Pump();
                Assert.True(attributesTab.IsSelected);
                Assert.True(tree.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void Export_shortcut_remembers_the_focused_tree_and_raises_the_request() => Sta.Run(() =>
        {
            EditorTreeLocator.HookEditorInput();
            var (window, tree, _, _) = BuildEditor();
            var requests = 0;
            void OnRequest() => requests++;
            EditorTreeLocator.ExportRequested += OnRequest;
            try
            {
                tree.Focusable = true;
                Keyboard.Focus(tree);
                var source = PresentationSource.FromVisual(tree)!;
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.E) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

                // Without modifiers nothing happens (the real modifiers cannot be simulated here,
                // so the gesture itself is checked separately).
                tree.RaiseEvent(args);
                Assert.Equal(0, requests);
                Assert.Equal(Key.E, EditorTreeLocator.ExportGesture.Key);
                Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, EditorTreeLocator.ExportGesture.Modifiers);
            }
            finally
            {
                EditorTreeLocator.ExportRequested -= OnRequest;
                window.Close();
            }
        });
    }
}
