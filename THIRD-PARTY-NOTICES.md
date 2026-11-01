# Third-party notices

Gpixel itself is released under the MIT License (see `LICENSE`). Release builds bundle or use the following third-party components.

| Component | Use | License |
|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) | Self-contained runtime in release builds | MIT |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (Avalonia, Desktop, Themes.Fluent, Controls.ColorPicker) | Cross-platform UI | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | Rendering and text shaping (via Avalonia) | MIT |
| [Skia](https://skia.org) | 2D graphics library (via SkiaSharp) | BSD-3-Clause |
| [HarfBuzz](https://github.com/harfbuzz/harfbuzz) | Text shaping (via HarfBuzzSharp) | MIT ("Old MIT") |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus), [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) | Avalonia platform dependencies | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | Reading the Unity Hub project list (`hub.db`) | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | SQLite native bindings | Apache-2.0 |
| [SQLite](https://sqlite.org) | Embedded database engine | Public domain |
| [Jint](https://github.com/sebastienros/jint) | Sandboxed JavaScript interpreter for script effects | BSD-2-Clause |
| [Acornima](https://github.com/adams85/acornima) | JavaScript parser (via Jint) | BSD-3-Clause |
| [Lucide](https://lucide.dev) | Shapes of the line icons redrawn in `VectorIcons.cs` (eye, lock, copy, plus, minus, refresh, layers, file/folder plus, folder open, play, pause, dice, external link, link, arrows, move, chevrons, check, x, circle, square, diamond, triangle, grip, menu, star, heart, download, trash, pencil, corner arrows, book image, image, sparkles, globe, coffee, grid 3x3, film, image down, maximize 2, sliders horizontal, undo 2, circle alert, columns 2, info, loader circle, package, upload, code) | ISC |
| [Simple Icons](https://simpleicons.org) | Discord logo path ([`discord.svg`](https://github.com/simple-icons/simple-icons/blob/develop/icons/discord.svg)) in the app's `VectorIcons.cs` (Help menu, About, crash and AI chat Discord buttons) and on the website's Discord buttons (the logo is a trademark of Discord Inc.) | CC0 1.0 |
| DawnBringer 32 palette by DawnBringer | Default palette colors | Freely shared by the author |

## Online content

- **Lospec palettes** (`팔레트 웹(Lospec)…`): palette lists and example images are fetched at run time from [lospec.com](https://lospec.com/palette-list) only when that window is opened. Palettes and example images belong to their respective authors; nothing from Lospec is bundled with Gpixel.
- **Claude chat**: uses the user's own locally installed [Claude Code](https://docs.anthropic.com/en/docs/claude-code) CLI and its login. Gpixel does not bundle or redistribute Claude Code; usage is subject to Anthropic's terms.

The full license texts of the MIT, BSD-2-Clause, BSD-3-Clause, Apache-2.0 and ISC licensed components are available at the linked project pages.
