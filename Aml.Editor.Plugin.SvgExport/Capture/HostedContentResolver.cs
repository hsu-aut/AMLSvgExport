using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Aml.Editor.Plugin.SvgExport.Capture.SvgPathData;

namespace Aml.Editor.Plugin.SvgExport.Capture;

public enum HostedContentMode
{
    /// <summary>Vector content from the host where available (SVG in a WebView2 page), else an image.</summary>
    PreferVector,
    /// <summary>Always an image of the host area.</summary>
    Image,
}

/// <summary>
/// Fills the placeholders that <see cref="VisualSvgWriter"/> left for natively rendered areas.
/// Works for any plugin: a WebView2 (found by duck typing, the control belongs to another
/// plugin's load context) is asked for the SVG elements of its page or for a PNG of itself;
/// every other native window is copied from the screen.
/// </summary>
public static class HostedContentResolver
{
    public static async Task<string> ResolveAsync(string svg, IReadOnlyList<HostedContent> hosts, HostedContentMode mode, StringBuilder log)
    {
        foreach (var host in hosts)
        {
            string replacement;
            try
            {
                replacement = await RenderAsync(host, mode, log);
            }
            catch (Exception ex)
            {
                log.AppendLine($"Hosted content {host.Id} ({host.Host.GetType().Name}): failed, {ex.Message}");
                replacement = "";
            }
            svg = svg.Replace(host.Placeholder, replacement);
        }
        return svg;
    }

    private static async Task<string> RenderAsync(HostedContent host, HostedContentMode mode, StringBuilder log)
    {
        var typeName = host.Host.GetType().Name;
        var r = host.OutputRect;

        if (IsWebView(host.Host))
        {
            if (mode == HostedContentMode.PreferVector)
            {
                var vector = await TryPageSvgAsync(host, log);
                if (vector != null) { log.AppendLine($"Hosted content {host.Id} ({typeName}): SVG from page."); return vector; }
            }
            var png = await TryWebViewPngAsync(host.Host);
            if (png != null) { log.AppendLine($"Hosted content {host.Id} ({typeName}): PNG from WebView2."); return ImageElement(r, png); }
        }

        if (host.Host is HwndHost hwndHost && hwndHost.Handle != IntPtr.Zero)
        {
            var screen = ScreenCopy(hwndHost.Handle);
            if (screen != null) { log.AppendLine($"Hosted content {host.Id} ({typeName}): copied from screen."); return ImageElement(r, screen); }
        }

        log.AppendLine($"Hosted content {host.Id} ({typeName}): no content available, left empty.");
        return "";
    }

    // ── WebView2 via duck typing ────────────────────────────────────────────

    private static bool IsWebView(object host) =>
        host.GetType().GetMethod("ExecuteScriptAsync", new[] { typeof(string) }) != null;

    /// <summary>
    /// Serializes every top-level &lt;svg&gt; of the page with computed styles inlined, so the
    /// result renders identically outside the page's stylesheets.
    /// </summary>
    private const string PageSvgScript = """
        (function () {
          const props = ['fill','fill-opacity','fill-rule','stroke','stroke-width','stroke-dasharray','stroke-dashoffset',
            'stroke-linecap','stroke-linejoin','stroke-opacity','stroke-miterlimit','opacity','font-family','font-size',
            'font-weight','font-style','text-anchor','dominant-baseline','letter-spacing','visibility','display',
            'marker-start','marker-mid','marker-end','stop-color','stop-opacity','color','overflow'];
          const out = [];
          for (const svg of document.querySelectorAll('svg')) {
            if (svg.ownerSVGElement) continue;               // nested: serialized with its root
            const cs = getComputedStyle(svg);
            if (cs.display === 'none' || cs.visibility === 'hidden') continue;
            const rect = svg.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) continue;
            const clone = svg.cloneNode(true);
            const src = [svg, ...svg.querySelectorAll('*')];
            const dst = [clone, ...clone.querySelectorAll('*')];
            for (let i = 0; i < src.length; i++) {
              if (!(src[i] instanceof SVGElement)) continue;
              const s = getComputedStyle(src[i]);
              let style = '';
              for (const p of props) { const v = s.getPropertyValue(p); if (v) style += p + ':' + v + ';'; }
              dst[i].setAttribute('style', style);
              dst[i].removeAttribute('class');
            }
            // Foreign HTML inside the svg cannot be rendered outside the page.
            for (const fo of clone.querySelectorAll('foreignObject')) fo.remove();
            clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
            clone.setAttribute('xmlns:xlink', 'http://www.w3.org/1999/xlink');
            clone.setAttribute('width', rect.width);
            clone.setAttribute('height', rect.height);
            clone.removeAttribute('x'); clone.removeAttribute('y');
            out.push({ x: rect.left, y: rect.top, w: rect.width, h: rect.height, markup: new XMLSerializer().serializeToString(clone) });
          }
          const bg = getComputedStyle(document.body).backgroundColor;
          return JSON.stringify({ background: bg, width: innerWidth, height: innerHeight, svgs: out });
        })()
        """;

