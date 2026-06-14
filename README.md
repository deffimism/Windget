# Windget

Current public version: `v0.1.0`

Windget is a lightweight Windows desktop widget app built with WPF and .NET. It places a transparent desktop canvas over the wallpaper and lets you arrange memo cards, system resource graphs, a sound mixer, calendar events, a timer/stopwatch, and quick launcher tiles.

This repository is organized as a fresh public GitHub project starting from `v0.1.0`.

## Features

- Transparent desktop canvas with click-through empty areas
- Movable and resizable widgets with saved position, size, and opacity
- Resolution-aware Auto layout for FHD, QHD, and 4K displays
- Alignment guide lines while moving or resizing widgets
- System tray icon for showing or hiding Control Center
- Optional Windows startup registration
- Alt+Tab hidden desktop-widget behavior
- Transparent-background app icon

## Widgets

### Control Center

- Toggle widgets on or off
- Adjust global opacity
- Save layout state
- Auto-arrange widgets for the current monitor resolution
- Toggle `Always On Top`
- Register or remove Windows startup launch

### Memo

- Collapsible title/content memo cards
- Inline title editing
- `Open / Done` state
- Per-memo reset rules
- `After Done`, `At Time`, and `Monthly` reset modes

### System

- CPU, memory, GPU, network, and app memory display
- CPU, memory, and GPU graph views

### Sound Mixer

- Master volume and mute
- Per-app audio session volume and mute
- Playback device and recording device selection
- Per-app output device selection where Windows audio policy supports it
- MMDevices-backed fallback for detecting more playback and recording devices

### Calendar

- Date-based event list
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

## Installation

Download the latest release ZIP from GitHub Releases:

```text
Windget-v0.1.0-win-x64.zip
```

Extract it to a folder you control, then run:

```text
WindgetApp.exe
```

Recommended install location:

```text
C:\Users\<user name>\Apps\Windget\
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

## Release Workflow

Release notes are maintained in [RELEASE_NOTES.md](RELEASE_NOTES.md).

## AI Usage Disclosure

This project was planned, implemented, and documented with assistance from AI tools. Feature direction, UI decisions, testing feedback, and final behavior were refined through user review.
