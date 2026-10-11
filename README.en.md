# ManualVideoStudio

English | [日本語](README.md)

ManualVideoStudio is a Windows 11 app that records your screen operations, cuts out the parts you do not need, and adds subtitles and arrows to make how-to videos. It can also export MP4 and Animated GIF files and extract still images for step-by-step documents. **The app and its user guide are available in Japanese only.**

This repository contains the source code of the publicly released ManualVideoStudio.

## Features

- Records a region, a window or a whole monitor (15 / 30 fps, with or without the cursor; the region can be landscape or portrait with a fixed aspect ratio; microphone recording is off by default)
- Cuts that delete or shorten waiting time and mistakes, with a 0.3-second crossfade at each joint (can be turned off). Freeze, silence and scene-change detection in FFmpeg suggests **candidates** for cuts (you decide which to use)
- Subtitles (4 styles, using fonts installed in Windows), timed shapes (rectangles, arrows, lines, text and mosaic) and a title at the start of the video
- Exports MP4 and Animated GIF (light / standard / high quality) with subtitles and shapes burned in
- Extracts still images for documents (`<number>_<caption>.png`) and adds rectangles, arrows, lines, text and mosaic annotations
- Lets Gemini (in the browser) draft subtitles and captions, which you check and import (a manual workflow of copying the prompt and pasting the answer; the Gemini API is not used)
- Keeps each project in its own folder with the source video and exported files; the project list can open, duplicate, rename, delete (to the Recycle Bin), and export / import projects as zip files
- A simple mode (recording, cuts, subtitles, shapes and export) and a full mode (also document images and AI assistance), switched in Settings

The app does not communicate over the internet. Recordings contain whatever is shown on the screen while recording (see the privacy policy below).

## Supported OS and runtime

- Windows 11 (64-bit, x64)
- **FFmpeg (ffmpeg.exe and ffprobe.exe) must be obtained separately.** FFmpeg is included neither in the app nor in this repository. Burning subtitles needs libass (the `subtitles` filter) and recording needs `gdigrab` (`dshow` for the microphone), so choose a build with these features, such as a full build (verified: 9.0.1, full build from gyan.dev). The app finds FFmpeg on the PATH automatically; otherwise, set its location in Tools → Settings → FFmpeg.
- The Microsoft Store version includes .NET, so .NET does not need to be installed.
- To build and run from the source in this repository, the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) is required.

## Installation

The official distribution channel is the Microsoft Store. Please get ManualVideoStudio there (free; available in the Japanese Microsoft Store).

- Microsoft Store: https://apps.microsoft.com/detail/9N33XK4M3JFJ

This GitHub repository does not distribute executables (binaries).

## Basic usage

1. First, prepare FFmpeg (section "3-1" of the user guide). When it is found, its version appears at the bottom right of the main window.
2. On the start screen, choose to record or to open a video. When you open a video, the app creates a "date-time_name" project folder in the save location (by default `ManualVideoStudio` in your Videos folder) and moves the video there.
3. Cut the parts you do not need, confirm the cuts, and add subtitles, shapes and a title.
4. Export MP4 or Animated GIF from the export menu.

See the user guide (Japanese) for details. Help (F1) in the app opens the web version. Help > About (at the bottom) shows the version, the MIT License and its full text.

- User guide: https://www.iwanaga.net/manualvideostudio/manual/

## Building

### Requirements

- Windows 11 x64
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) (verified: 8.0.425)
- Only the `dotnet` CLI is needed (Visual Studio is not required).
- The app uses the NuGet packages `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Logging` (`Microsoft.Extensions.Logging.Abstractions` in Core). The tests use `Microsoft.NET.Test.Sdk`, `xunit` and `xunit.runner.visualstudio`. All are restored from nuget.org on the first build.
- FFmpeg is not needed to build or to run the unit tests. It is needed for the integration tests and to run the app.

### Commands