    private static async Task<string?> TryPageSvgAsync(HostedContent host, StringBuilder log)
    {
        var method = host.Host.GetType().GetMethod("ExecuteScriptAsync", new[] { typeof(string) })!;
        if (method.Invoke(host.Host, new object[] { PageSvgScript }) is not Task task) return null;
        await task;
        var json = task.GetType().GetProperty("Result")?.GetValue(task) as string;
        if (string.IsNullOrEmpty(json) || json == "null") return null;

        // ExecuteScriptAsync returns the script result JSON-encoded, here a JSON string containing JSON.
        var inner = JsonSerializer.Deserialize<string>(json);
        if (string.IsNullOrEmpty(inner)) return null;
        using var doc = JsonDocument.Parse(inner);
        var root = doc.RootElement;
        var svgs = root.GetProperty("svgs");
        if (svgs.GetArrayLength() == 0) return null;

        var r = host.OutputRect;
        var pageWidth = root.GetProperty("width").GetDouble();
        var pageHeight = root.GetProperty("height").GetDouble();
        // CSS pixels of the page map onto the host area (both are device independent at zoom 100%).
        var sx = pageWidth > 0 ? r.Width / pageWidth : 1;
        var sy = pageHeight > 0 ? r.Height / pageHeight : 1;

        var sb = new StringBuilder();
        var background = CssColorToSvg(root.GetProperty("background").GetString());
        if (background != null)
            sb.Append($"<path d=\"{From(new RectangleGeometry(r)).Data}\" fill=\"{background}\" stroke=\"none\"/>\n");

        var clip = $"<clipPath id=\"hosted{host.Id}\" clipPathUnits=\"userSpaceOnUse\"><path d=\"{From(new RectangleGeometry(r)).Data}\"/></clipPath>";
        sb.Append("<defs>").Append(clip).Append("</defs>\n");
        sb.Append($"<g clip-path=\"url(#hosted{host.Id})\">\n");
        foreach (var item in svgs.EnumerateArray())
        {
            var x = r.X + item.GetProperty("x").GetDouble() * sx;
            var y = r.Y + item.GetProperty("y").GetDouble() * sy;
            var w = item.GetProperty("w").GetDouble() * sx;
            var h = item.GetProperty("h").GetDouble() * sy;
            var markup = item.GetProperty("markup").GetString() ?? "";
            var open = markup.IndexOf('>');
            if (!markup.StartsWith("<svg") || open < 0) continue;
            // Place the page svg as a nested svg at its on-screen position.
            var head = markup[..open];
            head = System.Text.RegularExpressions.Regex.Replace(head, "\\s(width|height)=\"[^\"]*\"", "");
            markup = head + $" x=\"{Format(x)}\" y=\"{Format(y)}\" width=\"{Format(w)}\" height=\"{Format(h)}\"" + markup[open..];
            sb.Append(markup).Append('\n');
        }
        sb.Append("</g>\n");
        log.AppendLine($"Hosted content {host.Id}: {svgs.GetArrayLength()} svg element(s) from page ({pageWidth:0}x{pageHeight:0} css px).");
        return sb.ToString();
    }

    private static async Task<byte[]?> TryWebViewPngAsync(object host)
    {
        var core = host.GetType().GetProperty("CoreWebView2")?.GetValue(host);
        var capture = core?.GetType().GetMethod("CapturePreviewAsync");
        if (core == null || capture == null) return null;
        var formatType = capture.GetParameters()[0].ParameterType;
        var png = Enum.ToObject(formatType, 0);   // CoreWebView2CapturePreviewImageFormat.Png
        using var ms = new MemoryStream();
        if (capture.Invoke(core, new object[] { png, ms }) is not Task task) return null;
        await task;
        return ms.Length > 0 ? ms.ToArray() : null;
    }

    private static string? CssColorToSvg(string? css)
    {
        if (string.IsNullOrWhiteSpace(css) || css == "transparent") return null;
        var m = System.Text.RegularExpressions.Regex.Match(css, @"rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)");
        if (!m.Success) return null;
        if (m.Groups[4].Success && double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) <= 0) return null;
        return $"#{int.Parse(m.Groups[1].Value):x2}{int.Parse(m.Groups[2].Value):x2}{int.Parse(m.Groups[3].Value):x2}";
    }

    // ── Generic: copy the native window from the screen ─────────────────────

    private static byte[]? ScreenCopy(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return null;

        var screen = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(memDc, bitmap);
        try
        {
            if (!BitBlt(memDc, 0, 0, w, h, screen, rect.Left, rect.Top, SRCCOPY)) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static string ImageElement(Rect r, byte[] png) =>
        $"<image x=\"{Format(r.X)}\" y=\"{Format(r.Y)}\" width=\"{Format(r.Width)}\" height=\"{Format(r.Height)}\" preserveAspectRatio=\"none\" xlink:href=\"data:image/png;base64,{Convert.ToBase64String(png)}\"/>\n";

    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
}
