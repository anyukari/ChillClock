# Chill Clock（专注时钟）

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET Framework 4.7.2](https://img.shields.io/badge/.NET%20Framework-4.7.2-blue.svg)](https://dotnet.microsoft.com/download/dotnet-framework/net472)
[![BepInEx](https://img.shields.io/badge/BepInEx-Plugin-green.svg)](https://github.com/BepInEx/BepInEx)

一个用于游戏《放松时光：与你共享Lo-Fi故事》的 BepInEx 插件：**在聪音专注时禁止打开白名单以外的应用**

---

[![Chill with You](imgs/header_schinese.jpg)](https://store.steampowered.com/app/3548580/)

> 「放松时光：与你共享Lo-Fi故事」是一个与喜欢写故事的女孩聪音一起工作的有声小说游戏。您可以自定义艺术家的原创乐曲、环境音和风景，以营造一个专注于工作的环境。在与聪音的关系加深的过程中，您可能会发现与她之间的特别联系。
---
## 0.6.0 更新内容

**语音**

- 扩充到 **1172 条**台词，覆盖专注闲聊、休息提醒、走神、任务管理器、退出拦截、点击回应六类场景；全部按语音时长校对过，没有"念半句就没声"的情况
- **联动台词**：多个池子都有"一次说两三句"的连播组（点击、提醒、闲聊等），按时段（早/午/晚/夜）分开写，不会串场
- 语音包**内嵌进 DLL**，发布形态只有一个 `ChillClock.dll`（约 57 MB），不需要额外的语音文件夹

**点击聪音**

- 专注中点她，用扩充的台词回应；她**边干活边回头说**（用游戏自己的 look 参数 0 / 0.5 / 1），不会停下手头的工作
- 她还在说话时点她，走**游戏自己的"现在不能反应"**（禁止光标），不打断、不叠音
- 番茄钟到点、游戏自己要开口时，我们会先停掉自己那句，避免两边同时说

**表现细节**

- 动作 / 表情 / 转头全部调用游戏接口（`ChangeHeroineAnimationForInteger` / `ChangeHeroineFacialAnimation` / `ChangeLookScaleByManual`），规则照抄游戏自己的 `CommandChangeMotion`
- 口型跟着音频**真正出声的段落**开关，句子中间的停顿会闭嘴
- 字幕使用游戏自带字幕框，并且**等游戏打字机打完**才收起（用它的 `OnTextShowed` 回调），不会一闪而过

**其它修复**

- 不再抢窗口焦点（之前会导致任务栏图标一直闪红光）
- 开场收窗口不再出声，也不会被记成"走神"提醒
- 提醒类台词不再做多余动作；生气表情密度下调

---
## 效果演示：

![alt text](imgs/overview.png)
## 它解决什么问题
<img src="imgs/satone.png" alt="satone" width="300">

### 专注软件首先得能专注！专注，唯有专注！

- Chill Clock 会在专注期间自动把**不在白名单里的应用最小化到任务栏**；
- 即使你再次打开它们，也会被继续最小化；
- 专注结束 / 休息 / 结束通话后，Chill Clock 停止干预。
- 同时支持**番茄钟**与**正计时**两种计时模式；
- 设置页提供「专注时禁止结束/跳过」「专注时隐藏 UI」「专注时禁止关闭游戏」；
- 走神、打开任务管理器或尝试关闭游戏时，聪音会播放语音提醒；
- 设置页内置简体中文 / English / 日本語。
- 试了让应用隐藏和应用置顶的方案，结果都不太好用，要是强退游戏还会出现些后遗症，最终还是让应用最小化这个方案稳妥点

## 安装步骤

### 前置环境要求

- 游戏《放松时光：与你共享Lo-Fi故事》
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases)（请勿使用 6.0）

### 步骤
1. **安装 BepInEx**
* 从上方链接下载 BepInEx。
* 解压至游戏根目录。
* 运行一次游戏以生成 BepInEx 相关文件夹（能看到 `[游戏根目录]/BepInEx/plugins/`）。

2. **安装 Mod**
* 从 Release 下载最新版本的 `ChillClock.dll`。
* 将 `ChillClock.dll` 放入`BepInEx/plugins/` 目录下。
* 确保你的文件夹结构如下所示：


```
[游戏根目录]/
└── BepInEx/
    └── plugins/
            └── ChillClock.dll
```

## 关于其他Mod

如果您对此游戏其他Mod感兴趣，可参见：[awesome-chillwithyou](https://github.com/clsty/awesome-chillwithyou)

## 开源协议

本项目采用 [MIT License](LICENSE)。

> 简单说：你可以自由使用、修改、分发、用于个人或商业项目；
> 只需保留版权声明和许可文本，并为自己的使用行为负责。

## 致谢

- 感谢 [BepInEx](https://github.com/BepInEx/BepInEx) 社区
- 设置页注入思路参考 [iGPU Savior (Potato Mode)](https://github.com/Small-tailqwq/iGPUSaviorMod)
- 番茄钟挂钩思路参考 [LofiNotify](https://github.com/kanghengliu/lofinotify)

> 本插件仅供学习交流，请支持正版游戏。
