# Chill Clock

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md)

A BepInEx plugin for *Chill with You : Lo-Fi Story* that turns the game's built-in Pomodoro timer into a desktop focus assistant.

*Chill with You : Lo-Fi Story* is an audiovisual novel about spending your work time with Satone, a girl who loves writing stories. You can customize original music, ambient sounds, and scenery to create an environment that helps you focus—and as your relationship deepens, you might discover something special between you two.

---

## What it does

When you start a Pomodoro "Focus" session in the game, Chill Clock automatically minimizes applications that are **not on your whitelist** to the taskbar:

- Windows already open are minimized to the taskbar.
- If you try to reopen them from the Start menu or system tray, they are minimized again.
- Whitelisted apps (editor, browser, notes, etc.) keep working normally.
- After focus ends / a break starts / you hang up the call, Chill Clock stops interfering but does **not** force all windows back open.

## Features

- Detects the game's focus state via `PomodoroService` methods and events
- Keeps suppressing non-whitelisted windows about every 0.2 s while focusing
- Adds a native-looking "Chill Clock" page to the in-game settings
- Global "Enable Chill Clock" toggle at the top of the page
- Whitelist management:
  - Shows whitelisted apps with app icons
  - One-tap delete
  - "Add app from file": uses the native Windows file picker
  - "Add app from open windows": lists running/tray apps and adds them one by one
  - Duplicates show a notice without closing the picker page
- Whitelist stored in `FocusWhitelist.txt` next to the plugin

## Installation

### Requirements

- *Chill with You : Lo-Fi Story*
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (do not use 6.0)

### Steps

1. Make sure BepInEx is installed (you can see `BepInEx/plugins/`).
2. Put the compiled `ChillClock.dll` into:

```text
BepInEx\plugins\
```

3. Start the game and open Settings; you will see the "Chill Clock" tab.
4. The first time you add an app, `FocusWhitelist.txt` is created next to the plugin.

## Whitelist format

One app per line; a full path or an exe name is accepted:

```text
C:\Program Files\Google\Chrome\Application\chrome.exe
notepad.exe
```

Lines starting with `#` are comments; double quotes are supported.

## Build

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (8.0+).

```powershell
.\build.ps1
```

Default game path:

```text
E:\SOFT\STEAM\steamapps\common\Chill with You Lo-Fi Story
```

Override with:

```powershell
.\build.ps1 -GameDir "your game directory"
```

Output: `bin\Release\ChillClock.dll`

## How it works

- Hooks `PomodoroService.StartPomodoro / OnTimerEnd / ResetTimer / CompletePomodoroTimer`
- Periodically enumerates visible top-level windows (`EnumWindows / ShowWindow`)
- Minimizes non-whitelisted windows (taskbar icons stay visible)
- Releases control at focus end without auto-restoring windows
- Settings page injected through Harmony patches on `SettingUI.Setup / Activate`

## License

[MIT License](LICENSE). See also [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Credits

- [BepInEx](https://github.com/BepInEx/BepInEx)
- UI injection reference: [iGPU Savior](https://github.com/Small-tailqwq/iGPUSaviorMod)
- Pomodoro hook reference: [LofiNotify](https://github.com/kanghengliu/lofinotify)
- Inspiration / UI: [FocusClock](https://github.com/mmmmagic/Clock), [Chill Music Information Sync](https://github.com/Cainongw/ChillMusicInformationSyncMod)

For personal/learning use. Use at your own risk.
