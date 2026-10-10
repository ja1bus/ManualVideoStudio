using System.Text.Json;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>外部文書連携の設定（v1.14）: 既定 OFF・旧設定も OFF・保存と再読込・プロジェクトには入れない。</summary>
public sealed class ExternalDocumentSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mvs-extdoc-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private SettingsService Service(string json)
    {
        var p = PathOf("settings.json");
        File.WriteAllText(p, json);
        return new SettingsService(p);
    }

    [Fact]
    public void NewSettingsAreOffAndFirstRunDoesNotEnableIt()
    {
        Assert.False(new AppSettings().EnableExternalDocumentExport);
        var svc = new SettingsService(PathOf("none.json"));   // ファイルなし（初めての起動）
        Assert.False(svc.Load().EnableExternalDocumentExport);
    }

    [Fact]
    public void OldSettingsWithoutTheKeyAreOffEvenInFullModeWithFullModeData()
    {
        // v1.13 以前の設定（全機能モード・過去の手順書の出力先など）から、自動で ON にしない。
        var loaded = Service("{\"schemaVersion\":1,\"uiMode\":\"full\",\"ui\":{\"recentProjects\":[\"C:\\\\a.mvsproj.json\"]},\"defaultExport\":{\"writeMetadataJson\":true}}").Load();
        Assert.Equal(UiModes.Full, loaded.UiMode);
        Assert.False(loaded.EnableExternalDocumentExport);
        var oldNoMode = Service("{\"schemaVersion\":1}").Load();   // uiMode も無い旧設定（全機能モードになる）
        Assert.Equal(UiModes.Full, oldNoMode.UiMode);
        Assert.False(oldNoMode.EnableExternalDocumentExport);
    }

    [Fact]
    public void ExplicitFalseIsOff() => Assert.False(Service("{\"enableExternalDocumentExport\":false}").Load().EnableExternalDocumentExport);

    [Fact]
    public void ExplicitTrueIsOn() => Assert.True(Service("{\"enableExternalDocumentExport\":true}").Load().EnableExternalDocumentExport);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValueIsSavedExplicitlyAndReloaded(bool value)
    {
        var svc = Service("{}");
        svc.Load();
        svc.Current.EnableExternalDocumentExport = value;
        svc.Save();
        var text = File.ReadAllText(PathOf("settings.json"));
        Assert.Contains($"\"enableExternalDocumentExport\": {(value ? "true" : "false")}", text);   // 明示的な true / false を書く
        Assert.Equal(value, new SettingsService(PathOf("settings.json")).Load().EnableExternalDocumentExport);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));   // 一時ファイルが残らない（AtomicFile 経由）
    }

    [Fact]
    public void TheFlagIsNotWrittenToProjectFiles()
    {
        var project = TestProjects.Create(60);
        var json = JsonSerializer.Serialize(project, JsonDefaults.Options);
        Assert.DoesNotContain("enableExternalDocumentExport", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(8, ProjectDocument.CurrentSchemaVersion);   // v1.15 でタイトル（title）を追加
    }
}

