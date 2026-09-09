# Chill Clock

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET Framework 4.7.2](https://img.shields.io/badge/.NET%20Framework-4.7.2-blue.svg)](https://dotnet.microsoft.com/download/dotnet-framework/net472)
[![BepInEx](https://img.shields.io/badge/BepInEx-Plugin-green.svg)](https://github.com/BepInEx/BepInEx)

A BepInEx plugin for *Chill with You : Lo-Fi Story*: **when Satone is focusing, it prevents you from opening apps that are not on the whitelist.**

---

[![Chill with You](imgs/header_schinese.jpg)](https://store.steampowered.com/app/3548580/)

> *Chill with You : Lo-Fi Story* is an audiovisual novel about spending your work time with Satone, a girl who loves writing stories. You can customize original music, ambient sounds, and scenery to create an environment that helps you focus—and as your relationship deepens, you might discover something special between you two.

---

## Demo

![Demo](imgs/overview.png)

## What problem does it solve?

<img src="imgs/satone.png" alt="satone" width="300">

### Honestly, it solves my own problem: I get distracted too easily—clicking this, checking that, and the whole day is gone without getting anything done.

- During a focus session, Chill Clock automatically minimizes apps that are **not on your whitelist** to the taskbar;
- Non-whitelisted windows that are already open get minimized;
- Even if you open them again from the Start menu or system tray, they get minimized again;
- Whitelisted apps keep working normally;
- After focus ends / a break starts / you hang up the call, Chill Clock stops interfering.
- Works with both **Pomodoro** and **Count-up** timer modes;
- Settings include "Disable End/Skip in Focus", "Hide Side UI in Focus" and "Block Game Exit in Focus";
- The settings UI is localized in Simplified Chinese / English / Japanese.

## Installation

### Requirements

- *Chill with You : Lo-Fi Story*
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (do not use 6.0)

### Steps

1. **Install BepInEx**
   - Download BepInEx from the link above.
   - Extract it into the game root folder.
   - Run the game once so BepInEx creates its folders (you should see `BepInEx/plugins/`).

2. **Install the Mod**
   - Download the latest `ChillClock.dll` from Releases.
   - Put `ChillClock.dll` into `BepInEx/plugins/`.
   - Your folder structure should look like this:

```
[Game root]/
└── BepInEx/
    └── plugins/
            └── ChillClock.dll
```

## License

This project is released under the [MIT License](LICENSE).

> In short: you are free to use, modify, and distribute it for personal or commercial projects; just keep the copyright notice and license text, and take responsibility for your own use.

## Credits

- Thanks to the [BepInEx](https://github.com/BepInEx/BepInEx) community
- Settings page injection based on [iGPU Savior (Potato Mode)](https://github.com/Small-tailqwq/iGPUSaviorMod)
- Pomodoro hooking based on [LofiNotify](https://github.com/kanghengliu/lofinotify)

> For personal and learning purposes only. Do not sell it directly. Use at your own risk.
