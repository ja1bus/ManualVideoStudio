using System.Text.Json;
using ManualVideoStudio.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Persistence;

public interface ISettingsService
{
    AppSettings Current { get; }
    AppSettings Load();
    void Save();
}

/// <summary>AppSettings の読み書き。壊れたファイルは *.bad に退避して既定値で起動する。</summary>
public sealed class SettingsService(string path, ILogger<SettingsService>? logger = null) : ISettingsService
{
    private readonly ILogger _logger = logger ?? NullLogger<SettingsService>.Instance;

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        bool firstRun = !File.Exists(path);
        Current = LoadJson<AppSettings>(path, _logger, "設定を読み込めなかったため、既定の設定で起動しました（元のファイルは末尾に .bad を付けて残しています）。") ?? new AppSettings();
        // 画面のモード（v1.11）: 初めての起動だけ簡易モード。設定ファイルがあるのに uiMode が無い・読めない（v1.10 以前から使っている）なら、
        // 使っていた機能を急に隠さないよう全機能モード。
        Current.UiMode = UiModes.Normalize(Current.UiMode, firstRun ? UiModes.Simple : UiModes.Full);
        // 前回マイク録音を ON にしていても、起動時は OFF（音声は意図した操作でのみ録る）。
        Current.Recording ??= new RecordingSettings();
        Current.Recording.NormalizeOnLoad();
        // v1.4 までの設定には字幕の項目が無い（既定値: 白文字＋半透明の黒帯（v1.9。v1.8 までは白文字＋黒背景）・メイリオ・48・下・余白 5%）。
        Current.Subtitle ??= new SubtitleStyle();
        Current.Subtitle.Normalize();
        return Current;
    }

    public void Save()
    {
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Current, JsonDefaults.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Settings save failed: {Path}", path);
            throw new InvalidOperationException($"設定を保存できませんでした: {ex.Message}", ex);
        }
    }

    /// <summary>JSON を読む。読めなければ *.bad に退避して null を返し、画面が出てから知らせる案内を残す（v1.4.2。what は案内の見出し）。</summary>
    internal static T? LoadJson<T>(string path, ILogger logger, string what) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogError(ex, "Failed to read {Path}; using defaults", path);
            FileLoadProblems.Report(FileReadProblem.Message(what, path));
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
    }
}
