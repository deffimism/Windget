# Windget

Current public version: `v0.2.1`

Windget is a lightweight Windows desktop widget app built with WPF and .NET. It places a transparent desktop canvas over the wallpaper and lets you arrange memo cards, system resource graphs, a sound mixer, calendar events, a timer/stopwatch, and quick launcher tiles.

This repository is organized as a fresh public GitHub project starting from `v0.1.0`.

## Features

- Transparent desktop canvas with click-through empty areas
- Movable and resizable widgets with saved position, size, and opacity
- Resolution-aware Auto layout for FHD, QHD, and 4K displays
- Alignment guide lines while moving or resizing widgets
- System tray icon for showing or hiding Control Center
- Optional Windows startup registration with Windget name/icon metadata and scheduled-task priority
- Alt+Tab hidden desktop-widget behavior
- Transparent-background app icon

## Widgets

### Control Center

- Toggle widgets on or off
- Adjust global opacity
- Save layout state
- Auto-arrange widgets for the current monitor resolution
- Hide only the Control Center without hiding all widgets
- Toggle `Always On Top`
- Register or remove Windows startup launch

### Memo

- Collapsible title/content memo cards
- Inline title editing
- `Open / Done` state
- Per-memo reset rules
- `After Done`, `At Time`, `Weekly`, and `Monthly` reset modes

### System

- CPU, memory, GPU, network, and app memory display
- Network speed display with automatic KB/s, MB/s, and GB/s units
- CPU, memory, and GPU graph views

### Sound Mixer

- Master volume and mute
- Per-app audio session volume and mute
- Cleaner system audio session names instead of raw Windows resource paths
- Improved app icon detection for games and protected processes
- Playback device and recording device selection
- Quick access to Windows App Volume Settings for per-app output routing
- MMDevices-backed fallback for detecting more playback and recording devices
- Correct MMDevice endpoint enumeration for reliable Windows default device changes
- Active endpoint validation before changing Windows default audio devices
- Stable device picker placement inside the widget canvas

### Calendar

- Date-based event list
- Automatically returns to today when the system date changes while the app is running
- Softer highlight color for days with events
- Event title, location, start time, and end time
- Time picker UI

### Timer / Stopwatch

- Timer mode
- Stopwatch mode
- Optional Windows notification alarm
- Hour/minute scroll picker

### Quick Launcher

- Drag-and-drop files, folders, and shortcuts
- User-created categories
- Category deletion with shortcut fallback to `General`
- Icon-only display mode
- Delete controls only appear while Quick Launcher settings are open

## Installation

Recommended:

```text
Windget-v0.2.1-win-x64-setup.exe
```

The setup executable contains the MSI package and keeps a loading window open until Windows Installer finishes.

Direct MSI package:

```text
Windget-v0.2.1-win-x64.msi
```

The MSI installs Windget for the current user under `%LOCALAPPDATA%\Programs\Windget`, registers app metadata for Task Manager, and automatically removes older or same-version MSI-installed Windget builds during upgrade.

Portable ZIP:

```text
Windget-v0.2.1-win-x64.zip
```

Extract the ZIP to a folder you control, then run:

```text
Windget.exe
```

For details, see [USAGE.md](USAGE.md).

## Build From Source

Requirements:

- Windows 10 or later
- .NET SDK compatible with the project target

```powershell
cd WindgetApp
dotnet build
dotnet run
```

Create a release build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o .\publish\win-x64
```

Create the MSI after publishing to the matching release folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-Msi.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-Setup.ps1
```

## Release Workflow

Release notes are maintained in [RELEASE_NOTES.md](RELEASE_NOTES.md).

## AI Usage Disclosure

This project was planned, implemented, and documented with assistance from AI tools. Feature direction, UI decisions, testing feedback, and final behavior were refined through user review.
