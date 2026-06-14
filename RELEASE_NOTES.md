# Windget v0.1.2

Windget v0.1.2 is a patch release focused on Sound Mixer device switching reliability.

Earlier local development builds used internal version labels, and the public GitHub release history starts from `v0.1.0`.

## Fixes

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
Windget-v0.1.2-win-x64.zip
```

## AI Usage Disclosure

This project was planned, implemented, and documented with assistance from AI tools. Feature direction, UI decisions, testing feedback, and final behavior were refined through user review.
