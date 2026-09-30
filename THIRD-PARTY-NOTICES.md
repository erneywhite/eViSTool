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
| .NET libraries (System.Security.Cryptography.ProtectedData) | © .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| CommunityToolkit.Mvvm | © .NET Foundation and Contributors | https://github.com/CommunityToolkit/dotnet |
| Newtonsoft.Json | © 2007 James Newton-King | https://github.com/JamesNK/Newtonsoft.Json |

## Acknowledgements

- [ViSST Server Tool](https://mods.vintagestory.at/show/mod/17652) by THumbert — ideas for server management
  (scheduled backups via `/genbackup`, restarts announced in chat). No code was taken.

Vintage Story is a game by Anego Studios. eViSTool is an unofficial fan-made tool and is not affiliated with Anego Studios.
