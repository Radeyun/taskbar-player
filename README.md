# Taskbar Player

A tiny Windows 10 app that shows the current track on the taskbar, right next to the notification area,
with buttons to control the player.

![Taskbar Player on the taskbar](docs/screenshot.png)

[Русская версия](README.ru.md)

- Shows **Artist — Title** of whatever is playing: Spotify, a browser, any player that shows up in
  the Windows media overlay. If several are playing, Spotify wins.
- **⏮ ⏯ ⏭** buttons right on the taskbar.
- The title slides left/right when you switch tracks.
- Follows the taskbar theme (light/dark) and DPI scaling.
- No Spotify account, no API keys, no login: it uses the system media controls (GSMTC).
- About 80 KB, a single exe, nothing to install besides itself. Almost zero CPU when idle.

## Install

1. Download `TaskbarPlayer.exe` from the [latest release](../../releases/latest).
2. Run it and click **Yes** in the "Install Taskbar Player?" dialog.

That's it. The app copies itself to `%LOCALAPPDATA%\Programs\TaskbarPlayer`, starts, and will start with
Windows from now on. No admin rights needed.

> **Windows SmartScreen** may say "Windows protected your PC" because the exe is not code-signed.
> Click **More info → Run anyway**.

**Update:** download the new `TaskbarPlayer.exe`, run it and click **Yes**. The running copy is replaced and restarted.

**Uninstall:** right-click the plaque → **Uninstall**.

**Portable use:** click **No** in the install dialog (or run with `--portable`) to just run the exe from where it is.

## Controls

| Action | Result |
|---|---|
| ⏮ / ⏯ / ⏭ buttons | Previous track / play-pause / next track |
| Left click on the title | Copy "Artist — Title" to the clipboard ("Copied" flashes for a second) |
| Middle click | Play / pause |
| Mouse wheel up / down | Next / previous track |
| Right click | Menu: Pause/Play, Next track, Previous track, Copy, Start with Windows, Uninstall, Exit |
| Hover | Tooltip with the full title |

When paused, the title is dimmed. When nothing is playing, the plaque shows "—".

The slide animation only runs when the track actually changes. When "previous" just rewinds the current
song to its start (Spotify does that after the first ~3 seconds), the title stays put.

## Requirements

- Windows 10, version 1903 or newer, horizontal taskbar on the primary monitor.
- .NET Framework 4.8, which is already part of Windows 10 1903+.

Windows 11 has a different taskbar. The app should fall back to an overlay window there, but this is untested.

## Command-line options

| Option | Meaning |
|---|---|
| `--width=300` | Width of the title area in pixels at 100% scale (default 260; the three buttons add 90) |
| `--portable` | Never offer to install |
| `--log` | Write diagnostics to `%LOCALAPPDATA%\TaskbarPlayer\error.log` (crashes are always logged there) |
| `--fallback` | Don't embed into the taskbar, use the overlay window instead |

To use options with autostart, edit the `TaskbarPlayer` value under
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`.

## Building from source

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer) or Visual Studio 2022.

```bash
dotnet build -c Release
```

The exe ends up in `bin\Release\net48\TaskbarPlayer.exe`. Pushing a `v*` tag builds it on GitHub Actions
and attaches it to a release (see [.github/workflows/build.yml](.github/workflows/build.yml)).

## How it works

```
src/
  Program.cs          entry point: install prompt, single instance, error handlers
  Installer.cs        per-user install, autostart (HKCU Run key), update, uninstall
  TrayApp.cs          wires everything: timers, mouse, buttons, animation, menu, clipboard, theme
  MediaService.cs     GSMTC: picks the session (Spotify first), events, player commands
  TaskbarHost.cs      embedding into the taskbar and the overlay fallback
  PlaqueWindow.cs     layered plaque window (UpdateLayeredWindow), tooltips, mouse input
  PlaqueRenderer.cs   layout (title + buttons) and drawing, including the slide animation
  ControllerWindow.cs hidden window for "TaskbarCreated", WM_SETTINGCHANGE, etc.
  WinEventHook.cs     SetWinEventHook wrapper
  Native.cs           P/Invoke declarations
  AppConfig.cs        settings
  Compat.cs, Log.cs   helpers
```

- **Embedding** works like [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor):
  `Shell_TrayWnd → ReBarWindow32 → MSTaskSwWClass`. The task button list is narrowed by the plaque width,
  and the plaque becomes a child window of `Shell_TrayWnd` in the freed space. When the taskbar re-lays
  itself out (a new tray icon, a resolution change), a WinEvent hook and a 1-second timer narrow it again.
  On exit the original width is restored.
- **Fallback:** if embedding fails, a borderless topmost tool window sits left of `TrayNotifyWnd`.
  It hides when a full-screen app is running.
- **explorer.exe restarts** are handled through the `TaskbarCreated` message.
- **Media data** comes from `Windows.Media.Control` events. A 2-second poll is only a safety net.

## Known limitations

- Primary monitor and horizontal taskbar only. With a vertical taskbar the plaque hides.
- The mouse wheel works when "Scroll inactive windows when I hover over them" is on (the Windows default).
- Text uses grayscale antialiasing, because ClearType can't be drawn on a transparent background.
- If the process is killed (Task Manager, `taskkill /f`), the task buttons stay narrowed until the taskbar
  re-lays itself out. Use **Exit** from the menu.

## License

[MIT](LICENSE)
