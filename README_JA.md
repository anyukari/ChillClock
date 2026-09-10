# Chill Clock

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET Framework 4.7.2](https://img.shields.io/badge/.NET%20Framework-4.7.2-blue.svg)](https://dotnet.microsoft.com/download/dotnet-framework/net472)
[![BepInEx](https://img.shields.io/badge/BepInEx-Plugin-green.svg)](https://github.com/BepInEx/BepInEx)

*Chill with You : Lo-Fi Story* 向けの BepInEx プラグインです。**サトネが集中している間、ホワイトリスト以外のアプリを開けないようにします。**

---

[![Chill with You](imgs/header_schinese.jpg)](https://store.steampowered.com/app/3548580/)

> 『*Chill with You : Lo-Fi Story*』は、物語を書くことが好きな女の子「サトネ」と一緒に作業するオーディオビジュアルノベルです。お気に入りのアーティスト楽曲や環境音、景色を自由にカスタマイズして、作業に集中できる環境を作りましょう。絆を深めていくと、ふたりだけの特別なつながりが見つかるかもしれません。

---

## デモ

![デモ](imgs/overview_jp.png)

## 何が解決できるの?

<img src="imgs/satone.png" alt="satone" width="300">

### 正直なところ、これは自分の集中力のなさを解決するためのプラグインです。あれこれ触って気づいたら一日終わってた、なんて経験ありませんか?

- 集中(Focus)中は、**ホワイトリストにないアプリを自動的にタスクバーへ最小化**します;
- 開いている非ホワイトリストのウィンドウも最小化されます;
- スタートメニューやトレイから再度開こうとしても、すぐに最小化し直されます;
- ホワイトリスト登録済みのアプリは通常どおり使えます;
- 集中終了 / 休憩 / 通話終了後は、Chill Clock は介入をやめます。
- **ポモドーロ**と**ストップウォッチ(正計時)**の両モードに対応;
- 設定に「集中中は終了/スキップ禁止」「集中中は右側UIを隠す」「集中中はゲーム終了禁止」を追加;
- 集中を妨げる操作やタスクマネージャーの起動、ゲーム終了を試みた時にサトネがボイスで注意;
- 設定UIは簡体中文 / English / 日本語に対応。

## インストール

### 必要なもの

- *Chill with You : Lo-Fi Story*
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases)(6.0 は使用しないでください)

### 手順

1. **BepInEx をインストール**
   - 上のリンクから BepInEx をダウンロードします。
   - ゲームのルートフォルダに解凍します。
   - ゲームを一度起動して、BepInEx 関連フォルダを生成させます(`BepInEx/plugins/` ができていれば OK)。

2. **Mod をインストール**
   - Release から最新の `ChillClock.dll` をダウンロードします。
   - `ChillClock.dll` を `BepInEx/plugins/` に配置します。
   - フォルダ構成は次のようになります:

```
[ゲームルート]/
└── BepInEx/
    └── plugins/
            └── ChillClock.dll
```

## ライセンス

このプロジェクトは [MIT License](LICENSE) で公開されています。

> 簡単に言うと:個人・商用を問わず自由に利用・改変・再配布できます。ただし著作権表示とライセンス文を残し、利用は自己責任でお願いします。

## 謝辞

- [BepInEx](https://github.com/BepInEx/BepInEx) コミュニティに感謝します
- 設定ページ注入の参考: [iGPU Savior (Potato Mode)](https://github.com/Small-tailqwq/iGPUSaviorMod)
- ポモドーロフックの参考: [LofiNotify](https://github.com/kanghengliu/lofinotify)

> 学習・交流目的のみ。直接販売はしないでください。利用による問題は自己責任です。
