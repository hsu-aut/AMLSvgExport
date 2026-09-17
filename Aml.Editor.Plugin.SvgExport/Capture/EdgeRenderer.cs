using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Aml.Editor.Plugin.SvgExport.Capture;

/// <summary>
/// Renders an SVG file to PDF or PNG with the Microsoft Edge that ships with Windows,
/// run headless. Saves the detour through Inkscape for LaTeX (PDF) and slides (PNG).
/// </summary>
public static class EdgeRenderer
{
    private static readonly string[] Candidates =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe"),
    };

    public static string? EdgePath => Candidates.FirstOrDefault(File.Exists);

    public static bool IsAvailable => EdgePath != null;

    /// <summary>Page size equals the SVG size (96 dpi user units), no margins, no header/footer.</summary>
    public static Task<bool> ToPdfAsync(string svgPath, string pdfPath, double width, double height)
    {
        var html = WrapperHtml(svgPath, width, height);
        return RunAsync(html, $"--print-to-pdf=\"{pdfPath}\" --no-pdf-header-footer", pdfPath);
    }

    /// <summary><paramref name="scale"/> 2 gives twice the pixel size of the SVG's user units.</summary>
    public static Task<bool> ToPngAsync(string svgPath, string pngPath, double width, double height, double scale = 2)
    {
        var html = WrapperHtml(svgPath, width, height);
        var w = (int)Math.Ceiling(width); var h = (int)Math.Ceiling(height);
        return RunAsync(html, $"--screenshot=\"{pngPath}\" --window-size={w},{h} --force-device-scale-factor={scale.ToString(CultureInfo.InvariantCulture)} --hide-scrollbars", pngPath);
    }

    private static string WrapperHtml(string svgPath, double width, double height)
    {
        var inv = CultureInfo.InvariantCulture;
        var w = width.ToString("0.###", inv); var h = height.ToString("0.###", inv);
        var uri = new Uri(svgPath).AbsoluteUri;
        var html = Path.Combine(Path.GetTempPath(), "svgexport_" + Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(html,
            $"<!doctype html><html><head><meta charset=\"utf-8\"><style>@page{{size:{w}px {h}px;margin:0}}html,body{{margin:0;padding:0;background:transparent}}img{{display:block;width:{w}px;height:{h}px}}</style></head>" +
            $"<body><img src=\"{uri}\"></body></html>");
        return html;
    }

    private static async Task<bool> RunAsync(string htmlPath, string arguments, string expectedOutput)
    {
        var edge = EdgePath;
        if (edge == null) return false;
        var profile = Path.Combine(Path.GetTempPath(), "svgexport_edge_profile");
        var psi = new ProcessStartInfo(edge,
            $"--headless --disable-gpu --no-first-run --no-default-browser-check --user-data-dir=\"{profile}\" {arguments} \"{new Uri(htmlPath).AbsoluteUri}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process == null) return false;
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(60)));
            if (!process.HasExited) { try { process.Kill(); } catch { /* best effort */ } }
            return File.Exists(expectedOutput) && new FileInfo(expectedOutput).Length > 0;
        }
        finally
        {
            try { File.Delete(htmlPath); } catch { /* best effort */ }
        }
    }
}
