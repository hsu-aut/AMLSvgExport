using System.IO;
using System.Xml.Linq;
using Aml.Editor.Plugin.SvgExport;
using Xunit;

namespace Aml.Editor.Plugin.SvgExport.Tests;

/// <summary>
/// Metadata.xml is the manifest the PlugIn Manager reads. Its version is written by hand, so it
/// silently falls behind the package version unless something checks it.
/// </summary>
public class MetadataTests
{
    private static XElement Manifest()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(MetadataTests).Assembly.Location)!, "Metadata.xml");
        Assert.True(File.Exists(path), $"Metadata.xml is missing next to the assembly ({path})");
        return XDocument.Load(path).Root!;
    }

    [Fact]
    public void Manifest_version_matches_the_assembly()
    {
        var assembly = typeof(SvgExportPlugin).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(assembly, Manifest().Element(Manifest().Name.Namespace + "Version")!.Value);
    }

    [Fact]
    public void Manifest_names_the_package_and_its_author()
    {
        var ns = Manifest().Name.Namespace;
        Assert.Equal("Aml.Editor.Plugin.SvgExport", Manifest().Element(ns + "PackageName")!.Value);
        Assert.Equal("Hamied Nabizada", Manifest().Element(ns + "Author")!.Value);
    }
}
