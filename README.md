# Clip

[![Latest release](https://img.shields.io/github/v/release/Kal-Voe/Clip?label=release)](https://github.com/Kal-Voe/Clip/releases/latest)
[![License](https://img.shields.io/github/license/Kal-Voe/Clip)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2011-blue)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](#run-from-source)

Clip is a free, open-source Windows clipboard history app inspired by Raycast's clipboard history.

It runs locally, opens with `Alt+V`, and keeps clipboard history on your own device.

## Download

[![Download Clip for Windows](https://img.shields.io/badge/⬇_Download-Clip_for_Windows-2ea44f?style=for-the-badge)](https://github.com/Kal-Voe/Clip/releases/latest/download/Clip-Setup.exe)

**[Click here to download the latest Clip installer.](https://github.com/Kal-Voe/Clip/releases/latest/download/Clip-Setup.exe)** Then open the downloaded `Clip-Setup.exe` and follow the prompts — that's it. The button always downloads the newest version.

The installer:

- Installs per-user with no admin prompt.
- Adds a Start menu entry.
- Registers Clip in Add/Remove Programs.
- Starts Clip with `Alt+V`.

Prefer a portable copy with no installer? **[Download Clip-win-x64.zip](https://github.com/Kal-Voe/Clip/releases/latest/download/Clip-win-x64.zip)**, unzip it anywhere, and run `Start-Clip.ps1` or `Clip.exe --palette-session`.

Already have the .NET 8 Desktop Runtime? **[Download Clip-win-x64-framework-dependent.zip](https://github.com/Kal-Voe/Clip/releases/latest/download/Clip-win-x64-framework-dependent.zip)** for the smaller portable build.

### SmartScreen

Clip is not code-signed yet, so Windows SmartScreen may warn the first time you install or run it.

If you trust the source, click **More info**, then **Run anyway**. The full source code, release history, privacy policy, and build scripts are public in this repository.

## Features

- Clipboard history for text, links, colors, images, files, and folders.
- Searchable `Alt+V` clipboard window.
- Pin, unpin, reorder, copy, paste, edit, delete, save, and open items.
- Image thumbnails and previews.
- File metadata and file previews where Windows or local renderers support it.
- Searchable `Open With` picker.
- Color swatches for copied hex colors.
- Source app metadata and item information panel.
- Local debug logs with `Ctrl+Shift+L`.
- Settings for startup, updates, history limits, storage, hotkeys, paste format, and excluded apps.

## Project Status

Clip is an individual-maintained open-source project.

- Releases are published on GitHub: <https://github.com/Kal-Voe/Clip/releases>
- Builds are produced with GitHub Actions.
- Tests live in `tests\Clip.Tests`.
- Privacy details live in `PRIVACY.md`.
- Code signing is planned, but releases are currently unsigned.

## Requirements

- Windows 11.
- Microsoft Edge WebView2 runtime for HTML previews.
- .NET 8 Desktop Runtime only if you use the smaller framework-dependent zip.
- .NET 8 SDK only if you want to build from source.

## Run From Source

```powershell
dotnet build .\Clip.sln
.\Start-Clip.ps1
```

Then press `Alt+V`.

## Publish a Release Build

```powershell
.\Publish-Clip.ps1
```

The release files are created under:

```text
artifacts\publish\Clip-win-x64
```

Start the lightweight Clip host:

```text
artifacts\publish\Clip-win-x64\Start-Clip.ps1
```

The zip file is created at:

```text
artifacts\publish\Clip-win-x64.zip
```

For a smaller framework-dependent build, run:

```powershell
.\Publish-Clip.ps1 -FrameworkDependent
```

This writes `artifacts\publish\Clip-win-x64-framework-dependent`. It is much smaller, but the machine needs the .NET 8 Desktop Runtime installed, so the normal installer/zip remains the default public download.

## Start With Windows

After publishing or building, run:

```powershell
.\Install-ClipStartup.ps1
```

This installs Clip under `%APPDATA%\Programs\Clip`, creates Desktop and Start Menu shortcuts, and starts Clip automatically when your Windows user signs in.

## Privacy

Clip stores clipboard history locally under:

```text
%LOCALAPPDATA%\Clip\Clipboard History
```

App settings, update state, app cache data, and logs are stored under `%LOCALAPPDATA%\Clip`. Review `PRIVACY.md` before sharing logs or local app data.

## Development

```powershell
dotnet build .\Clip.sln
dotnet test .\Clip.sln
```

Main projects:

- `src\Clip.Shell`: WPF shell and main UI.
- `src\Clip.Core`: clipboard history model and local JSON store.
- `src\Clip.Watcher`: headless clipboard watcher and Windows integration helpers.
- `tests\Clip.Tests`: focused regression tests.
