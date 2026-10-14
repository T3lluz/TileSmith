# Third-party notices

TileSmith includes or depends on the following open-source software.

| Component | License | Notes |
|---|---|---|
| [Material Symbols](https://github.com/google/material-design-icons) (Rounded, filled) | Apache License 2.0 | Icon font in `src/TileSmith/Assets/Fonts`, code points in `src/TileSmith.Core/Resources` |
| [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK) | MIT | UI framework |
| [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows) | MIT | Settings cards |
| [Velopack](https://github.com/velopack/velopack) | MIT | Installer and automatic updates |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | MIT | SSH connection to your server |
| [.NET](https://github.com/dotnet/runtime) | MIT | Runtime (bundled, self-contained) |
| [LobeHub Icons](https://github.com/lobehub/lobe-icons) (`@lobehub/icons-static-svg`) | MIT | AI provider and model logos in `src/TileSmith/Assets/Brands`. The logos are trademarks of their owners and identify their services only. |

TileSmith also uses these services at runtime:

| Service | Terms | Used for |
|---|---|---|
| [models.dev](https://models.dev) ([source](https://github.com/anomalyco/models.dev)) | MIT | Model names, release dates, context sizes and prices in the model pickers (downloaded at most once a day) |
| [Open-Meteo](https://open-meteo.com) | Data under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) | The `weather.get` action. Weather data by Open-Meteo.com |
| [OpenCode](https://opencode.ai) | OpenCode's terms | The OpenCode provider runs the official `opencode` CLI you install yourself |

Liquid Glass styling after Apple's design language and its open ports (e.g. Kyant's AndroidLiquidGlass). Solid colours after the author's DailyDash and Cinema-Info projects.
