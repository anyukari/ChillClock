# Chill Clock

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md)

*Chill with You : Lo-Fi Story* 向けの BepInEx プラグインです。ゲーム内のポモドーロタイマーを、デスクトップ上の「集中タイマー」として活用できるようにします。

『*Chill with You : Lo-Fi Story*』は、物語を書くことが好きな女の子「サトネ」と一緒に作業するオーディオビジュアルノベルです。お気に入りのアーティスト楽曲や環境音、景色を自由にカスタマイズして、作業に集中できる環境を作りましょう。絆を深めていくと、ふたりだけの特別なつながりが見つかるかもしれません。

---

## できること

ゲームでポモドーロの「集中(Focus)」を開始すると、**ホワイトリストにないアプリを自動的にタスクバーへ最小化**します。

- 開いているアプリをタスクバーへ最小化
- スタートメニューやトレイから再度開こうとしても、すぐに最小化し直します
- ホワイトリスト登録済みアプリ(エディタ・ブラウザ・メモなど)は通常どおり使えます
- 集中終了 / 休憩 / 通話終了後は介入をやめますが、**ウィンドウを自動で元に戻したりはしません**

## 主な機能

- `PomodoroService` のメソッド・イベントから集中状態を検出
- 集中中は約 0.2 秒ごとに非ホワイトリスト窓を最小化
- ゲーム標準の設定画面に「Chill Clock」ページを追加
- ページ最上部に「Chill Clock を有効化」トグル
- ホワイトリスト管理:
  - アプリアイコン付きで一覧表示
  - ワンタップで削除
  - 「ファイルから追加」: Windows 標準のファイル選択ダイアログ
  - 「開いているウィンドウから追加」: 実行中/トレイのアプリを一覧表示して追加
  - 重複追加時は通知を表示し、選択ページを閉じずに続行
- ホワイトリストはプラグイン同梱フォルダの `FocusWhitelist.txt` に保存

## インストール

### 必要なもの

- *Chill with You : Lo-Fi Story*
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases)(6.0 は使用しないでください)

### 手順

1. BepInEx が導入済みであることを確認します(`BepInEx/plugins/` がある状態)。
2. ビルドした `ChillClock.dll` を次へ配置:

```text
BepInEx\plugins\
```

3. ゲームを起動し、設定画面から「Chill Clock」タブを開きます。
4. 初めてアプリを追加すると `FocusWhitelist.txt` が生成されます。

## ホワイトリストの形式

1 行につき 1 アプリ。フルパスまたは exe 名を指定できます:

```text
C:\Program Files\Google\Chrome\Application\chrome.exe
notepad.exe
```

`#` から始まる行はコメント、ダブルクォートも利用できます。

## ビルド

[.NET SDK](https://dotnet.microsoft.com/download)(8.0 以上)が必要です。

```powershell
.\build.ps1
```

ゲームが別の場所にある場合:

```powershell
.\build.ps1 -GameDir "ゲームのパス"
```

出力: `bin\Release\ChillClock.dll`

## 仕組み

- `PomodoroService.StartPomodoro / OnTimerEnd / ResetTimer / CompletePomodoroTimer` をフック
- `EnumWindows / ShowWindow` で可視トップレベルウィンドウを周期的に走査
- 非ホワイトリスト窓を最小化(タスクバーアイコンは維持)
- 集中終了時は制御を解除(ウィンドウは自動復元しない)
- 設定ページは `SettingUI.Setup / Activate` への Harmony フックで注入

## ライセンス

[MIT License](LICENSE)。詳細は [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) もご覧ください。

## 謝辞

- [BepInEx](https://github.com/BepInEx/BepInEx)
- UI 注入の参考: [iGPU Savior](https://github.com/Small-tailqwq/iGPUSaviorMod)
- ポモドーロフックの参考: [LofiNotify](https://github.com/kanghengliu/lofinotify)
- 発想・UI の参考: [FocusClock](https://github.com/mmmmagic/Clock)、[Chill Music Information Sync](https://github.com/Cainongw/ChillMusicInformationSyncMod)

学習・交流目的でお使いください。自己責任でご利用ください。
