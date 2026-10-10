namespace ManualVideoStudio.Core.Analysis;

public enum AnalysisPreset
{
    Careful,
    Standard,
    Aggressive,
    Custom,
}

/// <summary>ローカル自動解析の設定。プリセットは値の組み合わせで、Custom は個別値をそのまま使う。</summary>
public sealed record AnalysisSettings
{
    public AnalysisPreset Preset { get; init; } = AnalysisPreset.Standard;

    public bool EnableFreeze { get; init; } = true;
    public bool EnableSilence { get; init; } = true;
    public bool EnableSceneChange { get; init; } = true;

    /// <summary>静止とみなす最短時間（秒）。freezedetect の d。</summary>
    public double FreezeMinSeconds { get; init; } = 3.0;
    /// <summary>静止判定のノイズ許容（dB）。freezedetect の n。小さい（-70 等）ほど厳密。</summary>
    public double FreezeNoiseDb { get; init; } = -60;
    /// <summary>無音とみなす最短時間（秒）。silencedetect の d。</summary>
    public double SilenceMinSeconds { get; init; } = 3.0;
    /// <summary>無音とみなす音量（dB）。silencedetect の noise。</summary>
    public double SilenceNoiseDb { get; init; } = -45;
    /// <summary>画面変化とみなすシーンスコア（0～1）。</summary>
    public double SceneThreshold { get; init; } = 0.3;

    /// <summary>候補区間の前後に残す余白（秒）。操作直後・直前の画面を消し過ぎないため。</summary>
    public double EdgeMarginSeconds { get; init; } = 0.5;
    /// <summary>短縮候補で残す秒数。</summary>
    public double ShortenKeepSeconds { get; init; } = 1.5;
    /// <summary>無音のみ（映像は動いている）の区間も候補にするか。</summary>
    public bool IncludeSilenceOnly { get; init; }
    /// <summary>解析時に映像を縮小する幅（px）。処理を軽くし、カーソル点滅など微小な変化の影響を減らす。0 は縮小しない。</summary>
    public int ScaleWidth { get; init; } = 640;

    public static AnalysisSettings FromPreset(AnalysisPreset preset) => preset switch
    {
        AnalysisPreset.Careful => new AnalysisSettings
        {
            Preset = preset, FreezeMinSeconds = 5.0, SilenceMinSeconds = 5.0, FreezeNoiseDb = -65,
            EdgeMarginSeconds = 0.8, ShortenKeepSeconds = 2.0,
        },
        AnalysisPreset.Aggressive => new AnalysisSettings
        {
            Preset = preset, FreezeMinSeconds = 1.5, SilenceMinSeconds = 1.5, FreezeNoiseDb = -55,
            EdgeMarginSeconds = 0.3, ShortenKeepSeconds = 1.0, IncludeSilenceOnly = true,
        },
        _ => new AnalysisSettings { Preset = preset == AnalysisPreset.Custom ? AnalysisPreset.Custom : AnalysisPreset.Standard },
    };
}
