# MyDarts System

VRChatワールド用のマイダーツ拡張です。
プレイヤーごとの **番号 / 所持コイン / ダーツのパーツ構成** をスプレッドシートに保存し、
**別のワールドでも同じ設定を読み込める** ようにします。

VRChatのパーシスタンスはワールド単位なので、ワールドをまたぐにはこの形になっています。

---

## 必要なもの

- Unity 2022.3
- VRChat SDK - Worlds 3.10.4 以降（UdonSharp を含む）
- Googleアカウント（スプレッドシート + Apps Script）
- Cloudflareアカウント（無料枠。Apps Script のリダイレクト回避に使います）

ダーツギミック本体は**同梱していません**。
このパッケージは既存のダーツギミックに **一切触らずに乗せる拡張** として作られています。

---

## 導入

1. VCC でこのパッケージを追加
2. Unity のメニューから **MyDarts → セットアップ**
3. 送信先URLと台帳URLを入れて **生成する** を押す

シーンに `[MyDarts] System` が1つ作られ、中の参照は自動で繋がります。

サーバー側（スプレッドシート / Apps Script / Cloudflare Worker）の手順は
`Documentation~/セットアップ.md` を見てください。

---

## 生成されるもの

```
[MyDarts] System
 ├ Managers
 │   ├ Register              MyDartsRegister   初回登録の送信
 │   └ Fetcher               MyDartsFetcher    台帳の読み込み
 ├ PlayerCanvas              プレイヤーに見せるパネル
 │   └ Panel                 MyDartsStatusPanel
 │       ├ Title / Body      見出しと説明文
 │       └ RegisterGroup     登録UI一式（登録済みなら自動で隠れる）
 │           ├ BuildUrlButton    「登録用URLを作る」
 │           ├ UrlOutput         ① ここからコピー
 │           ├ UrlInput          ② ここに貼り付け（VRCUrlInputField）
 │           └ SendButton        ③ 送信
 └ DebugCanvas               開発用。既定では非表示
     └ Panel                 MyDartsDebugPanel
         ├ State             いま何をしているか
         └ Log               直近のログ
```

---

## セットアップウィンドウの3つのボタン

用途で分けてあります。調整した見た目を壊さずに更新できます。

| ボタン | 何をするか | 位置や見た目の調整 |
|---|---|---|
| **生成する / 作り直す** | 一式を消して新規生成 | 消える |
| **参照を繋ぎ直す** | 階層はそのまま、参照だけ再解決 | **残る** |
| **設定値だけ流し込む** | URLと初期パーツ番号だけ更新 | **残る** |

設定は `Assets/MyDartsSystem Settings/` の `.asset` に保存されるので、
**作り直してもURLを入れ直す必要はありません**。
パッケージを更新しても設定は消えません。

---

## 仕組み

VRChatのUdonは**実行時にURLを組み立てられません**。
そのため送信は「あらかじめ焼き込んだURLを叩き分ける」方式にしています。

```
送信  ワールド → 焼き込み済みURL → Cloudflare Worker → Apps Script → スプレッドシート
読込  スプレッドシート → Apps Script → JSON → ワールド
```

Cloudflare Worker を挟んでいるのは、Apps Script が
`script.googleusercontent.com` へ302リダイレクトし、
VRChat側が `Redirect limit exceeded` で失敗するためです。

初回登録だけはURLのコピペが必要です（生涯1回）。

---

## 既知の制限

- **送信先が許可ドメイン外**なので、VRChat側で
  `Settings → Comfort & Safety → Allow Untrusted URLs` をオンにする必要があります。
  読み込みだけは GitHub Pages に書き出せば設定不要にできます（`Documentation~` 参照）
- 送信URLはワールドファイルから読めるため、**ブラウザで直接叩く不正は原理的に防げません**。
  設定シートの「1日の獲得上限」と履歴シートで運用カバーする前提です
- 別ワールドへ反映されるまで **1〜2分のラグ**があります
- VRでのコピー操作は未検証です。登録はデスクトップ推奨

---

## ライセンス

MIT
