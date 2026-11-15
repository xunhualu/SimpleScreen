# SimpleScreen

**Sound follows your windows.** Per-app audio routing for multi-monitor Windows — each app plays through the speaker paired with the monitor its window is on.

SimpleScreen is a customized fork of [ScreenSound](https://github.com/twibster/ScreenSound) (MIT, by Omar Omran), extended with monitor control, brightness sync and software color adjustment.

## Features

### Audio routing (from the original ScreenSound)
- Routes each app's audio to the audio device paired with the monitor its window sits on
- Per-app pins: lock an app to a specific audio device regardless of window position
- Hidden or minimised windows keep their audio on the last screen they occupied — and if the stream drifts away, it gets pulled back
- Tray mode, autostart, English / 中文 UI

### Added in this fork
- **Nit-based brightness sync** — absolute brightness alignment across panels with different peak nits (DDC/CI + WMI fallback)
- **Window size sync** — keeps the main window at the same physical size when dragged to a monitor with a different DPI scale (Windows normally keeps the *logical* size, so the window visibly jumps)
- **Color sync** — one color temperature / eye-care setting for the whole desk; editing the software color on any monitor's card applies it to every screen
- **Monitor details page** — auto-detected DDC controls: contrast, speaker volume, mute, input source, color preset, RGB gain
- **Software color** — 4000–9300 K color temperature and low-blue-light eye care via gamma ramp; works even on panels without DDC, and is restored when the app exits
- **Display rotation** — portrait/landscape switching through the Windows display configuration API, identical to the system setting
- **Robust DDC bus** — serialized transactions with retries, so a panel wired over two cables is no longer misdetected

## Requirements

- Windows 10 1809+ / Windows 11, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (the installer offers to download it automatically)
- DDC/CI-capable external monitor for hardware controls (audio routing and software color work without it)

## Installation

Download the latest release from the [Releases](https://github.com/xunhualu/SimpleScreen/releases) page:

- **SimpleScreen-Setup-x.y.z.exe** — installer (recommended)
- **SimpleScreen-x.y.z-portable.zip** — portable build, unpack and run `SimpleScreen.exe`

## Building from source

```powershell
git clone https://github.com/xunhualu/SimpleScreen.git
cd SimpleScreen
dotnet restore ScreenSound.sln
dotnet publish ScreenSound\ScreenSound.csproj -c Release -r win-x64 --self-contained false -o publish
```

The Inno Setup script in `installer/` builds the setup exe from the `publish/` folder.

## How it works

- Window positions are tracked via `SetWinEventHook` (move/resize end, foreground change) plus a 30 s heartbeat; audio sessions are moved between endpoints with the undocumented-but-stable `IPolicyConfig` COM interface.
- Monitor control uses DDC/CI (`dxva2.dll`) with a global bus lock and retry, falling back to WMI brightness for laptop panels.
- Software color is a per-display GDI gamma ramp, applied on startup if saved and restored to identity on exit.

## License

MIT — see [LICENSE](LICENSE). Original ScreenSound © Omar Omran; SimpleScreen modifications © xunhualu.
