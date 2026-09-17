using System.Text;
using System.Text.Json;
using System.Windows;
using System.Xml.Linq;
using Aml.Editor.Plugin.SvgExport.Capture;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

public class HostedContentResolverTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>Duck-typed stand-in for a WebView2 control of another plugin (found by method name).</summary>
    private sealed class FakeWebView : FrameworkElement
    {
        public string? LastScript;
        public string Result = "null";

        public Task<string> ExecuteScriptAsync(string script)
        {
            LastScript = script;
            return Task.FromResult(Result);
        }
    }

    private static string PageResult(params (double x, double y, double w, double h, string markup)[] svgs)
    {
        var inner = JsonSerializer.Serialize(new
        {
            background = "rgb(255, 255, 255)",
            width = 400,
            height = 300,
            svgs = svgs.Select(s => new { s.x, s.y, s.w, s.h, s.markup }).ToArray(),
        });
        return JsonSerializer.Serialize(inner);   // ExecuteScriptAsync JSON-encodes the script's string result
    }

    [Fact]
    public void Page_svg_is_embedded_at_its_position_inside_the_host_area() => Sta.Run(() =>
    {
        var view = new FakeWebView
        {
            Result = PageResult((50, 20, 300, 200, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"300\" height=\"200\"><circle cx=\"10\" cy=\"10\" r=\"5\" style=\"fill:rgb(0, 0, 0);\"/></svg>")),
        };
        var host = new HostedContent { Id = 1, Host = view, OutputRect = new Rect(100, 200, 400, 300) };
        var log = new StringBuilder();

        var result = HostedContentResolver.ResolveAsync($"<svg xmlns=\"http://www.w3.org/2000/svg\">\n{host.Placeholder}\n</svg>", new[] { host }, HostedContentMode.PreferVector, log).Result;

        Assert.DoesNotContain("<!--hosted", result);
        Assert.Contains("querySelectorAll('svg')", view.LastScript);
        var doc = XDocument.Parse(result);
        var nested = Assert.Single(doc.Root!.Descendants(Svg + "svg"));
        Assert.Equal("150", nested.Attribute("x")!.Value);
        Assert.Equal("220", nested.Attribute("y")!.Value);
        Assert.Equal("300", nested.Attribute("width")!.Value);
        Assert.Single(nested.Descendants(Svg + "circle"));
        Assert.Contains("SVG from page", log.ToString());
    });

    [Fact]
    public void Without_page_svg_and_without_native_window_the_placeholder_is_removed() => Sta.Run(() =>
    {
        var view = new FakeWebView { Result = PageResult() };
        var host = new HostedContent { Id = 2, Host = view, OutputRect = new Rect(0, 0, 10, 10) };
        var log = new StringBuilder();

        var result = HostedContentResolver.ResolveAsync($"<svg>{host.Placeholder}</svg>", new[] { host }, HostedContentMode.PreferVector, log).Result;

        Assert.Equal("<svg></svg>", result);
        Assert.Contains("left empty", log.ToString());
    });

    [Fact]
    public void Image_mode_does_not_ask_the_page_for_svg() => Sta.Run(() =>
    {
        var view = new FakeWebView { Result = PageResult((0, 0, 10, 10, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>")) };
        var host = new HostedContent { Id = 3, Host = view, OutputRect = new Rect(0, 0, 10, 10) };

        HostedContentResolver.ResolveAsync(host.Placeholder, new[] { host }, HostedContentMode.Image, new StringBuilder()).Wait();

        Assert.Null(view.LastScript);
    });
}
