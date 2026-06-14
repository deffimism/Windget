# Windget v0.1.6

Windget v0.1.6 improves Sound Mixer audio session naming and icon detection.

Earlier local development builds used internal version labels, and the public GitHub release history starts from `v0.1.0`.

## v0.1.6 Fixes

- Changed indirect Windows audio resource names such as `@%SystemRoot%...` to display as `System Volume`.
- Added audio-session icon path support before falling back to process icons.
- Improved icon detection for games and protected processes by querying process image paths with limited permissions and falling back to Windows shell icon extraction.

## v0.1.5 Fixes

- Replaced unsupported internal per-application output routing calls with a stable shortcut to Windows App Volume Settings.
- Removed dependency on Windows internal audio policy vtable slots for per-app output changes.
- Kept default playback and recording device switching in-app, where the Windows audio policy path is more stable.

## v0.1.4 Fixes

- Fixed per-application playback device changes by using the available Windows internal audio policy WinRT activation path.
- Added success and failure feedback for per-application output device changes.
- Kept per-application routing guarded because this path depends on Windows audio policy support and may vary by OS build.

## v0.1.3 Fixes

- Added pre-switch validation that the selected device is an active endpoint and matches the requested playback or recording direction.
- Added post-switch verification across Console, Multimedia, and Communications default device roles.
- Added clearer Sound Mixer status messages for successful changes, inactive devices, type mismatches, and Windows apply failures.

## v0.1.2 Fixes

- Fixed MMDevice endpoint enumeration by correcting the `IMMDeviceCollection` COM interface ID.
- Fixed Sound Mixer playback and recording device changes so selected active Windows audio endpoints are applied and verified correctly.
- Reduced invalid device selections by preferring active Windows audio endpoints over fallback registry entries.
- Added explicit failure feedback when Windows rejects or fails to apply a default device change.

## v0.1.1 Fixes

- Fixed Sound Mixer device picker placement so the selection popup opens next to the control instead of jumping to another position.
- Added scrolling and safer sizing for long Sound Mixer device lists.

## v0.1.0 Highlights

- Transparent desktop canvas with click-through empty areas
- Movable and resizable widgets with alignment guides
- Resolution-aware Auto layout
- Layout saving for widget position, size, opacity, and visibility
- Control Center for widget visibility, global opacity, startup launch, and layout control
- Memo widget with collapsible title/content cards and per-memo reset rules
- System widget with CPU, memory, GPU, network, app memory, and graph views
- Sound Mixer widget with master volume, per-session volume, mute controls, and device selection
- Playback and recording device selection with MMDevices fallback
- Per-application output device selection where supported by Windows audio policy
- Calendar widget with event title, location, start time, and end time
- Timer / Stopwatch widget with optional Windows notification alarm
- Quick Launcher with drag-and-drop shortcuts, categories, icon-only mode, and category deletion
- System tray icon support
- Optional Windows startup registration
- Transparent-background app icon

## Release Artifact

Recommended release asset name:

```text
Windget-v0.1.6-win-x64.zip
```

## AI Usage Disclosure

This project was planned, implemented, and documented with assistance from AI tools. Feature direction, UI decisions, testing feedback, and final behavior were refined through user review.
