# 开发约定（ChillClock / ChillFocusWhitelist）

## 第一原则：能用游戏自带的功能，就用游戏自带的

游戏里已经有的行为，一律优先调用它自己的接口，不要自己造一套。理由：自带的表现
（时机、动画、音效、UI 反馈）都是它自己调好的，我们复刻的只会更差；而且自造的
东西容易和游戏的状态机打架（这个项目已经踩过很多次：字幕框状态、口型开关、
点击反应、动作段错配……）。

具体到已有实现：

- 字幕：走游戏自己的 `Bulbul.StorySystemUI`（打字机、字体、淡入淡出都用它的）
- 语音：走游戏自己的 `KanKikuchi.AudioManager.VoiceManager.Play`（音量跟游戏设置）
- 动作 / 表情 / 转头：走 `HeroineService.ChangeHeroineAnimationForInteger` /
  `ChangeHeroineFacialAnimation` / `ChangeLookScaleByManual`，规则照抄
  `ScenarioReader.CommandChangeMotion`（身体动作 -1 = 不改，look 0/0.5/1）
- 点击时的"现在不能反应"：靠 `FacilityClickHeroine.ReactionReady` 返回 false，
  用游戏自己的禁止光标反馈，不要自己吞掉点击
- 她忙的时候（演出 / 离席 / 睡觉 / 野生动作）一律让路，不去抢她的动画

只有游戏确实没有对应机制时才自己写（例如：隐藏非白名单窗口、按我们自己的冷却
挑选台词）。

## 维护习惯

- 语音目录 `outputs\ChillClockVoicePack\Voices\voice_catalog.tsv` 是唯一数据源；
  改动都留 `tools\translations-*.tsv` / `lines-*.txt` 存档，方便回滚
- 改完必须：重打包（build-voice-pack.py）→ 重新编译 → 部署，并核对两边 DLL 哈希一致
- 游戏运行时 DLL 会被占用，替换前先确认 `Chill With You` 进程已退出