Run from the repository root. There is no `.sln`; each csproj is used on its own.

```powershell
dotnet build ManualVideoStudio.csproj
dotnet test Tests/ManualVideoStudio.Tests.csproj
dotnet run --project ManualVideoStudio.csproj
```

The integration tests use a real FFmpeg, found on the PATH or given by the environment variable `MVS_FFMPEG` (the path of ffmpeg.exe). They include tests that record the actual screen, so run them while the screen is not locked (and, over Remote Desktop, while the window is not minimized).

```powershell
dotnet test IntegrationTests/ManualVideoStudio.IntegrationTests.csproj
```

To create the distributable folder (framework-dependent, win-x64), run the following. The output goes to `publish\FrameworkDependent\` and requires the .NET 8 Desktop Runtime (x64) and FFmpeg to run.

```powershell
dotnet publish ManualVideoStudio.csproj -c Release -p:PublishProfile=FrameworkDependent
```

### Layout

| Path | Contents |
|---|---|
| `ManualVideoStudio.csproj` | The app (WPF, `net8.0-windows`, MVVM) |
| `App.xaml(.cs)`, `MainWindow.xaml(.cs)` | Startup and the main window |
| `ViewModels/`, `Views/`, `Controls/`, `Converters/` | View models and views (main window, recorder, settings, annotation, subtitles, project list and more) |
| `Core/` | UI-independent logic (`net8.0`): FFmpeg argument building and output parsing; editing of cuts, subtitles (ASS), shapes and titles; recording state; GIF; parsing AI answers; saving projects and settings |
| `Imaging/` | Drawing annotations, shapes and titles, and saving images (`net8.0-windows`, WPF rendering) |
| `Services/` | Dialogs, playback, FFmpeg status, fonts, Recycle Bin, and listing capture targets and microphones (Win32 / Core Audio) |
| `Resources/` | UI strings (`Strings.resx`, Japanese) and the icon |
| `Tests/` | Unit tests (xUnit; no FFmpeg needed) |
| `IntegrationTests/` | Integration tests with a real FFmpeg (xUnit) |
| `tools/icon-source.png`, `Resources/icon.ico` | App icon (`pwsh -File tools/make-icon.ps1` creates `icon.ico` from `icon-source.png`) |

The tooling that builds the Microsoft Store package (MSIX) and the installer is not included in this repository. The `ManualVideoStudioMsix` condition in `ManualVideoStudio.csproj` is for the Store version. The user guide is the web version (URL above); it is not embedded in the exe (since v1.17.0).

Settings are saved to `%AppData%\ManualVideoStudio\settings.json`, and the cache and logs to `%LocalAppData%\ManualVideoStudio\` (the Microsoft Store version uses the actual location under `%LocalAppData%\Packages\<package family name>\LocalCache\`, because Windows redirects the writes of a packaged app). The video prepared for Gemini subtitles is created in the `caption-requests\` folder inside the project folder.

Source comments and UI strings are written in Japanese. References such as "仕様書" or "仕様書 §N" (specification, section N) in comments point to the internal development specification, which is not included in this repository.

## License

Released under the [MIT License](LICENSE).

Copyright (c) 2026 ja1bus (© ja1bus)

For third-party software such as FFmpeg and the NuGet packages, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## No warranty

This software is provided "as is", without warranty of any kind, express or implied. In no event shall the author be liable for any damages arising from the use of this software (see [LICENSE](LICENSE) for details).

## Links

- Web page: https://www.iwanaga.net/manualvideostudio/en/
- User guide (Japanese): https://www.iwanaga.net/manualvideostudio/manual/
- Changelog: https://www.iwanaga.net/manualvideostudio/en/changelog/
- Privacy policy: https://www.iwanaga.net/manualvideostudio/en/privacy/
- Microsoft Store: https://apps.microsoft.com/detail/9N33XK4M3JFJ
- Contact: support@iwanaga.net
