# Chill Clock（专注时钟）

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md)

一个用于游戏《放松时光：与你共享Lo-Fi故事》的 BepInEx 插件：把游戏内置的番茄钟变成你的桌面专注助手。

「放松时光：与你共享Lo-Fi故事」是一个与喜欢写故事的女孩聪音一起工作的有声小说游戏。您可以自定义艺术家的原创乐曲、环境音和风景，以营造一个专注于工作的环境。在与聪音的关系加深的过程中，您可能会发现与她之间的特别联系。

---

## 它解决什么问题

用游戏开始一个番茄钟「专注」时，你的任务是把注意力留在当前工作上——但桌面上总有别的窗口会跳出来打扰你。

Chill Clock 会在专注期间自动把**不在白名单里的应用最小化到任务栏**：

- 已经打开的非白名单窗口会被收进任务栏；
- 即使你从开始菜单或托盘再次打开它们，也会被继续最小化；
- 白名单里的应用（比如编辑器、浏览器、笔记）不受影响，可以正常使用；
- 专注结束 / 休息 / 结束通话后，Chill Clock 停止干预，但**不会把窗口全部自动弹回**，你按自己的节奏点开即可。

## 功能

- 自动识别游戏番茄钟的专注状态（`PomodoroService` 状态 + 事件双重监听）
- 专注期间持续巡逻（约 0.2 秒一次），反复压制非白名单窗口
- 在游戏原生设置页新增「Chill Clock」页，UI 与游戏原版设置保持一致
- 页面顶部「启用 Chill Clock」总开关
- 白名单管理：
  - 显示当前白名单应用（带应用图标）
  - 一键删除
  - 「从文件添加应用」：调用 Windows 自带文件选择器
  - 「从当前窗口添加应用」：列出当前可见窗口/托盘应用，逐条添加
  - 重复添加会居中提示且停留在选择页
- 白名单保存在插件目录 `FocusWhitelist.txt`，可手工编辑或分享

## 安装

### 前置

- 游戏《放松时光：与你共享Lo-Fi故事》
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases)（请勿使用 6.0）

### 步骤

1. 确认游戏目录已正确安装 BepInEx（能看到 `BepInEx/plugins/`）。
2. 将编译好的 `ChillClock.dll` 放入：

```text
BepInEx\plugins\
```

3. 启动游戏，进入设置页即可看到「Chill Clock」标签。
4. 首次添加白名单后，会在插件目录生成 `FocusWhitelist.txt`。

## 白名单文件格式

`FocusWhitelist.txt` 每行一个应用，支持完整路径或 exe 名称：

```text
C:\Program Files\Google\Chrome\Application\chrome.exe
notepad.exe
```

支持 `#` 注释和英文双引号包裹路径。

## 如何构建

需要 [.NET SDK](https://dotnet.microsoft.com/download)（8.0 或更高）。

```powershell
.\build.ps1
```

默认游戏目录为：

```text
E:\SOFT\STEAM\steamapps\common\Chill with You Lo-Fi Story
```

如果你的游戏装在其他位置，使用：

```powershell
.\build.ps1 -GameDir "你的游戏目录"
```

构建结果位于 `bin\Release\ChillClock.dll`。

## 工作原理（技术细节）

- 游戏番茄钟由 `Bulbul.PomodoroService` 管理。插件直接挂钩
  `StartPomodoro / OnTimerEnd / ResetTimer / CompletePomodoroTimer`，
  不依赖 DI 容器是否解析成功，保证状态切换可靠。
- 专注开启后，Chill Clock 通过 Win32 `EnumWindows / ShowWindow` 周期扫描可见顶层窗口；
  对非白名单窗口执行最小化（保留任务栏图标），白名单窗口跳过。
- 专注结束/休息/完成时解除干预，不强制恢复窗口。
- 设置页通过 Harmony 挂钩 `SettingUI.Setup / Activate` 注入，克隆游戏原生标签与页面容器，
  行布局、字体与原生设置保持一致。

## 开源协议

本项目采用 [MIT License](LICENSE)。

> 简单说：你可以自由使用、修改、分发、用于个人或商业项目；
> 只需保留版权声明和许可文本，并为自己的使用行为负责。

## 致谢

- 感谢 [BepInEx](https://github.com/BepInEx/BepInEx) 社区
- 设置页注入思路参考 [iGPU Savior (Potato Mode)](https://github.com/Small-tailqwq/iGPUSaviorMod)
- 番茄钟挂钩思路参考 [LofiNotify](https://github.com/kanghengliu/lofinotify)
- 灵感与 UI 参考 [FocusClock](https://github.com/mmmmagic/Clock) 与
  [Chill Music Information Sync](https://github.com/Cainongw/ChillMusicInformationSyncMod)

> 本插件仅供学习交流，请勿直接售卖。使用产生的任何问题由使用者自行承担。
