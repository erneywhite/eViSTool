# Third-party notices

eViSTool is licensed under the GNU General Public License v3.0 (see [LICENSE](LICENSE)).
It includes or is based on the following third-party work, each under its own license.

## Rustique

Parts of the Vintage Story ModDB client logic (API handling, release and version selection,
tolerant `modinfo.json` parsing) are ported from Rustique.

- Source: https://github.com/Tekunogosu/Rustique
- License: MIT

```
MIT License

Copyright (c) 2025 Theysa

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Libraries distributed with eViSTool

All of these are licensed under the MIT License; the license text is the same as above,
with the copyright line given below.

| Component | Copyright | Source |
|---|---|---|
| .NET runtime and ASP.NET Core (bundled into `eViSTool.Agent.exe`) | © .NET Foundation and Contributors | https://github.com/dotnet/runtime, https://github.com/dotnet/aspnetcore |
| .NET libraries (System.Security.Cryptography.ProtectedData, Microsoft.Extensions.Logging.Abstractions) | © .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| CommunityToolkit.Mvvm | © .NET Foundation and Contributors | https://github.com/CommunityToolkit/dotnet |
| Newtonsoft.Json | © 2007 James Newton-King | https://github.com/JamesNK/Newtonsoft.Json |
| Microsoft.Data.Sqlite | © .NET Foundation and Contributors | https://github.com/dotnet/efcore |
| Avalonia, Avalonia.Controls.DataGrid (the window on Windows and Linux) | © AvaloniaUI OÜ | https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp, HarfBuzzSharp (drawing and text shaping in the Avalonia window) | © 2015-2016 Xamarin, Inc.; © 2017-2018 Microsoft Corporation | https://github.com/mono/SkiaSharp |
| Tmds.DBus (file dialogs and the file manager on Linux) | © Tom Deseyn | https://github.com/tmds/Tmds.DBus |
| FluentFTP (FTP and FTPS for servers at a hosting provider; the window only, not the agent) | © 2015 Robin Rodricks and FluentFTP Contributors | https://github.com/robinrodricks/FluentFTP |
| SSH.NET (SFTP for servers at a hosting provider; the window only, not the agent) | © 2010-2026 Renci | https://github.com/sshnet/SSH.NET |
| BouncyCastle.Cryptography (key exchange and keys for SSH.NET; the window only) | © 2000-2026 The Legion of the Bouncy Castle Inc. | https://github.com/bcgit/bc-csharp |

## Fonts

The window ships its own fonts, so it looks the same on Windows and Linux. Both are licensed under the
SIL Open Font License 1.1; the full license text is next to each font in `Assets/Fonts`.

- **Noto Sans** — Copyright 2022 The Noto Project Authors, https://github.com/notofonts/latin-greek-cyrillic
- **Cascadia Mono** — Copyright (c) 2019 - Present, Microsoft Corporation, with Reserved Font Name Cascadia Code,
  https://github.com/microsoft/cascadia-code

## Lucide icons

The window icons (navigation, buttons, menus) are taken from Lucide.

- Source: https://lucide.dev, https://github.com/lucide-icons/lucide
- License: ISC; icons derived from Feather (check, chevron-down, download, info, lock, monitor, plus, search,
  server, x among those used here) are also under MIT

```
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

```
The MIT License (MIT) (for the icons derived from Feather)

Copyright (c) 2013-present Cole Bemis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## SQLite

World backups use SQLite through the following components:

- **SQLite** (native library `e_sqlite3`, bundled into both executables) — public domain, https://sqlite.org/copyright.html
- **SQLitePCLRaw** — © 2014-2024 SourceGear, LLC, Apache License 2.0, https://github.com/ericsink/SQLitePCL.raw

## Acknowledgements

- [ViSST Server Tool](https://mods.vintagestory.at/show/mod/17652) by THumbert — ideas for server management
  (scheduled backups via `/genbackup`, restarts announced in chat). No code was taken.

Vintage Story is a game by Anego Studios. eViSTool is an unofficial fan-made tool and is not affiliated with Anego Studios.
