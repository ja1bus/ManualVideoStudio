# Third-party notices / 第三者ソフトウェアについて

The MIT License in [LICENSE](LICENSE) applies only to the source code and files in this repository authored by ja1bus. It does not change the license of the third-party software listed below.

[LICENSE](LICENSE) の MIT License は、このリポジトリに含まれる ja1bus が作成したソースコードとファイルだけに適用されます。以下の第三者のソフトウェアのライセンスは変更しません。

## FFmpeg (external, not included) / FFmpeg（外部。同梱していません）

ManualVideoStudio uses [FFmpeg](https://ffmpeg.org/) (ffmpeg.exe and ffprobe.exe) to record, analyze and export videos. FFmpeg is **not included** in this repository or in the app, and its source code is not used. The user obtains FFmpeg separately, and the app runs it as a separate process on the user's PC.

ManualVideoStudio は、動画の録画・解析・書き出しに [FFmpeg](https://ffmpeg.org/)（ffmpeg.exe と ffprobe.exe）を使います。FFmpeg はこのリポジトリにもアプリにも**含まれておらず**、そのソースコードも使っていません。利用者が FFmpeg を別に入手し、アプリはそれを利用者の PC 上で別のプロセスとして実行します。

FFmpeg is licensed under the GNU LGPL version 2.1 or later; builds that enable optional components (such as many "full" builds) are licensed under the GNU GPL. The license of the build you obtain applies to it. See https://ffmpeg.org/legal.html.

FFmpeg のライセンスは GNU LGPL 2.1 以降で、オプションの機能を有効にしたビルド（多くの「full」ビルドなど）は GNU GPL です。入手したビルドには、そのビルドのライセンスが適用されます。詳しくは https://ffmpeg.org/legal.html をご覧ください。

## Runtime / 実行時

ManualVideoStudio runs on .NET 8 and its Windows Desktop shared framework (WPF, System.Text.Json), provided by Microsoft under the MIT License. They are not included in this repository. (The Microsoft Store package includes the .NET runtime.)

ManualVideoStudio は .NET 8 と Windows Desktop 共有フレームワーク（WPF・System.Text.Json）で動作します。これらは Microsoft が MIT License で提供しているもので、このリポジトリには含まれていません（Microsoft Store のパッケージには .NET のランタイムを含めています）。

- .NET: https://github.com/dotnet/runtime (MIT)
- WPF: https://github.com/dotnet/wpf (MIT)

The following NuGet packages are referenced by the app. They are restored from nuget.org at build time and are not included in this repository; their DLLs are part of the built app.

次の NuGet パッケージをアプリから参照しています。ビルド時に nuget.org から取得するもので、このリポジトリには含まれていません（ビルドしたアプリには DLL として含まれます）。

| Package | Version | License | Referenced by |
|---|---|---|---|
| CommunityToolkit.Mvvm | 8.3.2 | MIT | `ManualVideoStudio.csproj` |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | MIT | `ManualVideoStudio.csproj` |
| Microsoft.Extensions.Logging | 8.0.1 | MIT | `ManualVideoStudio.csproj` |
| Microsoft.Extensions.Logging.Abstractions | 8.0.2 | MIT | `Core/ManualVideoStudio.Core.csproj` |

Fonts are not bundled. The app uses fonts installed in Windows (such as Meiryo, Yu Gothic UI and Segoe UI) for its UI, and the font chosen by the user for subtitles.

フォントは同梱していません。画面には Windows にインストールされているフォント（Meiryo・Yu Gothic UI・Segoe UI など）を、字幕には利用者が選んだインストール済みのフォントを使います。

## Tests only / テストのみ

The following NuGet packages are referenced by `Tests/ManualVideoStudio.Tests.csproj` and `IntegrationTests/ManualVideoStudio.IntegrationTests.csproj`. They are restored from nuget.org at build time, are not included in this repository, and are not part of the app.

次の NuGet パッケージは `Tests/ManualVideoStudio.Tests.csproj` と `IntegrationTests/ManualVideoStudio.IntegrationTests.csproj` から参照しています。ビルド時に nuget.org から取得するもので、このリポジトリにもアプリにも含まれていません。

| Package | Version | License |
|---|---|---|
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |

## External service / 外部サービス

The Gemini assistance features only copy a prompt to the clipboard and open Gemini (a Google service) in the browser. No Google software or API is used. Content you send to Gemini is handled under Google's terms and policies.

Gemini の連携機能は、依頼文をクリップボードにコピーし、ブラウザで Gemini（Google のサービス）を開くだけです。Google のソフトウェアや API は使っていません。Gemini に送った内容は Google の規約とポリシーに従って扱われます。
