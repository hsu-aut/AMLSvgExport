using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aml.Editor.Plugin.SvgExport;

/// <summary>Which part of a tree's panel the tree export writes.</summary>
public enum TreeVariant
{
    /// <summary>Tab strip, toolbar, complete tree and everything below it (e.g. attribute details).</summary>
    PaneWithTabs,
    /// <summary>Toolbar, complete tree and everything below it.</summary>
    PaneContent,
    /// <summary>Only the complete tree.</summary>
    TreeOnly,
    /// <summary>All three as separate files, plus a diagnostics file.</summary>
    All,
}

/// <summary>
/// Options of the plugin, kept across editor restarts. Stored next to the editor's own
/// plugin data, not in the plugin folder, which is replaced on every update.
/// </summary>
public sealed class PluginSettings
{
    public bool AlsoPdf { get; set; }
    public bool AlsoPng { get; set; }
    public bool CopyToClipboard { get; set; }
    public bool SuppressHover { get; set; } = true;
    public bool CropToContent { get; set; }
    public bool DiagramsAsVector { get; set; } = true;
    public bool TextAsPaths { get; set; }
    public bool HideSelection { get; set; }
    /// <summary>Export tree takes only the part of the tree currently in view, without enlarging it.</summary>
    public bool VisibleTreeOnly { get; set; }
    public bool WarnCutText { get; set; } = true;
    public TreeVariant TreeVariant { get; set; } = TreeVariant.PaneContent;
    public string? LastFolder { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutomationMLEditor", "SvgExport", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads the settings; a missing or unreadable file gives the defaults.</summary>
    public static PluginSettings Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (File.Exists(file))
                return JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(file), Json) ?? new PluginSettings();
        }
        catch
        {
            // Corrupt or foreign file: fall back to defaults rather than breaking the plugin.
        }
        return new PluginSettings();
    }

    public void Save(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // Settings are a convenience; a read-only profile must not break exports.
        }
    }
}
