// Exports AutomationML Editor views as vector graphics that look exactly like the editor:
// complete trees (scrolled through), the whole window, or a dragged region.

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Aml.Editor.Plugin.Contracts;
using Aml.Editor.Plugin.SvgExport.Capture;
using Aml.Editor.Plugin.SvgExport.Ui;
using Aml.Editor.Plugin.WPFBase;
using Microsoft.Win32;

namespace Aml.Editor.Plugin.SvgExport;

public partial class SvgExportPlugin : PluginViewBase, IToolBarIntegration
{
    /// <summary>One export action, shown as panel button and as editor toolbar button.</summary>
    private sealed record ExportAction(string Icon, string Label, KeyGesture Gesture, string Description, Action Run);

    /// <summary>
    /// The shortcuts are static class handlers. If the editor creates the plugin again (e.g. when
    /// the panel is closed and reopened), only the newest instance may react, otherwise an export
    /// would run once per instance.
    /// </summary>
    private static SvgExportPlugin? _active;

    private readonly PluginSettings _settings;
    private readonly List<ExportAction> _actions;
    private bool _exporting;
    private bool _treesStale = true;
    private bool _loadingSettings;
    private string? _lastFile;

    public SvgExportPlugin()
    {
        InitializeComponent();
        DisplayName = "SvgExport";   // identifier-only: used as a WPF x:Name / config key
        IsReactive = false;

        _settings = PluginSettings.Load();

        _actions = new List<ExportAction>
        {
            new(Icons.Tree, "Export tree", EditorTreeLocator.ExportGesture,
                "The tree you last clicked into, completely, including rows scrolled out of view.",
                () => ExportTree(EditorTreeLocator.LastUsed?.Tree ?? SelectedTree?.Tree, toClipboardOnly: false)),
            new(Icons.Clipboard, "Copy tree", EditorTreeLocator.CopyTreeGesture,
                "Like Export tree, but straight to the clipboard (SVG for Inkscape, image for PowerPoint).",
                () => ExportTree(EditorTreeLocator.LastUsed?.Tree ?? SelectedTree?.Tree, toClipboardOnly: true)),
            new(Icons.Window, "Export window", EditorTreeLocator.ExportWindowGesture,
                "The whole editor window exactly as shown, like a screenshot but as vectors.",
                () => { if (EditorWindow() is Window w) ExportWindow(w); }),
            new(Icons.Region, "Export region", EditorTreeLocator.ExportRegionGesture,
                "Drag a rectangle over the editor; exactly that area is exported.",
                () => { if (EditorWindow() is Window w) ExportRegion(w); }),
        };

        ToolBarCommands = _actions.Select(a => new PluginCommand
        {
            CommandName = a.Label,
            CommandButtonContent = Icons.WithLabel(a.Icon, a.Label),
            Command = new RelayCommand<object>(_ => a.Run(), _ => !_exporting),
            CommandToolTip = $"{a.Description} ({GestureText(a.Gesture)})",
            IsCheckable = false,
        }).ToList();

        BuildPanel();
        ApplySettingsToPanel();

        _active = this;
        EditorTreeLocator.HookEditorInput();
        // A click into an editor tree makes it the export target; a manual pick in the combo box
        // stays in effect until the next click into a tree. Searching the editor for trees walks
        // the whole window, so it only happens while the panel is visible.
        EditorTreeLocator.TreeUsed += tree =>
        {
            if (_active != this) return;
            if (IsVisible) RefreshTrees(prefer: tree.Tree, log: false);
            else _treesStale = true;
        };
        EditorTreeLocator.ExportRequested += () => { if (_active == this) ExportTree(EditorTreeLocator.LastUsed?.Tree, toClipboardOnly: false); };
        EditorTreeLocator.CopyRequested += () => { if (_active == this) ExportTree(EditorTreeLocator.LastUsed?.Tree, toClipboardOnly: true); };
        EditorTreeLocator.WindowExportRequested += w => { if (_active == this) ExportWindow(w); };
        EditorTreeLocator.RegionExportRequested += w => { if (_active == this) ExportRegion(w); };
        TreeCombo.DropDownOpened += (_, __) => RefreshTrees(prefer: SelectedTree?.Tree, log: false);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _treesStale) RefreshTrees(prefer: EditorTreeLocator.LastUsed?.Tree ?? SelectedTree?.Tree, log: false);
        };
        Loaded += (_, __) =>
        {
            RefreshTrees(prefer: EditorTreeLocator.LastUsed?.Tree);
            if (!EdgeRenderer.IsAvailable)
            {
                foreach (var option in new[] { PdfToggle, PngToggle }) { option.IsEnabled = false; option.ToolTip = "Needs Microsoft Edge, which was not found."; }
                Log("Microsoft Edge not found: PDF and PNG output are unavailable; the clipboard gets SVG text only.");
            }
        };
    }

    public override string PackageName => "Aml.Editor.Plugin.SvgExport";
    // Floating: docked as a tab next to Attributes it hid the attribute panel it is meant to export.
    public override DockPositionEnum InitialDockPosition => DockPositionEnum.Floating;
    public override bool CanClose => true;

    public List<PluginCommand> ToolBarCommands { get; }

    private EditorTree? SelectedTree => TreeCombo.SelectedItem as EditorTree;

    // ── Panel ───────────────────────────────────────────────────────────────

    private void BuildPanel()
    {
        foreach (var action in _actions)
        {
            var label = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            label.Children.Add(new TextBlock { Text = action.Label });
            label.Children.Add(new TextBlock { Text = GestureText(action.Gesture), Opacity = 0.6, FontSize = 11 });

            var content = new DockPanel();
            var icon = Icons.Create(action.Icon, 20);
            DockPanel.SetDock(icon, Dock.Left);
            content.Children.Add(icon);
            content.Children.Add(label);

            var button = new Button
            {
                Content = content,
                Style = (Style)Resources["ActionButton"],
                ToolTip = action.Description,
            };
            button.Click += (_, __) => action.Run();
            ActionGrid.Children.Add(button);
        }

        RefreshButton.Content = Icons.Create(Icons.Refresh, 14);
        OpenFileButton.Content = Icons.WithLabel(Icons.File, "Open");
        ShowInFolderButton.Content = Icons.WithLabel(Icons.Folder, "Show in folder");

        TreeVariantCombo.ItemsSource = Enum.GetValues<TreeVariant>()
            .Select(v => new ComboBoxItem { Content = TreeVariants.Label(v), Tag = v }).ToList();
    }

    private void ApplySettingsToPanel()
    {
        _loadingSettings = true;
        PdfToggle.IsChecked = _settings.AlsoPdf;
        PngToggle.IsChecked = _settings.AlsoPng;
        ClipboardToggle.IsChecked = _settings.CopyToClipboard;
        SuppressHoverToggle.IsChecked = _settings.SuppressHover;
        CropToggle.IsChecked = _settings.CropToContent;
        DiagramsAsVectorToggle.IsChecked = _settings.DiagramsAsVector;
        TextAsPathsToggle.IsChecked = _settings.TextAsPaths;
        HideSelectionToggle.IsChecked = _settings.HideSelection;
        VisibleTreeOnlyToggle.IsChecked = _settings.VisibleTreeOnly;
        WarnCutTextToggle.IsChecked = _settings.WarnCutText;
        TreeVariantCombo.SelectedItem = TreeVariantCombo.Items.Cast<ComboBoxItem>().First(i => (TreeVariant)i.Tag == _settings.TreeVariant);
        _loadingSettings = false;
    }

    private void Option_Changed(object sender, RoutedEventArgs e) => SaveSettingsFromPanel();

    private void TreeVariantCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveSettingsFromPanel();

    private void SaveSettingsFromPanel()
    {
        if (_loadingSettings) return;
        _settings.AlsoPdf = PdfToggle.IsChecked == true;
        _settings.AlsoPng = PngToggle.IsChecked == true;
        _settings.CopyToClipboard = ClipboardToggle.IsChecked == true;
        _settings.SuppressHover = SuppressHoverToggle.IsChecked == true;
        _settings.CropToContent = CropToggle.IsChecked == true;
        _settings.DiagramsAsVector = DiagramsAsVectorToggle.IsChecked == true;
        _settings.TextAsPaths = TextAsPathsToggle.IsChecked == true;
        _settings.HideSelection = HideSelectionToggle.IsChecked == true;
        _settings.VisibleTreeOnly = VisibleTreeOnlyToggle.IsChecked == true;
        _settings.WarnCutText = WarnCutTextToggle.IsChecked == true;
        if (TreeVariantCombo.SelectedItem is ComboBoxItem { Tag: TreeVariant v }) _settings.TreeVariant = v;
        _settings.Save();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshTrees(prefer: SelectedTree?.Tree);

    private void OpenFileButton_Click(object sender, RoutedEventArgs e) { if (_lastFile != null) Toast.Open(_lastFile); }

    private void ShowInFolderButton_Click(object sender, RoutedEventArgs e) { if (_lastFile != null) Toast.ShowInFolder(_lastFile); }

    private Window? EditorWindow() => Application.Current?.MainWindow ?? Window.GetWindow(this);

    private static string GestureText(KeyGesture g) =>
        g.GetDisplayStringForCulture(System.Globalization.CultureInfo.CurrentUICulture) is { Length: > 0 } s ? s : $"Ctrl+Shift+{g.Key}";

    // ── Feedback ────────────────────────────────────────────────────────────

    private void SetBusy(string? message)
    {
        _exporting = message != null;
        ActionGrid.IsEnabled = !_exporting;
        CommandManager.InvalidateRequerySuggested();
        if (message == null) return;

        ResultPanel.Visibility = Visibility.Visible;
        ResultIcon.Content = Icons.Create(Icons.Refresh, 18);
        ResultTitle.Text = message;
        ResultDetail.Text = "";
        ResultButtons.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows the result in the panel, or as a notification if the panel is not visible.</summary>
    private void ShowResult(string title, string? detail, string? file, bool isError, Window? editor, bool isWarning = false)
    {
        _lastFile = file;
        ResultPanel.Visibility = Visibility.Visible;
        ResultIcon.Content = Icons.Create(isError || isWarning ? Icons.Warning : Icons.Check, 18);
        ResultTitle.Text = title;
        ResultDetail.Text = detail ?? "";
        ResultDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        ResultButtons.Visibility = file != null && File.Exists(file) ? Visibility.Visible : Visibility.Collapsed;

        if (!IsVisible && editor != null)
            Toast.Show(editor, title, detail, file != null && File.Exists(file) ? file : null, isError || isWarning);
    }

    private void Fail(string what, Exception ex, Window? editor)
    {
        Log($"{what} failed: {ex}");
        ShowResult($"{what} failed", ex.Message + " Details in the activity log.", null, isError: true, editor);
    }

    /// <summary>Adds a note about text the editor shows only partly; returns whether there was any.</summary>
    private bool AppendCutTextNote(List<string> cut, ref string? detail)
    {
        if (cut.Count == 0) return false;
        var sample = string.Join(", ", cut.Take(3).Select(t => t.Length > 40 ? "'" + t[..37] + "...'" : "'" + t + "'"));
        detail = (detail == null ? "" : detail + " ")
                 + $"{cut.Count} text(s) are cut off in the editor, e.g. {sample}. Widen the panel and export again.";
        foreach (var t in cut) Log("  cut off: " + t);
        return true;
    }

    // ── Options ─────────────────────────────────────────────────────────────

    private SvgCaptureOptions CaptureOptions() => new() { TextAsPaths = _settings.TextAsPaths };

    private HostedContentMode HostedMode() => _settings.DiagramsAsVector ? HostedContentMode.PreferVector : HostedContentMode.Image;

    // ── Window and region ───────────────────────────────────────────────────

    /// <summary>Exports the window exactly as shown: no scrolling, hidden tabs stay hidden.</summary>
    private async void ExportWindow(Window window)
    {
        if (_exporting) return;
        try
        {
            var path = AskForFile(SafeFileName(string.IsNullOrWhiteSpace(window.Title) ? "AMLEditor" : window.Title) + "_window");
            if (path == null) return;
            SetBusy("Exporting window…");
            await SettleAsync();
            var (svg, width, height, report) = await CaptureAreaAsync(window, new Rect(window.RenderSize));
            await FinishAsync(path, svg, width, height, report, "Window", window);
        }
        catch (Exception ex)
        {
            Fail("Window export", ex, window);
        }
        finally
        {
            SetBusy(null);
        }
    }

    /// <summary>Lets the user drag a rectangle over the window and exports that area.</summary>
    private async void ExportRegion(Window window)
    {
        if (_exporting) return;
        try
        {
            SetBusy("Drag a rectangle over the editor…");
            var region = await RegionPicker.PickAsync(window);
            if (region == null) { ShowResult("Region export cancelled", null, null, isError: false, editor: null); return; }

            // Capture before the save dialog so the picked view is not disturbed; the overlay
            // just closed, so let the window repaint first.
            SetBusy("Exporting region…");
            await SettleAsync();
            var (svg, width, height, report) = await CaptureAreaAsync(window, region.Value);
            var path = AskForFile(SafeFileName(string.IsNullOrWhiteSpace(window.Title) ? "AMLEditor" : window.Title) + "_region");
            if (path == null) { ShowResult("Region export cancelled", null, null, isError: false, editor: null); return; }
            await FinishAsync(path, svg, width, height, report, "Region", window);
        }
        catch (Exception ex)
        {
            Fail("Region export", ex, window);
        }
        finally
        {
            SetBusy(null);
        }
    }

    private List<string> _lastCutText = new();

    private async Task<(string Svg, double Width, double Height, StringBuilder Report)> CaptureAreaAsync(Window window, Rect area)
    {
        var report = new StringBuilder();
        var writer = new VisualSvgWriter(CaptureOptions());
        _lastCutText = _settings.WarnCutText ? CutTextDetector.Find(new[] { (FrameworkElement)window }, window, area) : new List<string>();
        var selection = _settings.HideSelection ? await SelectionHider.HideAsync(new[] { window }) : null;
        try
        {
            using (_settings.SuppressHover ? await HoverSuppression.BeginAsync(window) : null)
            {
                writer.Begin();
                writer.AddArea(window, area, new Point(0, 0));
                var (w, h) = _settings.CropToContent ? writer.CropToContent(area.Width, area.Height) : (area.Width, area.Height);
                var svg = await HostedContentResolver.ResolveAsync(writer.End(w, h), writer.HostedContents, HostedMode(), report);
                report.AppendLine($"Area {area.Width:0}x{area.Height:0}, output {w:0}x{h:0}, unsupported: {Unsupported(writer)}");
                return (svg, w, h, report);
            }
        }
        finally
        {
            if (selection != null) await selection.RestoreAsync();
        }
    }

    // ── Trees ───────────────────────────────────────────────────────────────

    /// <param name="preferTree">Tree to export; null uses the combo box selection.</param>
    private async void ExportTree(FrameworkElement? preferTree, bool toClipboardOnly)
    {
        if (_exporting) return;
        FrameworkElement? paneToRestore = null;
        Window? editor = EditorWindow();
        try
        {
            RefreshTrees(prefer: preferTree ?? SelectedTree?.Tree, log: false);
            var target = SelectedTree;
            if (target == null)
            {
                ShowResult("No tree to export", "Click into a tree in the editor first.", null, isError: true, editor);
                return;
            }
            editor = target.Window;

            string? basePath = null;
            if (!toClipboardOnly)
            {
                var chosen = AskForFile(SafeFileName(string.IsNullOrWhiteSpace(target.Title) ? "tree" : target.Title));
                if (chosen == null) return;
                basePath = Path.Combine(Path.GetDirectoryName(chosen)!, Path.GetFileNameWithoutExtension(chosen));
            }
            SetBusy($"Exporting '{target.Title}'…");
            await SettleAsync();

            // Only rendered visuals can be vectorized: bring a hidden panel (e.g. a tab behind this
            // plugin's own panel) to front for the export and switch back afterwards.
            if (!target.Tree.IsVisible)
            {
                var ownPane = IsVisible ? EditorTreeLocator.FindContentPane(this) : null;
                if (!EditorTreeLocator.TryActivatePane(target.ContentPane) || !await WaitUntilRenderedAsync(target.Tree))
                {
                    ShowResult($"'{target.Title}' is hidden", "Its panel could not be brought to front. Show the panel and export again.", null, isError: true, editor);
                    return;
                }
                paneToRestore = ownPane;
                target = EditorTreeLocator.FindTrees().FirstOrDefault(t => ReferenceEquals(t.Tree, target.Tree)) ?? target;
            }

            var report = new StringBuilder();
            report.AppendLine(EditorTreeLocator.Describe(target));

            var variant = toClipboardOnly && _settings.TreeVariant == TreeVariant.All ? TreeVariant.PaneContent : _settings.TreeVariant;
            var frames = TreeVariants.Frames(target.Tree, target.ContentPane, target.TabGroupPane, variant, out var note);
            if (note != null) report.AppendLine(note);

            var cutText = _settings.WarnCutText ? CutTextDetector.Find(frames.Select(f => f.Frame)) : new List<string>();
            var selection = _settings.HideSelection ? await SelectionHider.HideAsync(new[] { target.Tree }) : null;

            List<string> svgs;
            try
            {
            using (_settings.SuppressHover ? await HoverSuppression.BeginAsync(target.Window) : null)
            {
                var scrollViewer = _settings.VisibleTreeOnly ? null : ScrollingCapture.FindTreeScrollViewer(target.Tree);
                if (scrollViewer == null)
                {
                    report.AppendLine(_settings.VisibleTreeOnly
                        ? "Only the visible part of the tree (option)."
                        : "No scroll viewer found, exported the visible area only.");
                    svgs = new List<string>();
                    foreach (var (frame, _) in frames)
                    {
                        var writer = new VisualSvgWriter(CaptureOptions());
                        var bounds = frame.TransformToAncestor(target.Window).TransformBounds(new Rect(frame.RenderSize));
                        writer.Begin();
                        writer.AddArea(target.Window, bounds, new Point(0, 0));
                        var (w, h) = _settings.CropToContent ? writer.CropToContent(bounds.Width, bounds.Height) : (bounds.Width, bounds.Height);
                        svgs.Add(await HostedContentResolver.ResolveAsync(writer.End(w, h), writer.HostedContents, HostedMode(), report));
                    }
                }
                else
                {
                    var title = target.Title;
                    var progress = new Progress<int>(round => ResultTitle.Text = $"Exporting '{title}'… laying out all rows ({round})");
                    svgs = await ScrollingCapture.CaptureAsync(target.Window, scrollViewer, frames.Select(f => f.Frame).ToList(),
                        CaptureOptions(), report, HostedMode(), _settings.CropToContent, progress);
                }
            }
            }
            finally
            {
                // After the scroll position and virtualization are restored, so the row is realized again.
                if (selection != null) await selection.RestoreAsync();
            }

            if (toClipboardOnly)
            {
                var (w, h) = SvgSize(svgs[0]);
                LogReport(report);
                if (await CopyToClipboardAsync(svgs[0], w, h))
                {
                    string? copyDetail = note ?? "Paste into Inkscape (vector) or PowerPoint (image).";
                    var copyCut = AppendCutTextNote(cutText, ref copyDetail);
                    ShowResult($"Copied '{target.Title}' to the clipboard", copyDetail, null, isError: false, editor, isWarning: copyCut);
                }
                else
                    ShowResult("Clipboard is busy", "Another program is using the clipboard. Try again.", null, isError: true, editor);
                return;
            }

            string? lastFile = null;
            for (int i = 0; i < frames.Count; i++)
            {
                var file = basePath + frames[i].Suffix + ".svg";
                var (w, h) = SvgSize(svgs[i]);
                await WriteOutputsAsync(file, svgs[i], w, h, copy: i == frames.Count - 1);
                lastFile = file;
            }
            if (_settings.TreeVariant == TreeVariant.All)
                File.WriteAllText(basePath + "_diagnostics.txt", report.ToString());
            LogReport(report);

            string? detail = note == null ? OutputSummary(lastFile!, frames.Count) : note + " " + OutputSummary(lastFile!, frames.Count);
            var treeCut = AppendCutTextNote(cutText, ref detail);
            ShowResult($"Exported '{target.Title}'", detail, lastFile, isError: false, editor, isWarning: treeCut);
        }
        catch (Exception ex)
        {
            Fail("Tree export", ex, editor);
        }
        finally
        {
            if (paneToRestore != null) EditorTreeLocator.TryActivatePane(paneToRestore);
            SetBusy(null);
        }
    }

    // ── Outputs ─────────────────────────────────────────────────────────────

    private async Task FinishAsync(string svgPath, string svg, double width, double height, StringBuilder report, string what, Window editor)
    {
        await WriteOutputsAsync(svgPath, svg, width, height, copy: true);
        LogReport(report);
        string? detail = OutputSummary(svgPath, 1);
        var cut = AppendCutTextNote(_lastCutText, ref detail);
        ShowResult($"{what} exported", detail, svgPath, isError: false, editor, isWarning: cut);
    }

    /// <summary>Writes the SVG and, as configured, PDF, PNG and clipboard.</summary>
    private async Task WriteOutputsAsync(string svgPath, string svg, double width, double height, bool copy)
    {
        File.WriteAllText(svgPath, svg);
        if (_settings.AlsoPdf && EdgeRenderer.IsAvailable
            && !await EdgeRenderer.ToPdfAsync(svgPath, Path.ChangeExtension(svgPath, ".pdf"), width, height))
            Log($"PDF could not be created for {Path.GetFileName(svgPath)}.");
        if (_settings.AlsoPng && EdgeRenderer.IsAvailable
            && !await EdgeRenderer.ToPngAsync(svgPath, Path.ChangeExtension(svgPath, ".png"), width, height))
            Log($"PNG could not be created for {Path.GetFileName(svgPath)}.");
        if (copy && _settings.CopyToClipboard && !await CopyToClipboardAsync(svg, width, height))
            Log("Clipboard is busy (used by another program); the files were written.");
        Log($"Wrote {svgPath}");
    }

    private string OutputSummary(string file, int svgCount)
    {
        var formats = new List<string> { svgCount > 1 ? $"{svgCount} SVG files" : "SVG" };
        if (_settings.AlsoPdf && EdgeRenderer.IsAvailable) formats.Add("PDF");
        if (_settings.AlsoPng && EdgeRenderer.IsAvailable) formats.Add("PNG");
        if (_settings.CopyToClipboard) formats.Add("clipboard");
        return $"{Path.GetFileName(file)} ({string.Join(", ", formats)})";
    }

    /// <summary>SVG text plus, when Edge is available, a PNG image, so both Inkscape and PowerPoint can paste.</summary>
    /// <returns>False if the clipboard stayed locked by another program.</returns>
    private static async Task<bool> CopyToClipboardAsync(string svg, double width, double height)
    {
        var data = new DataObject();
        data.SetText(svg);
        data.SetData("image/svg+xml", new MemoryStream(Encoding.UTF8.GetBytes(svg)));

        if (EdgeRenderer.IsAvailable)
        {
            var temp = Path.Combine(Path.GetTempPath(), "svgexport_clip_" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(temp + ".svg", svg);
                if (await EdgeRenderer.ToPngAsync(temp + ".svg", temp + ".png", width, height))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(temp + ".png");
                    image.EndInit();
                    image.Freeze();
                    data.SetImage(image);
                }
            }
            finally
            {
                try { File.Delete(temp + ".svg"); File.Delete(temp + ".png"); } catch { /* best effort */ }
            }
        }

        // The clipboard is a shared resource; clipboard managers or remote desktop hold it briefly.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                await Task.Delay(100);
            }
        }
        return false;
    }

    private static (double Width, double Height) SvgSize(string svg)
    {
        var m = Regex.Match(svg, "<svg[^>]*\\swidth=\"([\\d.]+)\"[^>]*\\sheight=\"([\\d.]+)\"");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return m.Success ? (double.Parse(m.Groups[1].Value, inv), double.Parse(m.Groups[2].Value, inv)) : (0, 0);
    }

    private static string Unsupported(VisualSvgWriter writer) =>
        writer.Unsupported.Count == 0 ? "none" : string.Join(", ", writer.Unsupported.Select(kv => $"{kv.Key}={kv.Value}"));

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Lets the window repaint after a dialog or overlay covered it (native areas are copied from screen).</summary>
    private async Task SettleAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(150);
    }

    /// <summary>Shows the save dialog in the last used folder; returns the chosen path or null.</summary>
    private string? AskForFile(string suggestedName)
    {
        var folder = _settings.LastFolder;
        var dialog = new SaveFileDialog
        {
            Title = "Export as SVG",
            Filter = "SVG (*.svg)|*.svg",
            FileName = suggestedName + ".svg",
            InitialDirectory = folder != null && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true) return null;
        _settings.LastFolder = Path.GetDirectoryName(dialog.FileName);
        _settings.Save();
        return dialog.FileName;
    }

    /// <summary>Re-reads the editor trees (panels open and close, titles change with the selection).</summary>
    private List<EditorTree> RefreshTrees(FrameworkElement? prefer, bool log = true)
    {
        try
        {
            var trees = EditorTreeLocator.FindTrees();
            _treesStale = false;
            TreeCombo.ItemsSource = trees;
            TreeCombo.SelectedItem = trees.FirstOrDefault(t => ReferenceEquals(t.Tree, prefer))
                                     ?? trees.FirstOrDefault(t => ReferenceEquals(t.Tree, EditorTreeLocator.LastUsed?.Tree))
                                     ?? trees.FirstOrDefault();
            if (log) Log($"Found {trees.Count} tree(s): {string.Join(", ", trees)}");
            return trees;
        }
        catch (Exception ex)
        {
            Log("Tree search failed: " + ex);
            return new List<EditorTree>();
        }
    }

    private static async Task<bool> WaitUntilRenderedAsync(FrameworkElement element)
    {
        for (int i = 0; i < 40; i++)
        {
            await element.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (element.IsVisible && element.ActualHeight > 0) return true;
            await Task.Delay(25);
        }
        return false;
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = Regex.Replace(name, "_{2,}", "_").Replace(" _ ", "_").Trim(' ', '_');
        return name.Length > 0 ? name : "export";
    }

    private void LogReport(StringBuilder report)
    {
        foreach (var line in report.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                     .Where(l => l.StartsWith("Scroll") || l.StartsWith("Frame") || l.StartsWith("Area") || l.StartsWith("Hosted") || l.StartsWith("This panel") || l.StartsWith("No ")))
            Log("  " + line);
    }

    private void Log(string line)
    {
        StatusLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        StatusLog.ScrollToEnd();
    }
}
