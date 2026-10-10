using System.Text.Json;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Ai;

public interface IPromptTemplateService
{
    string Get(AiPurpose purpose);
    bool IsCustomized(AiPurpose purpose);
    void Save(AiPurpose purpose, string text);
    void ResetToDefault(AiPurpose purpose);
}

/// <summary>
/// ユーザーが編集したテンプレートのファイル形式。未編集の用途はキーを持たない（既定値を使う）。
/// 手順書作成の依頼文（v1.9）は templates の外の tejunshoRequest に置く（v1.8 以前のアプリが知らない列挙値で
/// ファイル全体を読めなくならないように。知らないプロパティは読み飛ばされる）。
/// </summary>
public sealed class PromptTemplateFile
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<AiPurpose, string> Templates { get; set; } = [];
    public string? TejunshoRequest { get; set; }
    /// <summary>字幕依頼の文（v1.12。TejunshoRequest と同じ理由で templates の外に置く）。</summary>
    public string? SubtitleRequest { get; set; }
}

/// <summary>
/// プロンプトテンプレートの管理（%APPDATA%\ManualVideoStudio\prompt-templates.json）。
/// 既定テンプレートはアプリ内蔵（DefaultPromptTemplates）。空文字は保存せず既定値に戻す。
/// </summary>
public sealed class PromptTemplateService : IPromptTemplateService
{
    private readonly string _path;
    private readonly ILogger _logger;
    private PromptTemplateFile _file;

    public PromptTemplateService(string path, ILogger<PromptTemplateService>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<PromptTemplateService>.Instance;
        _file = SettingsService.LoadJson<PromptTemplateFile>(path, _logger,
            "プロンプトテンプレートを読み込めなかったため、既定のテンプレートを使います（元のファイルは末尾に .bad を付けて残しています）。") ?? new PromptTemplateFile();
        _file.Templates ??= [];
    }

    public static string TemplateName(AiPurpose purpose) => purpose.ToString();

    public string Get(AiPurpose purpose)
        => Stored(purpose) is { } t && !string.IsNullOrWhiteSpace(t) ? t : DefaultPromptTemplates.Get(purpose);

    public bool IsCustomized(AiPurpose purpose)
        => Stored(purpose) is { } t && !string.IsNullOrWhiteSpace(t) && t != DefaultPromptTemplates.Get(purpose);

    public void Save(AiPurpose purpose, string text)
    {
        Store(purpose, string.IsNullOrWhiteSpace(text) || text == DefaultPromptTemplates.Get(purpose) ? null : text);
        Persist();
    }

    public void ResetToDefault(AiPurpose purpose)
    {
        Store(purpose, null);
        Persist();
    }

    private string? Stored(AiPurpose purpose)
        => purpose == AiPurpose.TejunshoRequest ? _file.TejunshoRequest
            : purpose == AiPurpose.SubtitleRequest ? _file.SubtitleRequest
            : _file.Templates.TryGetValue(purpose, out var t) ? t : null;

    private void Store(AiPurpose purpose, string? text)
    {
        if (purpose == AiPurpose.TejunshoRequest) _file.TejunshoRequest = text;
        else if (purpose == AiPurpose.SubtitleRequest) _file.SubtitleRequest = text;
        else if (text is null) _file.Templates.Remove(purpose);
        else _file.Templates[purpose] = text;
    }

    private void Persist()
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_file, JsonDefaults.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Prompt template save failed");
            throw new InvalidOperationException($"テンプレートを保存できませんでした: {ex.Message}", ex);
        }
    }
}
