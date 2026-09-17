# AMLSvgExport

Vector export of AutomationML Editor views.

A plugin for the AutomationML Editor that exports trees, attribute panels, the window or any
region as vector graphics: complete InstanceHierarchies and libraries including rows scrolled
out of view, attribute panels with their details, and diagrams of other plugins, as SVG and
optionally PDF or PNG. The picture is taken from what the editor actually renders, and it
stays editable: text is text and every element is a separate object. It is meant for papers,
slides and documentation, instead of screenshots.

## Requirements

- AutomationML Editor 6.4 on Windows (the plugin was developed against 6.4.3)
- Microsoft Edge for the optional PDF and PNG output (installed with Windows)

## Installation

The plugin is published on [nuget.org](https://www.nuget.org/packages/Aml.Editor.Plugin.SvgExport),
where the editor's PlugIn Manager finds it by itself.

1. Close all AutomationML Editor windows except one. With several instances open, an
   installation or update is applied only partly and no plugin is loaded on the next start.
2. In the editor, open *PlugIns → PlugIn Manager*, choose the plugin source *PublicSource*
   and install *Aml.Editor.Plugin.SvgExport* from *available*. Restart the editor.

Updates appear under *updates* in the same way. Without access to nuget.org, download
`Aml.Editor.Plugin.SvgExport.<version>.nupkg` from the
[releases](https://github.com/hsu-aut/AMLSvgExport/releases) into a folder of its own, add
that folder as a plugin source (gear button) and install from there.

The plugin panel opens as a floating window. The four actions are also in the editor's
plugin toolbar (the editor shows the toolbar of one plugin at a time).

## Usage

| Shortcut | Action |
|---|---|
| **Ctrl+Shift+E** | Export the tree you last clicked into, completely, including rows scrolled out of view |
| **Ctrl+Shift+K** | The same, straight to the clipboard: SVG text for vector editors, an image for office programs |
| **Ctrl+Shift+W** | Export the whole editor window exactly as shown |
| **Ctrl+Shift+R** | Drag a rectangle over the editor; exactly that area is exported |

The shortcuts work anywhere in the editor, also while the plugin panel is closed; the result
is then reported by a small notification with *Show in folder*. They do not reach the plugin
while the keyboard focus is inside a browser-based plugin view (such as a diagram editor
hosted in WebView2); click outside it first or use the toolbar.

A tree export takes the whole panel if wanted: the part below the tree, such as the attribute
details below the attribute list, is placed below the complete tree. If the tree's panel is
hidden behind another tab, it is brought to front for the export.

### Options

Options are set in the plugin panel and kept across editor restarts.

| Option | Effect |
|---|---|
| Export tree writes | Panel with tab strip, panel with toolbar (default), tree only, or all three plus a diagnostics file |
| Only the visible part of the tree | The tree as currently shown, with its scroll position, instead of the complete tree |
| Also save as PDF / PNG | Same size as the SVG; the PDF keeps text and vectors and goes straight into LaTeX. PNG at twice the resolution |
| Also copy to clipboard | After every file export |
| Hide mouse hover highlight | The row or button under the pointer is exported without its hover state |
| Hide selection highlight | The selected row is unselected for the export and selected again afterwards. Panels that follow the selection, such as the attribute details, are then empty in the export |
| Crop empty space | Cuts empty space on the right and at the bottom |
| Warn if text is cut off | Reports values the editor shows only partly (for example long IDs in a narrow panel), so the panel can be widened before exporting again |
| Plugin diagrams as vector | Browser-based plugin views contribute the SVG of their page; HTML parts such as palettes are then missing. Switch off for a picture that includes them |
| Text as outlines | Converts text to shapes: identical everywhere, but no longer editable as text |

### Editing the result

Every path, text and image is a top-level element with an absolute position. In a vector
editor such as Inkscape, rows can be selected with a rubber band, removed, and the rest moved
up. Text keeps the editor's font (Segoe UI) unless *Text as outlines* is set.

## How it works

The plugin interface of the editor offers no access to its views, but plugins run inside the
editor's WPF process. The plugin therefore

1. finds the editor's tree controls and the surrounding dock panes by type name, without a
   compile-time dependency on the editor's own assemblies, and
2. translates what WPF renders into SVG: for every visual in the exported area its drawing
   (geometries, gradients, clips, images, glyph runs) with offset, transform, clip and
   opacity. The capture starts at the window, so adorners such as the InternalLink lines are
   included.

For a complete tree, the tree's scroll area is made as large as its content for the capture,
so nothing needs to be scrolled: every row is in view, the editor creates all rows itself and
draws every InternalLink as it does for visible rows. Scrolling page by page does not work for
links, because the editor draws a link only while the rows at its ends exist and places a line
end that is scrolled above the view at the wrong position. Only the tree and its link lines are
taken from the enlarged state; header, toolbar and the parts below the tree come from the
original layout. Size, scroll position and selection are restored afterwards.

Areas that WPF does not render itself, such as the WebView2 of another plugin, are filled
from the source: the SVG elements of the page with their computed styles, or a picture from
WebView2, or a copy of the screen area for any other native window.

## Limitations

- The plugin relies on the internal structure of the editor (type names of its tree control
  and dock panes). A future editor version can break it.
- Only what the editor renders can be exported. Values the editor cuts off appear cut off;
  the plugin warns about them.
- Very large, widely expanded trees take a few seconds to export, since all rows are created.
  During the export the tree briefly covers the panel below it.
- Browser-based plugin views exported as vector lose their HTML parts (palettes, toolbars).

## Building and testing

```bash
dotnet build Aml.Editor.Plugin.SvgExport.sln -c Release
dotnet test Aml.Editor.Plugin.SvgExport.sln -c Release
```

The tests render WPF windows and need Windows; the PDF/PNG test uses Microsoft Edge. The
package is written to `build/Plugins/Aml.Editor.Plugin.SvgExport/Release/`.

## Releasing

A tag `v<version>` matching `<Version>` in the project file builds and tests the plugin,
publishes the package to nuget.org and attaches it to a GitHub release:

```bash
git tag v0.8.1
git push origin v0.8.1
```

A version on nuget.org cannot be replaced or deleted (only unlisted), so test a package in the
editor from a local plugin source before tagging.

Publishing uses nuget.org trusted publishing: GitHub proves the identity of the workflow and
nuget.org issues a short-lived key, so no API key is stored. One-time setup:

1. On nuget.org, under *Trusted Publishing*, add a policy for repository owner `hsu-aut`,
   repository `AMLSvgExport`, workflow file `ci.yml` and environment `nuget.org`.
2. In the GitHub repository, create the environment `nuget.org` (*Settings → Environments*),
   optionally with required reviewers, and add the variable `NUGET_USER` with the nuget.org
   profile name that owns the policy.

## Related projects

- [AMLFPB.js](https://github.com/hsu-aut/AMLFPB.js): formalised process descriptions
  (VDI/VDE 3682) in the AutomationML Editor
- [AMLPetriNet](https://github.com/hsu-aut/AMLPetriNet): place/transition Petri nets in the
  AutomationML Editor

## License

MIT, see [LICENSE](https://github.com/hsu-aut/AMLSvgExport/blob/main/LICENSE). Third-party
components: [THIRD-PARTY-NOTICES.md](https://github.com/hsu-aut/AMLSvgExport/blob/main/THIRD-PARTY-NOTICES.md).
