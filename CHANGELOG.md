# Changelog

## 0.8.1

First public release.

- **Export tree** (Ctrl+Shift+E): the tree last clicked into, completely, including rows
  scrolled out of view, or optionally only the visible part. Choice of panel with tab strip,
  panel with toolbar, tree only, or all three with a diagnostics file. The part of the panel
  below the tree, such as the attribute details, is placed below the complete tree.
- **Copy tree** (Ctrl+Shift+K): the same to the clipboard, as SVG text and as image.
- **Export window** (Ctrl+Shift+W): the whole editor window as shown.
- **Export region** (Ctrl+Shift+R): a rectangle dragged over the editor.
- InternalLink lines are complete across the whole tree: for the capture the tree's scroll area
  is enlarged to its content, so the editor draws every link as for visible rows.
- Views of other plugins that render natively are included; browser-based views contribute
  the SVG of their page (or a picture of it), other native windows a screen copy.
- Optional outputs: PDF and PNG (rendered with Microsoft Edge), clipboard after every export.
- Options: hide mouse hover highlight, hide selection highlight, crop empty space, warn about
  text the editor shows only partly, plugin diagrams as vector or image, text as outlines.
- Flat, editable SVG: every path, text and image is a top-level element.
- Result box with Open and Show in folder; notification for exports started by shortcut while
  the panel is closed; settings kept across editor restarts.
