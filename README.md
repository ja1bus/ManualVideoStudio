# ManualVideoStudio

[English](README.en.md) | 日本語

ManualVideoStudio は、パソコンの操作を録画し、いらない部分を切り取り、字幕や矢印を付けた「操作説明の動画」を作る Windows 11 用のアプリです。MP4・Animated GIF での書き出しや、手順書に貼る画像の切り出しもできます。アプリと取扱説明書は日本語のみです。

このリポジトリは、一般公開している ManualVideoStudio のソースコードです。

## 主な機能

- 画面の範囲・ウィンドウ・モニターを録画（15 / 30 fps、カーソルあり / なし。範囲は横 / 縦の切り替えと縦横比の固定に対応。マイク録音は既定で OFF）
- 待ち時間や誤操作を「削除」または「短縮」するカット。つなぎ目に 0.3 秒のクロスフェード（設定で OFF 可）。FFmpeg の静止・無音・画面変化の検出から、カットの**候補**を提示（採用するかは人が決める）
- 字幕（見た目 4 種・インストール済みのフォント）、四角形・矢印・直線・文字・モザイクの図形（表示時間つき）、動画冒頭のタイトルを追加
- MP4・Animated GIF（軽量 / 標準 / 高品質）で書き出し。字幕・図形は焼き込み
- 手順書用の静止画（`連番_キャプション.png`）を切り出し、四角形・矢印・直線・文字・モザイクの注釈を追加
- Gemini（ブラウザ）に字幕・キャプションの案を作ってもらい、確かめて取り込む（依頼文のコピーと回答の貼り付けによる手動の連携。Gemini API は使いません）
- プロジェクトごとにフォルダーを作って元動画・書き出したファイルをまとめ、プロジェクト一覧で開く・複製・名前の変更・削除（ごみ箱へ）・zip のエクスポート / インポート
- 簡易モード（録画・カット・字幕・図形・書き出し）と全機能モード（手順書用の画像・AI 連携も表示）を設定で切り替え

本アプリはインターネットとの通信を行いません。録画には、その間に画面に表示されていた内容がそのまま含まれます（詳しくは下記のプライバシーポリシー）。

## 対応 OS と実行環境

- Windows 11（64 ビット、x64）
- **FFmpeg（ffmpeg.exe と ffprobe.exe）を別途用意する必要があります。** FFmpeg はアプリにもこのリポジトリにも含まれていません。字幕の焼き込みに libass（`subtitles` フィルター）、録画に `gdigrab`（マイクは `dshow`）が必要なため、full build など字幕機能入りのものを選んでください（確認済み: 9.0.1、gyan.dev の full build）。PATH が通っていれば自動で見つけます。見つからない場合は［ツール］→［設定］→［FFmpeg］で場所を指定します。
- Microsoft Store 版は .NET を内包しているため、.NET の追加のインストールは不要です。
- このリポジトリのソースからビルドして実行する場合は、[.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。

## インストール

正式な配布先は Microsoft Store です。通常はこちらから入手してください（無料。日本の Microsoft Store で配布しています）。

- Microsoft Store: https://apps.microsoft.com/detail/9N33XK4M3JFJ

この GitHub リポジトリでは実行ファイル（バイナリ）を配布していません。

## 基本的な使い方

1. 初めに FFmpeg を用意します（取扱説明書の「3-1. FFmpeg を用意する」）。見つかると、主画面の右下に FFmpeg のバージョンが出ます。
2. 開始画面で［● 録画する］か［動画を開く］を選びます。動画を開くと、保存先（既定はビデオフォルダーの `ManualVideoStudio`）に「日時_名前」のプロジェクトフォルダーを作り、動画をそこへ移動します。
3. 不要な部分をカットして確定し、字幕・図形・タイトルを付けます。
4. ［書き出し ▾］から MP4 または Animated GIF を書き出します。

詳しくは取扱説明書をご覧ください。アプリの［ヘルプ］（F1）からも開けます。

- 取扱説明書: https://www.iwanaga.net/manualvideostudio/manual/

## ビルド方法

### 必要な環境

- Windows 11 x64
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)（確認済み: 8.0.425）
- `dotnet` CLI だけで作れます（Visual Studio は不要）。
- アプリは NuGet パッケージ `CommunityToolkit.Mvvm`、`Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.Logging`（Core は `Microsoft.Extensions.Logging.Abstractions`）を使います。テストは `Microsoft.NET.Test.Sdk`、`xunit`、`xunit.runner.visualstudio` を使います（いずれも初回のビルドで nuget.org から取得）。
- ビルドと Unit Test に FFmpeg は不要です。Integration Test とアプリの実行には FFmpeg が必要です。

