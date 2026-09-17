# Third-party notices

The license of this project (MIT, see [LICENSE](LICENSE)) covers its own code.
The plugin package contains only the plugin assembly; no third-party code is
bundled or redistributed with it.

## Referenced at build time

These packages are restored from NuGet when building. They are neither part of the
plugin package nor declared as its dependencies: at run time the AutomationML Editor
provides the plugin contract and the AutomationML engine.

| Component | Version | License | Used for |
|---|---|---|---|
| [Aml.Editor.Plugin.Contract](https://www.nuget.org/packages/Aml.Editor.Plugin.Contract) | 4.3.0 | MIT | Plugin interface of the AutomationML Editor |
| [Aml.Engine](https://www.nuget.org/packages/Aml.Engine) | 4.5.2 | MIT | CAEX types in the plugin interface |
| [xunit](https://github.com/xunit/xunit) | 2.9.3 | Apache-2.0 | Tests only |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 2.8.2 | Apache-2.0 | Tests only |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.14.1 | MIT | Tests only |

## Used at run time, not included

- **Microsoft Edge**, as installed with Windows, renders the optional PDF and PNG
  outputs (started headless). Without it, SVG export works and those options are
  disabled.
- **WebView2 controls of other plugins** are accessed at run time to take the SVG of
  their page or a picture of it. No WebView2 assembly is referenced or shipped.
