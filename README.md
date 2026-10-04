# MGS4 iPod Manager

A Windows utility and accompanying ASI plugin for managing and expanding iPod content in **METAL GEAR SOLID 4: Guns of the Patriots - Master Collection Version**.

The iPod Manager provides a graphical interface for adding, organizing, inspecting, and deploying custom music and restored podcast content while preserving the game's original iPod tracks.

> [!NOTE]
> This is an unofficial fan project and is not affiliated with or endorsed by KONAMI.

## Features

- Full custom audio deployment to the in-game iPod interface
- Support for FLAC, WAV, AAC, M4A, MP3, OGG, and WMA
- Support for 'MGS4 Integral Podcast' and 'Guns of the HIDECHAN!Radio' 
  - NOTE: Requires [additional download from NexusMods](https://www.nexusmods.com/metalgearsolid4mc/mods/100)
- Expanded iPod capacity to 1,024 total tracks (73 default + 951 custom).
- Track metadata, annotations, waveform display, and category management.
- Enable or disable continuous background playback while the iPod is unequipped

## Installation

1. Download the latest release from the [Releases](../../releases/latest) page.
2. Extract the `MGS4` folder from the .zip into your **`./steamapps/common/METAL GEAR SOLID 4`** install directory.
4. Install the **.NET 10 Desktop Runtime** if it is not already installed.
5. Run:

```text
MGS4\iPod\iPodManager.exe
```

The release includes the required `iPodManager.asi` plugin and Ultimate ASI Loader.

## Usage

[![MGS4 iPod Manager Tutorial](https://img.youtube.com/vi/GzS7nFnazko/maxresdefault.jpg)](https://youtu.be/GzS7nFnazko)

Watch the full setup and usage tutorial on YouTube.

## Building

### Requirements

- Windows
- .NET 10 SDK
- Visual Studio Build Tools with **Desktop development with C++**
  - MSVC x64 toolchain
  - Windows SDK

Clone the repository and run:

```powershell
.\build.ps1
```

The build script automatically:

- Downloads and verifies the required FFmpeg and vgmstream dependencies.
- Downloads and verifies Ultimate ASI Loader.
- Builds the MGS4 iPod Manager application.
- Builds the native ASI plugin.
- Produces a deployment-ready build under:

```text
build\MGS4
```

The resulting folder can be copied directly into the game's installation directory.

## Project Structure

```text
iPodManager.App/       WPF application and managed tests
iPodManager.Plugin/    Native ASI plugin and native tests
build.ps1              Dependency setup and release build
```

## Third-Party Software

MGS4 iPod Manager uses several third-party projects, including:

- [FFmpeg](https://ffmpeg.org/)
- [vgmstream](https://github.com/vgmstream/vgmstream)
- [TagLibSharp](https://github.com/mono/taglib-sharp)
- [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader)

Applicable third-party licenses and notices are included with release builds. See `iPodManager.App/THIRD_PARTY_NOTICES.md` for additional information.

## License

The original source code for MGS4 iPod Manager is licensed under the [MIT License](LICENSE).

Third-party software and assets included with or used by the project are subject to their respective licenses and terms.