/// <summary>主画面の利用条件（v1.14）: 表示 = 全機能モード AND 有効、実行 = 既存の条件 AND 有効。</summary>
public class ExternalDocumentAvailabilityTests
{
    [Theory]
    [InlineData(UiModes.Simple, false, false)]
    [InlineData(UiModes.Simple, true, false)]    // 簡易モードでは値を保持するが、見せない・実行させない
    [InlineData(UiModes.Full, false, false)]     // 全機能でも既定 OFF では出さない
    [InlineData(UiModes.Full, true, true)]
    public async Task DocumentExportFollowsModeAndFlag(string mode, bool flag, bool available)
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync(mode, flag);
        Assert.Equal(flag, vm.IsExternalDocumentEnabled);   // 設定値はモードにかかわらず保持される
        Assert.Equal(available, vm.ShowExternalDocument);
        Assert.Equal(available, vm.CanExportExternalDocument);
        Assert.Equal(available, vm.ExportTejunshoCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(UiModes.Full, false)]
    [InlineData(UiModes.Full, true)]
    public async Task RegularImageFeaturesAreIndependentOfTheFlag(string mode, bool flag)
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync(mode, flag);
        Assert.True(vm.ExportImagesCommand.CanExecute(null));   // 通常の画像書き出し
        Assert.True(vm.AddCaptureCommand.CanExecute(null));
        Assert.True(vm.ShowAiAssistCommand.CanExecute(null));
        Assert.True(vm.ShowImageWorkspaceCommand.CanExecute(null));   // 画像作成の作業領域は隠さない
        Assert.True(vm.ShowWorkspaceSwitch);
        Assert.True(vm.ShowPromptSettingsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangingOnlyTheFlagIsReflectedImmediately()
    {
        var (vm, _, windows) = await MainViewModelPhaseTests.OpenAsync(UiModes.Full, false);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(vm.ExportTejunshoCommand.CanExecute(null));
        var canChanged = 0;
        vm.ExportTejunshoCommand.CanExecuteChanged += (_, _) => canChanged++;

        windows.SettingsChange = () => vm.Settings.EnableExternalDocumentExport = true;   // モードは変えない
        await vm.ShowSettingsCommand.ExecuteAsync(null);
        Assert.Equal(UiModes.Full, vm.Settings.UiMode);   // ON にしても画面のモードは自動で変えない
        Assert.True(vm.ShowExternalDocument);
        Assert.True(vm.ExportTejunshoCommand.CanExecute(null));
        Assert.Contains(nameof(MainViewModel.ShowExternalDocument), changed);
        Assert.True(canChanged > 0);

        windows.SettingsChange = () => vm.Settings.EnableExternalDocumentExport = false;
        await vm.ShowSettingsCommand.ExecuteAsync(null);
        Assert.False(vm.ShowExternalDocument);
        Assert.False(vm.ExportTejunshoCommand.CanExecute(null));
        Assert.True(vm.ExportImagesCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelingSettingsChangesNothing()
    {
        var (vm, _, windows) = await MainViewModelPhaseTests.OpenAsync(UiModes.Full, false);
        windows.SettingsChange = null;   // キャンセル（Saved = false）
        await vm.ShowSettingsCommand.ExecuteAsync(null);
        Assert.False(vm.ShowExternalDocument);
        Assert.False(vm.Settings.EnableExternalDocumentExport);
    }

    [Fact]
    public async Task SwitchingToSimpleKeepsTheValueAndHidesTheEntrances()
    {
        var (vm, _, windows) = await MainViewModelPhaseTests.OpenAsync(UiModes.Full, true);
        Assert.True(vm.ShowExternalDocument);
        windows.SettingsChange = () => vm.Settings.UiMode = UiModes.Simple;
        await vm.ShowSettingsCommand.ExecuteAsync(null);
        Assert.True(vm.Settings.EnableExternalDocumentExport);   // 値は保持
        Assert.False(vm.ShowExternalDocument);
        Assert.False(vm.ExportTejunshoCommand.CanExecute(null));
        windows.SettingsChange = () => vm.Settings.UiMode = UiModes.Full;   // 全機能に戻すと使える
        await vm.ShowSettingsCommand.ExecuteAsync(null);
        Assert.True(vm.ExportTejunshoCommand.CanExecute(null));
    }

    [Fact]
    public async Task DirectInvocationWhileOffStartsNothing()
    {
        var (vm, dialogs, windows) = await MainViewModelPhaseTests.OpenAsync(UiModes.Full, false);
        Assert.True(vm.Editor!.AddCapture(10).Result.Success);
        dialogs.YesNoCancelResult = true;
        // コマンドを直接呼んでも、フォルダー作成・FFmpeg の確認・出力の確認・完了画面のどれも始まらない。
        await vm.ExportTejunshoCommand.ExecuteAsync(null);
        Assert.Equal("", dialogs.LastConfirm);
        Assert.Empty(dialogs.Errors);
        Assert.Empty(windows.TejunshoDone);
        // 簡易モード＋ON でも同じ。
        var (vm2, dialogs2, _) = await MainViewModelPhaseTests.OpenAsync(UiModes.Simple, true);
        await vm2.ExportTejunshoCommand.ExecuteAsync(null);
        Assert.Equal("", dialogs2.LastConfirm);
    }

    [Fact]
    public async Task DirectInvocationWhileOnProceedsToTheExistingFlow()
    {
        var (vm, dialogs, _) = await MainViewModelPhaseTests.OpenAsync(UiModes.Full, true);
        Assert.True(vm.Editor!.AddCapture(10).Result.Success);
        await vm.ExportTejunshoCommand.ExecuteAsync(null);
        Assert.NotEqual("", dialogs.LastConfirm);   // 既存の流れ（この環境は FFmpeg 未検出のため、その案内で止まる）
    }

    [Fact]
    public async Task OffKeepsChaptersStepsNotesAndOutputSettingsAcrossSaveAndReload()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync(UiModes.Full, false);
        var id = vm.Editor!.AddCapture(10).Result.Success ? vm.Project!.CapturePoints[0].Id : 0;
        Assert.True(vm.Editor.SetChapterTitle(id, "章 1").Success);
        Assert.True(vm.Editor.SetStepText(id, "「設定」をクリックする。").Success);
        Assert.True(vm.Editor.SetNote(id, "確認ポイント").Success);
        vm.Project!.ExportSettings.TejunshoOutputDirectory = @"C:\out\tejunsho";
        vm.Project.ExportSettings.WriteMetadataJson = true;
        vm.SaveProjectCommand.Execute(null);   // OFF のまま保存しても、隠した値を空で上書きしない
        var p = vm.Project.CapturePoints[0];
        Assert.Equal("章 1", p.ChapterTitle);
        Assert.Equal("「設定」をクリックする。", p.StepText);
        Assert.Equal("確認ポイント", p.Note);
        Assert.Equal(@"C:\out\tejunsho", vm.Project.ExportSettings.TejunshoOutputDirectory);
        Assert.True(vm.Project.ExportSettings.WriteMetadataJson);
        // JSON の往復でも維持される（保存形式は変わらない）。
        var clone = JsonDefaults.Clone(vm.Project);
        Assert.Equal("章 1", clone.CapturePoints[0].ChapterTitle);
        Assert.Equal(ProjectDocument.CurrentSchemaVersion, clone.SchemaVersion);
    }
}

/// <summary>設定画面（v1.14）: 詳細・外部連携のチェック、［OK］での保存と主画面への反映条件、プロンプトの出し分け。</summary>
public sealed class ExternalDocumentSettingsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mvs-extdoc-vm-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class ThrowingSettings : ISettingsService
    {
        public AppSettings Current { get; } = new() { UiMode = UiModes.Full };
        public AppSettings Load() => Current;
        public void Save() => throw new InvalidOperationException("設定を保存できませんでした: テスト");
    }

    private (SettingsViewModel Vm, ISettingsService Settings, PromptTemplateService Templates, MainViewModelPhaseTests.FakeDialogs Dialogs) Create(
        string mode, bool flag, ISettingsService? settings = null)
    {
        var s = settings ?? new MainViewModelPhaseTests.FakeSettings();
        s.Current.UiMode = mode;
        s.Current.EnableExternalDocumentExport = flag;
        var unused = new MainViewModelPhaseTests.Unused();
        var ffmpeg = new FFmpegStatus(new Core.FFmpeg.FFmpegLocator(), unused, s, NullLogger<FFmpegStatus>.Instance);
        var templates = new PromptTemplateService(Path.Combine(_dir, "prompt-templates.json"));
        var dialogs = new MainViewModelPhaseTests.FakeDialogs();
        var vm = new SettingsViewModel(s, ffmpeg, templates, dialogs, null!);
        return (vm, s, templates, dialogs);
    }

    private static bool Has(SettingsViewModel vm, AiPurpose p) => vm.Purposes.Any(c => c.Value == p);

    [Fact]
    public void CheckboxStartsFromTheSavedValueAndDefaultIsOff()
    {
        Assert.False(Create(UiModes.Full, false).Vm.EnableExternalDocumentExport);
        Assert.True(Create(UiModes.Simple, true).Vm.EnableExternalDocumentExport);   // 簡易モードでも開いて見られる・値を保持
    }

    [Theory]
    [InlineData(UiModes.Full, false, false)]
    [InlineData(UiModes.Full, true, true)]
    [InlineData(UiModes.Simple, true, false)]
    [InlineData(UiModes.Simple, false, false)]
    public void TejunshoRequestIsOfferedOnlyInFullModeWhenEnabled(string mode, bool flag, bool shown)
    {
        var (vm, _, _, _) = Create(mode, flag);
        Assert.Equal(shown, Has(vm, AiPurpose.TejunshoRequest));
        Assert.Equal(shown, vm.ShowExternalDocumentSettings);
        // 他の用途は常に残る。
        Assert.True(Has(vm, AiPurpose.CaptureCaption));
        Assert.True(Has(vm, AiPurpose.CutAnalysis));
        Assert.True(Has(vm, AiPurpose.CutReview));
        Assert.True(Has(vm, AiPurpose.SubtitleRequest));
    }

    [Fact]
    public void TogglingTheDraftShowsAndHidesThePurposeWithoutChangingTheMode()
    {
        var (vm, s, _, _) = Create(UiModes.Full, false);
        vm.EnableExternalDocumentExport = true;
        Assert.True(Has(vm, AiPurpose.TejunshoRequest));
        Assert.Equal(AiPurpose.SubtitleRequest, vm.Purposes[^1].Value);   // 字幕依頼の手前に入る
        Assert.False(vm.IsSimpleMode);                                      // モードは自動で変えない
        Assert.False(s.Current.EnableExternalDocumentExport);              // 下書き（OK まで保存しない）
        vm.IsSimpleMode = true;
        Assert.False(Has(vm, AiPurpose.TejunshoRequest));
        vm.IsSimpleMode = false;
        Assert.True(Has(vm, AiPurpose.TejunshoRequest));
    }

    [Fact]
    public void OkSavesTheFlagAndCancelDoesNot()
    {
        var (vm, s, _, _) = Create(UiModes.Full, false);
        vm.EnableExternalDocumentExport = true;
        vm.CancelCommand.Execute(null);
        Assert.False(s.Current.EnableExternalDocumentExport);   // キャンセルでは変わらない
        Assert.False(vm.Saved);

        var (vm2, s2, _, _) = Create(UiModes.Simple, false);
        vm2.EnableExternalDocumentExport = true;                 // 簡易モードのままでも ON を保存できる
        vm2.OkCommand.Execute(null);
        Assert.True(vm2.Saved);
        Assert.True(s2.Current.EnableExternalDocumentExport);
        Assert.Equal(UiModes.Simple, s2.Current.UiMode);         // ON にしてもモードは変わらない
    }

    [Fact]
    public void SaveFailureDoesNotLeaveTheNewFlagInMemory()
    {
        var failing = new ThrowingSettings();
        var (vm, s, _, dialogs) = Create(UiModes.Full, false, failing);
        vm.EnableExternalDocumentExport = true;
        vm.IsSimpleMode = true;
        vm.OkCommand.Execute(null);
        Assert.False(vm.Saved);                                   // 主画面は Saved で反映するため、新しい状態を適用しない
        Assert.False(s.Current.EnableExternalDocumentExport);     // メモリー上だけ ON にならない
        Assert.Equal(UiModes.Full, s.Current.UiMode);
        Assert.Single(dialogs.Errors);
    }

    [Fact]
    public void HidingDoesNotDeleteOrResetTheCustomTemplateAndShowingRestoresIt()
    {
        var (vm, _, templates, _) = Create(UiModes.Full, true);
        templates.Save(AiPurpose.TejunshoRequest, "{{OUTPUT_DIR_HOME}} から文書を作って。{{PROJECT_NAME}}");
        vm.EnableExternalDocumentExport = false;
        Assert.False(Has(vm, AiPurpose.TejunshoRequest));
        Assert.True(templates.IsCustomized(AiPurpose.TejunshoRequest));   // 削除しない・初期値に戻さない
        vm.EnableExternalDocumentExport = true;
        Assert.True(Has(vm, AiPurpose.TejunshoRequest));
        Assert.Equal("{{OUTPUT_DIR_HOME}} から文書を作って。{{PROJECT_NAME}}", templates.Get(AiPurpose.TejunshoRequest));
        vm.SelectedPurpose = AiPurpose.TejunshoRequest;
        Assert.Equal("{{OUTPUT_DIR_HOME}} から文書を作って。{{PROJECT_NAME}}", vm.TemplateText);   // 編集済みの依頼文を再び使える
    }

    [Fact]
    public void SelectedHiddenPurposeMovesAwayWhenTurnedOff()
    {
        var (vm, _, _, _) = Create(UiModes.Full, true);
        vm.SelectedPurpose = AiPurpose.TejunshoRequest;
        vm.EnableExternalDocumentExport = false;   // 未保存の編集が無いので確認なしで OFF
        Assert.NotEqual(AiPurpose.TejunshoRequest, vm.SelectedPurpose);
        Assert.False(Has(vm, vm.SelectedPurpose) == false);
    }

    [Fact]
    public void HiddenPurposeCannotBeSavedOrReset()
    {
        var (vm, _, templates, dialogs) = Create(UiModes.Full, false);
        vm.SelectedPurpose = AiPurpose.TejunshoRequest;   // 直接の操作（画面からは選べない）
        vm.TemplateText = "直接書いた依頼文";
        dialogs.ConfirmResult = true;
        vm.SaveTemplateCommand.Execute(null);
        Assert.False(templates.IsCustomized(AiPurpose.TejunshoRequest));
        templates.Save(AiPurpose.TejunshoRequest, "保存済み");
        vm.ResetTemplateCommand.Execute(null);
        Assert.Equal("保存済み", templates.Get(AiPurpose.TejunshoRequest));   // 初期値に戻さない
    }

    [Fact]
    public async Task TurningOffWithUnsavedEditsAsksSaveDiscardOrCancel()
    {
        // 保存する（はい）
        var a = Create(UiModes.Full, true);
        a.Vm.SelectedPurpose = AiPurpose.TejunshoRequest;
        a.Vm.TemplateText = "{{OUTPUT_DIR}} 編集 1";
        a.Dialogs.YesNoCancelResult = true;
        a.Vm.EnableExternalDocumentExport = false;
        Assert.False(a.Vm.EnableExternalDocumentExport);
        Assert.Equal("{{OUTPUT_DIR}} 編集 1", a.Templates.Get(AiPurpose.TejunshoRequest));
        Assert.False(Has(a.Vm, AiPurpose.TejunshoRequest));

        // 破棄する（いいえ）: 保存済みの内容は変わらない
        var b = Create(UiModes.Full, true);
        b.Templates.Save(AiPurpose.TejunshoRequest, "{{OUTPUT_DIR}} 保存済み");
        b.Vm.SelectedPurpose = AiPurpose.TejunshoRequest;
        b.Vm.TemplateText = "{{OUTPUT_DIR}} 破棄する編集";
        b.Dialogs.YesNoCancelResult = false;
        b.Vm.EnableExternalDocumentExport = false;
        Assert.False(b.Vm.EnableExternalDocumentExport);
        Assert.Equal("{{OUTPUT_DIR}} 保存済み", b.Templates.Get(AiPurpose.TejunshoRequest));

        // キャンセル: OFF にしない（編集中の内容も選択も残る）
        var c = Create(UiModes.Full, true);
        c.Vm.SelectedPurpose = AiPurpose.TejunshoRequest;
        c.Vm.TemplateText = "{{OUTPUT_DIR}} 編集中";
        c.Dialogs.YesNoCancelResult = null;
        c.Vm.EnableExternalDocumentExport = false;
        await Task.Delay(100);   // チェックの書き戻しは UI の文脈で少し遅れて行う
        Assert.True(c.Vm.EnableExternalDocumentExport);
        Assert.True(Has(c.Vm, AiPurpose.TejunshoRequest));
        Assert.Equal(AiPurpose.TejunshoRequest, c.Vm.SelectedPurpose);
        Assert.Equal("{{OUTPUT_DIR}} 編集中", c.Vm.TemplateText);
    }
}