### コマンド

リポジトリのルートで実行します。`.sln` はなく、各 csproj を個別に扱います。

```powershell
dotnet build ManualVideoStudio.csproj
dotnet test Tests/ManualVideoStudio.Tests.csproj
dotnet run --project ManualVideoStudio.csproj
```

実 FFmpeg を使う Integration Test は、PATH または環境変数 `MVS_FFMPEG`（ffmpeg.exe のパス）で FFmpeg を指定して実行します。実際の画面を録画する試験を含むため、画面をロックしていない状態（リモートデスクトップなら最小化していない状態）で実行してください。

```powershell
dotnet test IntegrationTests/ManualVideoStudio.IntegrationTests.csproj
```

配布用のフォルダー（Framework-dependent、win-x64）を作る場合は次を実行します。出力先は `publish\FrameworkDependent\` で、実行には .NET 8 Desktop Runtime（x64）と FFmpeg が必要です。

```powershell
dotnet publish ManualVideoStudio.csproj -c Release -p:PublishProfile=FrameworkDependent
```

### 構成

| パス | 内容 |
|---|---|
| `ManualVideoStudio.csproj` | アプリ本体（WPF、`net8.0-windows`、MVVM） |
| `App.xaml(.cs)`, `MainWindow.xaml(.cs)` | 起動処理と主画面 |
| `ViewModels/`, `Views/`, `Controls/`, `Converters/` | 画面（主画面・録画・設定・注釈・字幕・プロジェクト一覧など）の ViewModel と View |
| `Core/` | UI に依存しない処理（`net8.0`）。FFmpeg の引数生成と出力の解析、カット・字幕（ASS）・図形・タイトルの編集、録画の状態管理、GIF、AI の回答の解析、プロジェクトと設定の保存 |
| `Imaging/` | 注釈・図形・タイトルの描画と画像の保存（`net8.0-windows`、WPF の描画） |
| `Services/` | ダイアログ・再生・FFmpeg の状態・フォント・ごみ箱・録画対象とマイクの列挙（Win32 / Core Audio）など |
| `Resources/` | 画面の文言（`Strings.resx`、日本語）、アイコン、取扱説明書（`Help/manual.html`。exe に埋め込み） |
| `Tests/` | Unit Test（xUnit。FFmpeg 不要） |
| `IntegrationTests/` | 実 FFmpeg を使う Integration Test（xUnit） |
| `tools/icon-source.png`, `Resources/icon.ico` | アプリアイコン（`pwsh -File tools/make-icon.ps1` で `icon-source.png` から `icon.ico` を作成） |

Microsoft Store 用のパッケージ（MSIX）とインストーラーの作成ツールは、このリポジトリに含まれていません。`ManualVideoStudio.csproj` の `ManualVideoStudioMsix` の条件は Store 版用です。`Resources/Help/manual.html` は取扱説明書の原稿から生成したファイルで、原稿と生成ツールは含まれていません。

設定は `%AppData%\ManualVideoStudio\settings.json` に、キャッシュ・ログは `%LocalAppData%\ManualVideoStudio\` に保存します。

ソースのコメントは主に日本語です。コメント中の「仕様書」「仕様書 §N」などは、このリポジトリに含まれていない内部の開発用仕様書の節を指します。

## ライセンス

[MIT License](LICENSE) で公開しています。

Copyright (c) 2026 ja1bus（© ja1bus）

FFmpeg・NuGet パッケージなど第三者のソフトウェアについては [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) をご覧ください。

## 無保証

本ソフトウェアは「現状のまま」提供され、明示・黙示を問わず、いかなる保証もありません。本ソフトウェアの利用によって生じたいかなる損害についても、作者は責任を負いません（詳細は [LICENSE](LICENSE)）。

## 関連リンク

- Web ページ: https://www.iwanaga.net/manualvideostudio/
- 取扱説明書: https://www.iwanaga.net/manualvideostudio/manual/
- 更新履歴: https://www.iwanaga.net/manualvideostudio/changelog/
- プライバシーポリシー: https://www.iwanaga.net/manualvideostudio/privacy/
- Microsoft Store: https://apps.microsoft.com/detail/9N33XK4M3JFJ
- 連絡先: support@iwanaga.net
