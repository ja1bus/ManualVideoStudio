using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Subtitles;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>
/// v1.22 §44.5 YouTube 用に書き出し（字幕なし MP4＋SRT）の初期試験（試験表 T-47〜T-59）。
/// 偽のダイアログ・進捗画面・動画書き出し・シェルで MainViewModel を動かす。図形・タイトルの画像は WPF で描くため STA で実行する。
/// </summary>
public sealed class YouTubeExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mvs-122-yt-").FullName;

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, true);
    }

    // ---------------- 偽物 ----------------

    internal sealed class Dialogs : IDialogService
    {
        public List<(string Kind, string Message, string? Detail, string Title)> Calls { get; } = [];
        public List<(string Filter, string Name, string? Folder, string Title)> SaveFiles { get; } = [];
        public string? SaveFileResult { get; set; }
        /// <summary>確認の答え。既定は「上書きなどは OK、完了の確認（フォルダーを開く）は いいえ」。</summary>
        public Func<string, string, bool> ConfirmAnswer { get; set; } = (msg, _) => !msg.StartsWith("書き出しが完了", StringComparison.Ordinal);
        public Func<int>? ExportCalls { get; set; }
        public List<int> ExportCallsAtConfirm { get; } = [];
        public IEnumerable<(string Message, string? Detail)> Errors => Calls.Where(c => c.Kind == "error").Select(c => (c.Message, c.Detail));
        public IEnumerable<(string Message, string Title)> Confirms => Calls.Where(c => c.Kind == "confirm").Select(c => (c.Message, c.Title));

        public void ShowInfo(string message, string title = "情報") => Calls.Add(("info", message, null, title));
        public void ShowWarning(string message, string title = "注意") => Calls.Add(("warning", message, null, title));
        public void ShowError(string message, string? detail = null, string title = "エラー") => Calls.Add(("error", message, detail, title));
        public bool Confirm(string message, string title = "確認")
        {
            Calls.Add(("confirm", message, null, title));
            ExportCallsAtConfirm.Add(ExportCalls?.Invoke() ?? 0);
            return ConfirmAnswer(message, title);
        }
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => false;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "ファイルを開く") => null;
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "名前を付けて保存")
        {
            SaveFiles.Add((filter, defaultFileName, initialDirectory, title));
            return SaveFileResult;
        }
        public string? PickFolder(string? initialDirectory = null, string title = "フォルダーの選択") => null;
    }

    internal enum ExportMode { Success, Fail, FFmpegFail }

    internal sealed class Export : IVideoExportService
    {
        public ExportMode Mode { get; set; }
        public List<VideoExportRequest> Requests { get; } = [];
        public List<bool> SrtExistedAtCall { get; } = [];
        public Task<VideoExportResult> ExportAsync(VideoExportRequest request, IProgress<VideoExportProgress>? progress, CancellationToken ct)
        {
            Requests.Add(request);
            SrtExistedAtCall.Add(File.Exists(Path.ChangeExtension(request.OutputPath, ".srt")));
            if (Mode == ExportMode.Fail) throw new InvalidOperationException("偽の失敗");
            if (Mode == ExportMode.FFmpegFail) throw new FFmpegException("偽の FFmpeg の失敗", "詳細");
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            File.WriteAllBytes(request.OutputPath, new byte[1024]);
            return Task.FromResult(new VideoExportResult(request.OutputPath, 19, TimeSpan.FromSeconds(1)));
        }
    }

    internal sealed class Shell : IShellService
    {
        public List<string> Revealed { get; } = [];
        public List<string> Opened { get; } = [];
        public void OpenFolder(string folder) => Opened.Add(folder);
        public void RevealFile(string file) => Revealed.Add(file);
        public void OpenUrl(string url) { }
    }

    private sealed class Info(HashSet<string> filters) : IFFmpegInfoService
    {
        public Task<FFmpegCapabilities> DetectAsync(FFmpegLocation location, CancellationToken ct = default)
            => Task.FromResult(new FFmpegCapabilities { FfmpegVersion = "9.0", MajorVersion = 9, Encoders = new HashSet<string> { "libx264", "aac", "png", "mjpeg" }, Filters = filters });
    }

    internal sealed record Rig(MainViewModel Vm, Dialogs Dialogs, MainViewModelPhaseTests.FakeWindows Windows, Export Export, Shell Shell,
        ListLogger<MainViewModel> Log, ProjectDocument Project, string Folder);

    private static readonly string[] AllFilters = ["subtitles", "zoompan", "palettegen", "paletteuse"];

    /// <summary>構成 Y: 字幕 A・B、カット 15〜16、タイトルなし、保存済み、exportSettings の出力先・名前は空、FFmpeg あり。</summary>
    private ProjectDocument ProjectY(bool subtitles = true, double fps = 30)
    {
        var p = TestProjects.Create(20);
        p.VideoMetadata = p.VideoMetadata! with { Fps = fps, Width = 1280, Height = 720 };
        p.SourceVideo.Path = Path.Combine(_dir, "rec.mp4");
        p.CutSegments.Add(new CutSegment { Id = "cut1", Start = 15, End = 16 });
        p.EditPhase = EditPhase.Subtitle;
        if (subtitles)
        {
            p.SubtitleSegments.Add(new SubtitleSegment { Start = 1, End = 2.5, Text = "設定を開きます" });
            p.SubtitleSegments.Add(new SubtitleSegment { Start = 3, End = 5, Text = "保存を押します" });
        }
        return p;
    }

    /// <param name="videoExport">指定すると偽の動画書き出しの代わりに ViewModel へ注入する（Rig.Export は呼ばれない）。</param>
    private async Task<Rig> OpenAsync(ProjectDocument project, string name = "y", bool ffmpeg = true, IEnumerable<string>? filters = null, bool expectClean = true,
        IVideoExportService? videoExport = null)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        if (!File.Exists(project.SourceVideo.Path)) File.WriteAllBytes(project.SourceVideo.Path, [1, 2, 3]);
        var settings = new MainViewModelPhaseTests.FakeSettings();
        if (ffmpeg)
        {
            var exe = Path.Combine(_dir, "ffmpeg.exe");
            var probe = Path.Combine(_dir, "ffprobe.exe");
            if (!File.Exists(exe)) File.WriteAllBytes(exe, [0]);
            if (!File.Exists(probe)) File.WriteAllBytes(probe, [0]);
            settings.Current.FfmpegPath = exe;
            settings.Current.FfprobePath = probe;
        }
        else
        {
            settings.Current.FfmpegPath = Path.Combine(_dir, "no-such", "ffmpeg.exe");
            settings.Current.FfprobePath = Path.Combine(_dir, "no-such", "ffprobe.exe");
        }
        var status = new FFmpegStatus(new FFmpegLocator(), new Info([.. filters ?? AllFilters]), settings, NullLogger<FFmpegStatus>.Instance);
        if (ffmpeg) await status.RefreshAsync();
        var unused = new MainViewModelPhaseTests.Unused();
        var dialogs = new Dialogs();
        var windows = new MainViewModelPhaseTests.FakeWindows { RunWork = true };
        var export = new Export();
        dialogs.ExportCalls = () => export.Requests.Count;
        var shell = new Shell();
        var log = new ListLogger<MainViewModel>();
        var vm = new MainViewModel(settings, new MainViewModelPhaseTests.FakeProjects(project), unused, unused, videoExport ?? export, unused,
            new ImageExportService(unused), unused, dialogs, windows, shell, status, log);
        await vm.OpenProjectFileAsync(Path.Combine(_dir, name + ".mvsproj.json"));
        Assert.True(vm.HasProject);
        if (expectClean) Assert.False(vm.IsDirty);
        return new Rig(vm, dialogs, windows, export, shell, log, project, folder);
    }

    /// <summary>書き出しのコマンドを STA で実行する（図形・タイトルの画像を WPF で描くため）。</summary>
    private static Task RunAsync(IAsyncRelayCommand command) => StaThread.RunAsync(() => command.ExecuteAsync(null).GetAwaiter().GetResult());

    private static string[] Files(string folder) => Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    /// <summary>同じ状態で［字幕ファイルを書き出し（SRT）…］が書くバイト列。</summary>
    private static byte[] SrtCommandBytes(Rig r, string path)
    {
        r.Dialogs.SaveFileResult = path;
        r.Vm.ExportSrtCommand.Execute(null);
        return File.ReadAllBytes(path);
    }

    // ---------------- T-47 AC-19 ----------------

    [Theory] // T-47
    [InlineData(UiModes.Full)]
    [InlineData(UiModes.Simple)]
    public async Task EnabledExactlyWhenTheSrtExportIsEnabled(string mode)
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync(mode);
        void Same() => Assert.Equal(vm.ExportSrtCommand.CanExecute(null), vm.ExportYouTubeCommand.CanExecute(null));
        Assert.False(vm.ExportYouTubeCommand.CanExecute(null));
        Same();
        var changed = 0;
        vm.ExportYouTubeCommand.CanExecuteChanged += (_, _) => changed++;
        vm.ConfirmCutsCommand.Execute(null);
        Assert.False(vm.ExportYouTubeCommand.CanExecute(null));
        Assert.True(vm.ExportVideoCommand.CanExecute(null));

        vm.SeekTo(1);
        vm.SetMarkInCommand.Execute(null);
        vm.SeekTo(3);
        vm.SetMarkOutCommand.Execute(null);
        changed = 0;
        vm.AddSubtitleCommand.Execute(null);
        Assert.True(vm.ExportYouTubeCommand.CanExecute(null));
        Same();
        Assert.True(changed > 0);

        changed = 0;
        vm.Editor!.RemoveSubtitle(vm.Project!.SubtitleSegments[0].Id);
        Assert.False(vm.ExportYouTubeCommand.CanExecute(null));
        Same();
        Assert.True(changed > 0);

        changed = 0;
        vm.UndoCommand.Execute(null);
        Assert.True(vm.ExportYouTubeCommand.CanExecute(null));
        Same();
        Assert.True(changed > 0);
    }

    [Fact] // T-47 (a)(g)
    public async Task DisabledWithoutProjectOrWithoutSubtitles()
    {
        var dialogs = new MainViewModelPhaseTests.FakeDialogs();
        var settings = new MainViewModelPhaseTests.FakeSettings();
        var unused = new MainViewModelPhaseTests.Unused();
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), unused, settings, NullLogger<FFmpegStatus>.Instance);
        var none = new MainViewModel(settings, new MainViewModelPhaseTests.FakeProjects(TestProjects.Create()), unused, unused, unused, unused,
            new ImageExportService(unused), unused, dialogs, new MainViewModelPhaseTests.FakeWindows(), unused, ffmpeg, NullLogger<MainViewModel>.Instance);
        Assert.False(none.ExportYouTubeCommand.CanExecute(null));

        var p = ProjectY(subtitles: false);
        p.Title.Text = "操作";
        p.ShapeGroups.Add(new ShapeGroup { Start = 2, End = 4, ImageWidth = 1280, ImageHeight = 720, Shapes = [new RectangleAnnotation { X = 10, Y = 10, Width = 100, Height = 50 }] });
        var r = await OpenAsync(p);
        Assert.False(r.Vm.ExportYouTubeCommand.CanExecute(null));
        Assert.True(r.Vm.ExportVideoCommand.CanExecute(null));
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.Empty(r.Dialogs.SaveFiles);
        Assert.Empty(r.Export.Requests);
    }

    // ---------------- T-48・T-49 AC-20・AC-22 ----------------

    [Fact] // T-48
    public async Task OneSaveDialogLikeTheMp4ExportThenSrtNextToTheVideo()
    {
        var r = await OpenAsync(ProjectY());
        r.Dialogs.SaveFileResult = null;
        await RunAsync(r.Vm.ExportVideoCommand);                  // 通常の書き出しの保存ダイアログの引数（キャンセル）
        var normal = Assert.Single(r.Dialogs.SaveFiles);
        r.Dialogs.SaveFiles.Clear();

        var output = Path.Combine(r.Folder, "out.mp4");
        r.Dialogs.SaveFileResult = output;
        await RunAsync(r.Vm.ExportYouTubeCommand);

        var save = Assert.Single(r.Dialogs.SaveFiles);
        Assert.Equal("MP4 動画 (*.mp4)|*.mp4|Matroska 動画 (*.mkv)|*.mkv", save.Filter);
        Assert.Equal((normal.Filter, normal.Name, normal.Folder), (save.Filter, save.Name, save.Folder));
        Assert.Equal("rec_edited.mp4", save.Name);
        Assert.Equal("YouTube 用に書き出し（字幕なし MP4＋SRT）", save.Title);
        Assert.Equal("YouTube 用の書き出し", r.Windows.ProgressTitles[^1]);
        var req = Assert.Single(r.Export.Requests);
        Assert.Equal(output, req.OutputPath);
        Assert.False(Assert.Single(r.Export.SrtExistedAtCall));   // MP4 の後に SRT を書く
        var srt = Path.Combine(r.Folder, "out.srt");
        Assert.Equal(["out.mp4", "out.srt"], Files(r.Folder));
        var bytes = File.ReadAllBytes(srt);
        Assert.Equal("﻿1\r\n00:00:01,000 --> 00:00:02,500\r\n設定を開きます\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\n保存を押します\r\n\r\n",
            Encoding.UTF8.GetString(bytes));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Directory.CreateDirectory(Path.Combine(_dir, "cmp"));
        Assert.Equal(SrtCommandBytes(r, Path.Combine(_dir, "cmp", "out.srt")), bytes);
        Assert.Empty(r.Dialogs.Errors);
    }

    [Theory] // T-49 (a)〜(d)
    [InlineData("out.mkv", "out.srt")]
    [InlineData("OUT.MP4", "OUT.srt")]
    [InlineData("a.b.mp4", "a.b.srt")]
    [InlineData("出力 先\\動画 #1.mp4", "出力 先\\動画 #1.srt")]
    [InlineData("noext", "noext.srt")]
    public async Task SrtPathIsTheVideoPathWithSrtExtension(string video, string srt)
    {
        var r = await OpenAsync(ProjectY());
        Directory.CreateDirectory(Path.Combine(r.Folder, "出力 先"));
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, video);
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.Single(r.Dialogs.SaveFiles);
        Assert.Equal(Path.Combine(r.Folder, video), Assert.Single(r.Export.Requests).OutputPath);
        var expected = "﻿1\r\n00:00:01,000 --> 00:00:02,500\r\n設定を開きます\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\n保存を押します\r\n\r\n";
        Assert.Equal(expected, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(r.Folder, srt))));
        Assert.Empty(r.Dialogs.Errors);
    }

    [Theory] // T-49 (e)(f)
    [InlineData("x.srt")]
    [InlineData("x.SRT")]
    public async Task VideoNamedLikeTheSrtIsRejected(string name)
    {
        var r = await OpenAsync(ProjectY());
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, name);
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.Equal("動画と字幕ファイルが同じ名前になります。拡張子 .mp4 で指定してください。", Assert.Single(r.Dialogs.Errors).Message);
        Assert.Empty(r.Export.Requests);
        Assert.Empty(r.Dialogs.Confirms);
        Assert.Empty(Files(r.Folder));
    }

    // ---------------- T-50・T-51 AC-21・AC-30 ----------------

    /// <summary>T-50 の構成: 構成 Y にタイトル 3 秒・フェード ON、図形（四角 2〜4 秒）、拡大（3〜4 秒）、CRF 18・slow・128 kbps。</summary>
    private ProjectDocument ProjectRich(bool subtitles)
    {
        var p = ProjectY(subtitles);
        p.Title.Text = "操作マニュアル";
        p.Title.Seconds = 3;
        p.Title.FadeIn = true;
        p.ShapeGroups.Add(new ShapeGroup { Id = "s", Start = 2, End = 4, ImageWidth = 1280, ImageHeight = 720, Shapes = [new RectangleAnnotation { X = 100, Y = 100, Width = 200, Height = 100 }] });
        p.ShapeGroups.Add(new ShapeGroup { Id = "z", Start = 3, End = 4, ImageWidth = 1280, ImageHeight = 720, Shapes = [new ZoomAnnotation { X = 320, Y = 180, Width = 640, Height = 360 }] });
        p.ExportSettings.VideoCrf = 18;
        p.ExportSettings.VideoPreset = "slow";
        p.ExportSettings.AudioBitrateKbps = 128;
        return p;
    }

    /// <summary>要求の OutputPath・字幕以外の項目を比べられる形にする（画像はバイト列のハッシュ）。</summary>
    private static string Describe(VideoExportRequest r)
    {
        static string H(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
        var shapes = r.Shapes is null ? "null" : JsonSerializer.Serialize(new
        {
            Images = r.Shapes.Images.Select(i => JsonSerializer.Serialize(i with { Png = [] }) + H(i.Png)),
            r.Shapes.Mosaics, r.Shapes.Zooms, Clicks = r.Shapes.Clicks?.Count,
        });
        return string.Join("\n",
            r.FfmpegPath, r.FfmpegMajorVersion, r.InputPath, JsonSerializer.Serialize(r.Metadata),
            JsonSerializer.Serialize(r.CutSegments), JsonSerializer.Serialize(r.Options), JsonSerializer.Serialize(r.SubtitleStyle),
            r.TransitionSeconds, r.TextMeasurer?.GetType().FullName, shapes,
            r.Title is null ? "null" : $"{r.Title.Seconds}/{r.Title.FadeSeconds}/{H(r.Title.Png)}", r.ZoomPanAvailable);
    }

    [Fact] // T-50・T-51 (a)(b)
    public async Task RequestAndFfmpegArgumentsEqualTheNormalExportWithoutSubtitles()
    {
        foreach (var rich in new[] { false, true })
        {
            var tag = rich ? "rich" : "plain";
            ProjectDocument Make(bool subs) => rich ? ProjectRich(subs) : ProjectY(subs);
            var yt = await OpenAsync(Make(true), $"{tag}-yt");
            var n = await OpenAsync(Make(true), $"{tag}-n");
            var p0 = await OpenAsync(Make(false), $"{tag}-0");
            foreach (var r in new[] { yt, n, p0 })
            {
                r.Vm.Settings.Subtitle.FontSize = 77; // 字幕の設定を既定以外に
                r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
            }
            await RunAsync(yt.Vm.ExportYouTubeCommand);
            await RunAsync(n.Vm.ExportVideoCommand);
            await RunAsync(p0.Vm.ExportVideoCommand);
            var (ryt, rn, r0) = (Assert.Single(yt.Export.Requests), Assert.Single(n.Export.Requests), Assert.Single(p0.Export.Requests));

            Assert.NotNull(ryt.Subtitles);
            Assert.Empty(ryt.Subtitles!);
            Assert.Empty(r0.Subtitles!);
            Assert.Equal(2, rn.Subtitles!.Count);
            Assert.Equal(Describe(r0), Describe(ryt));
            Assert.Equal(Describe(rn), Describe(ryt));
            if (rich)
            {
                Assert.NotNull(ryt.Title);
                Assert.NotNull(ryt.Shapes);
                Assert.NotEmpty(ryt.Shapes!.Zooms);
                Assert.Equal(new VideoEncodeOptions { Crf = 18, Preset = "slow", AudioBitrateKbps = 128 }, ryt.Options);
            }
            Assert.Equal(2, yt.Project.SubtitleSegments.Count);

            // 実際の VideoExportService が組み立てる ffmpeg の引数を比べる（T-51）。
            var aYt = await ArgumentsAsync(ryt, Path.Combine(_dir, $"{tag}-args-yt"));
            var a0 = await ArgumentsAsync(r0, Path.Combine(_dir, $"{tag}-args-0"));
            var aN = await ArgumentsAsync(rn, Path.Combine(_dir, $"{tag}-args-n"));
            Assert.Equal(a0, aYt);
            Assert.DoesNotContain("subtitles=", aYt);
            Assert.DoesNotContain(".ass", aYt);
            Assert.Contains("subtitles", aN);
            Assert.NotEqual(aN, aYt);
        }
    }

    [Fact] // T-51 (c): カット・タイトル・図形なし（P0 の通常の書き出しは「何もありません」の確認に OK）
    public async Task ArgumentsEqualWithoutCutsTitleOrShapes()
    {
        ProjectDocument Make(bool subs) { var p = ProjectY(subs); p.CutSegments.Clear(); return p; }
        var yt = await OpenAsync(Make(true), "c-yt");
        var p0 = await OpenAsync(Make(false), "c-0");
        yt.Dialogs.SaveFileResult = Path.Combine(yt.Folder, "out.mp4");
        p0.Dialogs.SaveFileResult = Path.Combine(p0.Folder, "out.mp4");
        p0.Dialogs.ConfirmAnswer = (_, _) => true;
        await RunAsync(yt.Vm.ExportYouTubeCommand);
        await RunAsync(p0.Vm.ExportVideoCommand);
        Assert.Contains(p0.Dialogs.Confirms, c => c.Message.StartsWith("確定したカット区間・字幕・図形がありません", StringComparison.Ordinal));
        Assert.DoesNotContain(yt.Dialogs.Confirms, c => c.Message.StartsWith("確定したカット区間・字幕・図形がありません", StringComparison.Ordinal));
        var ryt = Assert.Single(yt.Export.Requests);
        var r0 = Assert.Single(p0.Export.Requests);
        Assert.Equal(Describe(r0), Describe(ryt));
        Assert.Equal(await ArgumentsAsync(r0, Path.Combine(_dir, "c-args-0")), await ArgumentsAsync(ryt, Path.Combine(_dir, "c-args-yt")));
    }

    /// <summary>
    /// 要求を実際の VideoExportService に通し、記録用の偽のプロセス実行で ffmpeg の引数・作業フォルダーのファイル・フィルタの一時ファイルを記録する。
    /// 作業フォルダー・出力フォルダー・フィルタの一時ファイルのパスは置き換える。
    /// </summary>
    private static async Task<string> ArgumentsAsync(VideoExportRequest request, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var runner = new ArgRunner(outDir);
        var service = new VideoExportService(runner);
        await StaThread.RunAsync(() => service.ExportAsync(request with { OutputPath = Path.Combine(outDir, "out.mp4") }, null, CancellationToken.None).GetAwaiter().GetResult());
        Assert.True(File.Exists(Path.Combine(outDir, "out.mp4")));
        Assert.Empty(Directory.GetFiles(outDir, "*.partial.*"));
        return runner.Record.ToString();
    }

    private sealed class ArgRunner(string outDir) : IProcessRunner
    {
        public StringBuilder Record { get; } = new();
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            var ws = request.WorkingDirectory ?? "<no working directory>";
            Record.AppendLine("exe=" + Path.GetFileName(request.FileName) + " ws=" + (request.WorkingDirectory is null ? "none" : "<WS>"));
            foreach (var a in request.Arguments)
            {
                if (Path.GetFileName(a).StartsWith("manualvideostudio-filter-", StringComparison.Ordinal) && File.Exists(a))
                {
                    Record.AppendLine("<FILTER>");
                    Record.AppendLine("filter=" + File.ReadAllText(a).Replace(ws, "<WS>", StringComparison.OrdinalIgnoreCase));
                    continue;
                }
                Record.AppendLine(a.Replace(ws, "<WS>", StringComparison.OrdinalIgnoreCase).Replace(outDir, "<OUT>", StringComparison.OrdinalIgnoreCase));
            }
            foreach (var f in request.WorkingDirectory is null ? Enumerable.Empty<string>() : Directory.GetFiles(ws).Order(StringComparer.Ordinal))
                Record.AppendLine($"ws:{Path.GetFileName(f)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}");
            File.WriteAllBytes(request.Arguments[^1], new byte[1024]);
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.FromSeconds(1)));
        }
    }

    // ---------------- T-52 AC-22 ----------------

    [Theory] // T-52
    [InlineData("", 3, true, 30.0, "00:00:01,000 --> 00:00:02,500", "00:00:03,000 --> 00:00:05,000")]
    [InlineData("操作", 3, true, 30.0, "00:00:04,000 --> 00:00:05,500", "00:00:06,000 --> 00:00:08,000")]
    [InlineData("操作", 3, true, 30000.0 / 1001, "00:00:04,003 --> 00:00:05,503", "00:00:06,003 --> 00:00:08,003")]
    [InlineData("操作", 3, false, 30000.0 / 1001, "00:00:04,003 --> 00:00:05,503", "00:00:06,003 --> 00:00:08,003")]
    [InlineData("   ", 5, true, 30.0, "00:00:01,000 --> 00:00:02,500", "00:00:03,000 --> 00:00:05,000")]
    [InlineData("操作", 5, true, 30.0, "00:00:06,000 --> 00:00:07,500", "00:00:08,000 --> 00:00:10,000")]
    [InlineData("操作", 3, true, 0.0, "00:00:04,000 --> 00:00:05,500", "00:00:06,000 --> 00:00:08,000")]
    public async Task SrtTimesIncludeTheActualTitleLengthOfTheVideo(string text, int seconds, bool fade, double fps, string a, string b)
    {
        var p = ProjectY(fps: fps);
        p.Title.Text = text;
        p.Title.Seconds = seconds;
        p.Title.FadeIn = fade;
        var r = await OpenAsync(p);
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        var bytes = File.ReadAllBytes(Path.Combine(r.Folder, "out.srt"));
        var lines = Encoding.UTF8.GetString(bytes).Split("\r\n");
        Assert.Equal(a, lines[1]);
        Assert.Equal(b, lines[5]);

        // 足した秒数は、MP4 に渡したタイトルの実際のコマ数 ÷ fps（fps が 0 以下なら 30）
        var req = Assert.Single(r.Export.Requests);
        var effectiveFps = fps > 0 ? fps : 30;
        var expectedOffset = req.Title is null ? 0
            : FFmpegArguments.TitleFrameCount(new TitleFilterPlan("", req.Title.Seconds, 0, effectiveFps)) / effectiveFps;
        Assert.Equal(expectedOffset, SrtExport.TitleOffsetSeconds(p.Title, fps), 6);
        Assert.Equal(1000 + (long)Math.Round(expectedOffset * 1000, MidpointRounding.AwayFromZero), MsOf(lines[1][..12]));

        Directory.CreateDirectory(Path.Combine(_dir, "cmp"));
        Assert.Equal(SrtCommandBytes(r, Path.Combine(_dir, "cmp", "out.srt")), bytes);
        Assert.Equal((1.0, 2.5), (p.SubtitleSegments[0].Start, p.SubtitleSegments[0].End));

        static long MsOf(string t)
        {
            var x = t.Split(':', ',');
            return long.Parse(x[0]) * 3_600_000 + long.Parse(x[1]) * 60_000 + long.Parse(x[2]) * 1000 + long.Parse(x[3]);
        }
    }

    // ---------------- T-53 AC-23 ----------------

    [Fact] // T-53 (a)
    public async Task ExistingSrtIsConfirmedBeforeTheVideoAndCancelWritesNothing()
    {
        var r = await OpenAsync(ProjectY());
        var srt = Path.Combine(r.Folder, "out.srt");
        File.WriteAllText(srt, "OLD");
        var time = File.GetLastWriteTimeUtc(srt);
        r.Dialogs.ConfirmAnswer = (_, _) => false;
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        var c = Assert.Single(r.Dialogs.Confirms);
        Assert.Equal($"同じ名前の字幕ファイルがあります。上書きしますか？{Environment.NewLine}{srt}", c.Message);
        Assert.Equal(0, Assert.Single(r.Dialogs.ExportCallsAtConfirm));
        Assert.Empty(r.Export.Requests);
        Assert.Equal(["out.srt"], Files(r.Folder));
        Assert.Equal("OLD", File.ReadAllText(srt));
        Assert.Equal(time, File.GetLastWriteTimeUtc(srt));
        Assert.Empty(r.Dialogs.Errors);
        Assert.DoesNotContain("YouTube 用に書き出しました", r.Vm.StatusMessage ?? "");
    }

    [Fact] // T-53 (b)
    public async Task ExistingSrtIsOverwrittenAfterConfirmation()
    {
        var r = await OpenAsync(ProjectY());
        var srt = Path.Combine(r.Folder, "out.srt");
        File.WriteAllText(srt, "OLD");
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.StartsWith("同じ名前の字幕ファイルがあります。", r.Dialogs.Confirms.First().Message);
        Assert.Equal(0, r.Dialogs.ExportCallsAtConfirm[0]);
        Assert.Single(r.Export.Requests);
        Assert.StartsWith("1\r\n00:00:01,000 --> 00:00:02,500\r\n設定を開きます", File.ReadAllText(srt));
    }

    [Theory] // T-53 (c)(d)
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoSrtConfirmationWithoutAnExistingSrt(bool mp4Exists)
    {
        var r = await OpenAsync(ProjectY());
        if (mp4Exists) File.WriteAllBytes(Path.Combine(r.Folder, "out.mp4"), [9]);
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.DoesNotContain(r.Dialogs.Confirms, c => c.Message.StartsWith("同じ名前の字幕ファイル", StringComparison.Ordinal));
        Assert.Single(r.Export.Requests);
        Assert.True(File.Exists(Path.Combine(r.Folder, "out.srt")));
    }

    // ---------------- T-54 AC-24 ----------------

    [Theory] // T-54
    [InlineData("cancel", false)]
    [InlineData("fail", false)]
    [InlineData("ffmpeg", false)]
    [InlineData("cancel", true)]
    [InlineData("fail", true)]
    public async Task CancelOrFailureWritesNoSrt(string kind, bool existing)
    {
        var r = await OpenAsync(ProjectY());
        var srt = Path.Combine(r.Folder, "out.srt");
        DateTime time = default;
        if (existing)
        {
            File.WriteAllText(srt, "OLD");
            time = File.GetLastWriteTimeUtc(srt);
        }
        r.Windows.RunWork = kind != "cancel";
        r.Export.Mode = kind == "fail" ? ExportMode.Fail : ExportMode.FFmpegFail;
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
        var es = r.Project.ExportSettings;
        await RunAsync(r.Vm.ExportYouTubeCommand);

        if (existing)
        {
            Assert.Equal("OLD", File.ReadAllText(srt));
            Assert.Equal(time, File.GetLastWriteTimeUtc(srt));
        }
        else Assert.False(File.Exists(srt));
        Assert.Empty(r.Shell.Revealed);
        Assert.DoesNotContain(r.Dialogs.Confirms, c => c.Message.StartsWith("書き出しが完了", StringComparison.Ordinal));
        Assert.False(r.Vm.IsDirty);
        Assert.Equal(("", ""), (es.VideoOutputDirectory, es.VideoOutputFileName));
        switch (kind)
        {
            case "cancel":
                Assert.Equal("動画の書き出しをキャンセルしました（途中のファイルは削除しました）。字幕ファイルは書き出していません。", r.Vm.StatusMessage);
                Assert.Empty(r.Dialogs.Errors);
                break;
            case "fail":
                var e = Assert.Single(r.Dialogs.Errors);
                Assert.Equal(("動画の書き出しに失敗しました。", "偽の失敗"), (e.Message, e.Detail));
                Assert.Single(r.Log.Entries, x => x.Message == "YouTube export failed" && x.Exception?.Message == "偽の失敗");
                break;
            case "ffmpeg":
                var f = Assert.Single(r.Dialogs.Errors);
                // 通常の書き出しと同じ表示
                var n = await OpenAsync(ProjectY(), "n");
                n.Export.Mode = ExportMode.FFmpegFail;
                n.Dialogs.SaveFileResult = Path.Combine(n.Folder, "out.mp4");
                await RunAsync(n.Vm.ExportVideoCommand);
                Assert.Equal(Assert.Single(n.Dialogs.Errors), f);
                break;
        }
    }

    // ---------------- T-55 AC-25 ----------------

    [Theory] // T-55
    [InlineData("folder")]
    [InlineData("locked")]
    [InlineData("readonly")]
    public async Task SrtFailureKeepsTheVideoAndReportsBothPaths(string kind)
    {
        var r = await OpenAsync(ProjectY());
        var video = Path.Combine(r.Folder, "out.mp4");
        var srt = Path.Combine(r.Folder, "out.srt");
        FileStream? lockHandle = null;
        switch (kind)
        {
            case "folder": Directory.CreateDirectory(srt); break;
            case "locked": File.WriteAllText(srt, "OLD"); lockHandle = new FileStream(srt, FileMode.Open, FileAccess.ReadWrite, FileShare.None); break;
            case "readonly": File.WriteAllText(srt, "OLD"); File.SetAttributes(srt, FileAttributes.ReadOnly); break;
        }
        r.Dialogs.SaveFileResult = video;
        try
        {
            await RunAsync(r.Vm.ExportYouTubeCommand);
        }
        finally
        {
            lockHandle?.Dispose();
        }
        Assert.True(File.Exists(video));
        var e = Assert.Single(r.Dialogs.Errors);
        Assert.Equal($"動画は書き出しましたが、字幕ファイルを書き出せませんでした。{Environment.NewLine}{srt}{Environment.NewLine}動画: {video}", e.Message);
        Assert.False(string.IsNullOrEmpty(e.Detail));
        Assert.Single(r.Log.Entries, x => x.Message == $"YouTube export: SRT write failed: {srt}" && x.Exception is not null);
        Assert.Equal($"動画を書き出しました: {video}（字幕ファイルは書き出せませんでした）", r.Vm.StatusMessage);
        if (kind == "folder") Assert.True(Directory.Exists(srt));
        else Assert.Equal("OLD", File.ReadAllText(srt));
        Assert.Equal(["out.mp4", .. kind == "folder" ? Array.Empty<string>() : ["out.srt"]], Files(r.Folder));
        Assert.DoesNotContain(r.Dialogs.Confirms, c => c.Message.StartsWith("書き出しが完了", StringComparison.Ordinal)); // 決定（不明点 8）
        Assert.False(r.Vm.IsDirty);
        Assert.Equal(("", ""), (r.Project.ExportSettings.VideoOutputDirectory, r.Project.ExportSettings.VideoOutputFileName));
    }

    // ---------------- T-56 AC-26 ----------------

    [Theory] // T-56 (a)(b)
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessReportsBothPathsAndOffersToOpenTheFolderLikeTheMp4Export(bool open)
    {
        var r = await OpenAsync(ProjectY());
        var video = Path.Combine(r.Folder, "out.mp4");
        var srt = Path.Combine(r.Folder, "out.srt");
        r.Dialogs.ConfirmAnswer = (_, _) => open;
        r.Dialogs.SaveFileResult = video;
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.Equal($"YouTube 用に書き出しました: {video}／字幕ファイル: {srt}（字幕 2 件）", r.Vm.StatusMessage);
        var c = Assert.Single(r.Dialogs.Confirms);
        Assert.Equal("完了", c.Title);
        Assert.Equal($"書き出しが完了しました。{Environment.NewLine}{video}{Environment.NewLine}{srt}{Environment.NewLine}{Environment.NewLine}フォルダーを開きますか？", c.Message);
        if (open) Assert.Equal([video], r.Shell.Revealed);
        else Assert.Empty(r.Shell.Revealed);
        Assert.Empty(r.Shell.Opened);
        Assert.DoesNotContain(r.Dialogs.Calls, x => x.Kind == "info");

        // 通常の書き出しも完了の確認で MP4 を示す（同じ動作）
        var n = await OpenAsync(ProjectY(), "n");
        n.Dialogs.ConfirmAnswer = (_, _) => open;
        n.Dialogs.SaveFileResult = Path.Combine(n.Folder, "out.mp4");
        await RunAsync(n.Vm.ExportVideoCommand);
        Assert.Equal(open ? [Path.Combine(n.Folder, "out.mp4")] : [], n.Shell.Revealed);
    }

    [Fact] // T-56 (c)
    public async Task CountIsTheNumberOfWrittenBlocks()
    {
        var p = ProjectY();
        p.SubtitleSegments.Add(new SubtitleSegment { Start = 6, End = 7, Text = "\n  \n" });
        var r = await OpenAsync(p);
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.EndsWith("（字幕 2 件）", r.Vm.StatusMessage);
        Assert.Equal(2, File.ReadAllText(Path.Combine(r.Folder, "out.srt")).Split(" --> ").Length - 1);
    }

    // ---------------- T-57 AC-27 ----------------

    [Fact] // T-57 (a)
    public async Task WithoutFfmpegItBehavesLikeTheMp4Export()
    {
        var yt = await OpenAsync(ProjectY(), "yt", ffmpeg: false);
        var n = await OpenAsync(ProjectY(), "n", ffmpeg: false);
        yt.Dialogs.ConfirmAnswer = n.Dialogs.ConfirmAnswer = (_, _) => false;
        await RunAsync(yt.Vm.ExportYouTubeCommand);
        await RunAsync(n.Vm.ExportVideoCommand);
        Assert.Equal(n.Dialogs.Calls, yt.Dialogs.Calls);
        Assert.NotEmpty(yt.Dialogs.Calls);
        Assert.Empty(yt.Dialogs.SaveFiles);
        Assert.Empty(yt.Export.Requests);
        Assert.Empty(Files(yt.Folder));
    }

    [Theory] // T-57 (b)(c)
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingCandidatesAreConfirmedLikeTheMp4Export(bool proceed)
    {
        var p = ProjectY();
        p.CutCandidates.Add(new CutCandidate { Start = 5, End = 6, Status = CandidateStatus.Pending });
        p.CutCandidates.Add(new CutCandidate { Start = 7, End = 8, Status = CandidateStatus.Pending });
        var r = await OpenAsync(p, expectClean: false);
        r.Dialogs.ConfirmAnswer = (msg, _) => msg.StartsWith("未判断", StringComparison.Ordinal) ? proceed : !msg.StartsWith("書き出しが完了", StringComparison.Ordinal);
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.Equal("未判断のカット候補が 2 件あります。候補は書き出しに反映されません（確定区間のみ反映）。続行しますか？", r.Dialogs.Confirms.First().Message);
        Assert.Equal(proceed ? 1 : 0, r.Dialogs.SaveFiles.Count);
        Assert.Equal(proceed ? 1 : 0, r.Export.Requests.Count);
        Assert.DoesNotContain(r.Dialogs.Confirms, c => c.Message.StartsWith("確定したカット区間", StringComparison.Ordinal));
    }

    [Fact] // T-57 (d)
    public async Task SourceVideoPathIsRejected()
    {
        var r = await OpenAsync(ProjectY());
        var before = File.ReadAllBytes(r.Vm.VideoPath);
        r.Dialogs.SaveFileResult = r.Vm.VideoPath;
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.Equal("元動画と同じファイルには書き出せません。別の名前を指定してください。", Assert.Single(r.Dialogs.Errors).Message);
        Assert.Empty(r.Export.Requests);
        Assert.False(File.Exists(Path.ChangeExtension(r.Vm.VideoPath, ".srt")));
        Assert.Equal(before, File.ReadAllBytes(r.Vm.VideoPath));
    }

    [Fact] // T-57 (e)
    public async Task NoLibassCheckBecauseNothingIsBurnedIn()
    {
        var yt = await OpenAsync(ProjectY(), "yt", filters: ["zoompan"]);
        var n = await OpenAsync(ProjectY(), "n", filters: ["zoompan"]);
        yt.Dialogs.SaveFileResult = Path.Combine(yt.Folder, "out.mp4");
        n.Dialogs.SaveFileResult = Path.Combine(n.Folder, "out.mp4");
        await RunAsync(yt.Vm.ExportYouTubeCommand);
        await RunAsync(n.Vm.ExportVideoCommand);
        Assert.Empty(yt.Dialogs.Errors);
        Assert.Single(yt.Export.Requests);
        Assert.True(File.Exists(Path.Combine(yt.Folder, "out.srt")));
        Assert.StartsWith("この ffmpeg には字幕の焼き込み", Assert.Single(n.Dialogs.Errors).Message); // 通常の書き出しは止まる
        Assert.Empty(n.Export.Requests);
    }

    [Fact] // T-57 (g)（2回目レビュー R1: 実 VideoExportService と記録用 runner を ViewModel に注入し、コマンド経由で確認する）
    public async Task MissingZoompanStopsBeforeFfmpegAndWritesNoSrt()
    {
        var outDir = Path.Combine(_dir, "g-out");
        Directory.CreateDirectory(outDir);
        var runner = new ArgRunner(outDir);
        var r = await OpenAsync(ProjectRich(subtitles: true), "g", filters: ["subtitles"], videoExport: new VideoExportService(runner));
        var mp4 = Path.Combine(outDir, "out.mp4");
        r.Dialogs.SaveFileResult = mp4;
        await RunAsync(r.Vm.ExportYouTubeCommand);

        var (message, detail) = Assert.Single(r.Dialogs.Errors);
        Assert.Equal("動画の書き出しに失敗しました。", message);
        Assert.Contains(ZoomSupport.MissingZoomPanMessage, detail);
        Assert.Equal(0, runner.Record.Length);                       // ffmpeg は呼ばれない
        Assert.Empty(r.Export.Requests);                             // 偽の書き出しは使われていない
        Assert.Empty(Directory.GetFiles(outDir));                    // MP4・SRT・途中のファイルなし
        Assert.DoesNotContain(r.Dialogs.Confirms, c => c.Message.StartsWith("書き出しが完了", StringComparison.Ordinal));
        Assert.DoesNotContain("YouTube 用に書き出しました", r.Vm.StatusMessage ?? "");
        Assert.False(r.Vm.IsDirty);
    }

    [Theory] // T-57 (g) の回帰確認: zoompan あり＋拡大あり、zoompan なし＋拡大なしは同じ接続で書き出せる
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ZoompanCheckDoesNotBlockValidCombinations(bool zoompan, bool zoom)
    {
        var tag = $"g-{zoompan}-{zoom}";
        var outDir = Path.Combine(_dir, tag + "-out");
        Directory.CreateDirectory(outDir);
        var runner = new ArgRunner(outDir);
        var p = ProjectRich(subtitles: true);
        if (!zoom) p.ShapeGroups.RemoveAll(g => g.Id == "z");
        var r = await OpenAsync(p, tag, filters: zoompan ? ["subtitles", "zoompan"] : ["subtitles"], videoExport: new VideoExportService(runner));
        var mp4 = Path.Combine(outDir, "out.mp4");
        r.Dialogs.SaveFileResult = mp4;
        await RunAsync(r.Vm.ExportYouTubeCommand);

        Assert.Empty(r.Dialogs.Errors);
        Assert.NotEqual(0, runner.Record.Length);
        Assert.Equal(zoom, runner.Record.ToString().Contains("zoompan", StringComparison.Ordinal));
        Assert.True(File.Exists(mp4));
        Assert.True(File.Exists(Path.Combine(outDir, "out.srt")));
        Assert.Contains(r.Dialogs.Confirms, c => c.Message.StartsWith("書き出しが完了", StringComparison.Ordinal));
    }

    // ---------------- T-58 AC-28 ----------------

    [Fact] // T-58 (a)(b)(c)
    public async Task TheProjectIsNotChangedAndTheNormalExportStillRecordsItsDestination()
    {
        var r = await OpenAsync(ProjectY());
        var es = r.Project.ExportSettings;
        r.Dialogs.SaveFileResult = Path.Combine(_dir, "yt", "y.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.False(r.Vm.IsDirty);
        Assert.Equal(("", ""), (es.VideoOutputDirectory, es.VideoOutputFileName));

        r.Dialogs.SaveFileResult = null;
        r.Dialogs.SaveFiles.Clear();
        await RunAsync(r.Vm.ExportYouTubeCommand);
        await RunAsync(r.Vm.ExportVideoCommand);
        Assert.All(r.Dialogs.SaveFiles, s => Assert.Equal("rec_edited.mp4", s.Name));
        Assert.Equal(r.Dialogs.SaveFiles[0].Folder, r.Dialogs.SaveFiles[1].Folder);

        var d = Path.Combine(_dir, "D");
        r.Dialogs.SaveFileResult = Path.Combine(d, "x.mp4");
        await RunAsync(r.Vm.ExportVideoCommand);
        Assert.True(r.Vm.IsDirty);                                     // 通常の書き出しは従来どおり記録する
        Assert.Equal((d, "x.mp4"), (es.VideoOutputDirectory, es.VideoOutputFileName));
        r.Vm.SaveProjectCommand.Execute(null);
        Assert.False(r.Vm.IsDirty);

        r.Dialogs.SaveFileResult = Path.Combine(_dir, "E", "y.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.False(r.Vm.IsDirty);
        Assert.Equal((d, "x.mp4"), (es.VideoOutputDirectory, es.VideoOutputFileName));
        r.Dialogs.SaveFileResult = null;
        r.Dialogs.SaveFiles.Clear();
        await RunAsync(r.Vm.ExportYouTubeCommand);
        await RunAsync(r.Vm.ExportVideoCommand);
        Assert.All(r.Dialogs.SaveFiles, s => Assert.Equal(("x.mp4", d), (s.Name, s.Folder)));
    }

    [Fact] // T-59
    public async Task UnappliedSubtitleInputIsAppliedFirstAndInvalidInputStopsLikeTheMp4Export()
    {
        var r = await OpenAsync(ProjectY());
        r.Vm.SelectedSubtitleItem = r.Vm.SubtitleItems[0];
        r.Vm.SubtitleDraftText = "未反映の本文";
        Assert.True(r.Vm.HasSubtitleDraft);
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.Equal("未反映の本文", r.Project.SubtitleSegments[0].Text);
        Assert.Contains("\r\n未反映の本文\r\n", File.ReadAllText(Path.Combine(r.Folder, "out.srt")));

        // 不正な入力（終了が開始より前）: 通常の書き出しと同じく保存ダイアログを出さずに止まる
        var yt = await OpenAsync(ProjectY(), "yt2");
        var n = await OpenAsync(ProjectY(), "n2");
        foreach (var x in new[] { yt, n })
        {
            x.Vm.SelectedSubtitleItem = x.Vm.SubtitleItems[0];
            x.Vm.SubtitleDraftEnd = "00:00:00.500";
            x.Dialogs.SaveFileResult = Path.Combine(x.Folder, "out.mp4");
        }
        await RunAsync(yt.Vm.ExportYouTubeCommand);
        await RunAsync(n.Vm.ExportVideoCommand);
        Assert.Empty(yt.Dialogs.SaveFiles);
        Assert.Empty(n.Dialogs.SaveFiles);
        Assert.Equal(n.Dialogs.Calls, yt.Dialogs.Calls);
        Assert.Equal(n.Vm.StatusMessage, yt.Vm.StatusMessage);
        Assert.False(string.IsNullOrEmpty(yt.Vm.StatusMessage));
        Assert.Empty(yt.Export.Requests);
        Assert.Empty(Files(yt.Folder));
    }

    [Fact] // T-58 (e)
    public async Task UnsavedChangesStayUnsaved()
    {
        var r = await OpenAsync(ProjectY());
        var s = r.Project.SubtitleSegments[0];
        Assert.True(r.Vm.Editor!.UpdateSubtitle(s.Id, s.Start, s.End, "変更", s.Position, s.FontSize).Success);
        Assert.True(r.Vm.IsDirty);
        r.Dialogs.SaveFileResult = Path.Combine(r.Folder, "out.mp4");
        await RunAsync(r.Vm.ExportYouTubeCommand);
        Assert.True(r.Vm.IsDirty);
        Assert.Contains("\r\n変更\r\n", File.ReadAllText(Path.Combine(r.Folder, "out.srt")));
    }
}
