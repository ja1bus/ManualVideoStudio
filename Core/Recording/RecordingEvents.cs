using System.Text.Json;
using System.Text.Json.Serialization;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Core.Recording;

/// <summary>録画中のクリック 1 回（v1.21）。T は動画の秒、X / Y は動画の px（録画範囲の左上から）、Button は "left" / "right"。</summary>
public sealed record RecordedClick(double T, int X, int Y, string Button)
{
    public const string Left = "left";
    public const string Right = "right";
}

/// <summary>
/// 録画中の操作の記録（v1.21）。時刻はすべて元動画の秒。録画フォルダーの「動画名.events.json」に置き、プロジェクトには入れない。
/// マーカーは「この直前は不要」の印。クリックは録画範囲の中のものだけ位置付きで残す。
/// キー・ホイールの範囲とマウス移動の範囲は、間隔 0.5 秒以内の入力をまとめたもの（キーの種類・内容は持たない）。
/// </summary>
public sealed class RecordingEvents
{
    public const int CurrentSchemaVersion = 1;
    /// <summary>クリックを「入力あり」とみなす前後の幅（秒）。</summary>
    public const double ClickPadding = 0.25;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>この記録が説明する動画のファイル名（同じフォルダー内）。</summary>
    public string VideoFileName { get; set; } = "";
    /// <summary>
    /// クリック位置と入力の時刻を記録したか（録画時の「記録する」の設定）。false ならマーカーだけが意味を持ち、入力の無い範囲は求めない。
    /// 連結で片方にしか記録が無い場合も false。項目が無いファイルは true として読む。
    /// </summary>
    public bool InputLogged { get; set; } = true;
    public List<double> Markers { get; set; } = [];
    public List<RecordedClick> Clicks { get; set; } = [];
    [JsonConverter(typeof(TimeRangePairListConverter))]
    public List<TimeRange> KeyRanges { get; set; } = [];
    [JsonConverter(typeof(TimeRangePairListConverter))]
    public List<TimeRange> MoveRanges { get; set; } = [];

    /// <summary>
    /// 入力の無い範囲: クリック（前後 0.25 秒）・キー範囲・マウス移動範囲のどれにも当たらない、minSeconds 以上の範囲（[0, duration] の中）。
    /// 入力を記録していない記録では空。
    /// </summary>
    public IReadOnlyList<TimeRange> IdleRanges(double duration, double minSeconds)
    {
        var result = new List<TimeRange>();
        if (!InputLogged || !(duration > 0)) return result;
        var busy = new List<TimeRange>();
        busy.AddRange(Clicks.Select(c => new TimeRange(c.T - ClickPadding, c.T + ClickPadding)));
        busy.AddRange(KeyRanges);
        busy.AddRange(MoveRanges);
        busy.Sort((a, b) => a.Start.CompareTo(b.Start));

        double cursor = 0;
        foreach (var b in busy)
        {
            if (b.End <= cursor) continue;
            AddIdle(result, cursor, Math.Min(b.Start, duration), minSeconds);
            cursor = Math.Max(cursor, b.End);
            if (cursor >= duration) break;
        }
        AddIdle(result, cursor, duration, minSeconds);
        return result;
    }

    private static void AddIdle(List<TimeRange> list, double start, double end, double minSeconds)
    {
        if (end - start >= minSeconds && end > start) list.Add(new TimeRange(start, end));
    }

    /// <summary>すべての時刻を offset 秒ずらした複製。</summary>
    public RecordingEvents Shifted(double offset) => new()
    {
        SchemaVersion = SchemaVersion,
        VideoFileName = VideoFileName,
        InputLogged = InputLogged,
        Markers = Markers.Select(m => Round(m + offset)).ToList(),
        Clicks = Clicks.Select(c => c with { T = Round(c.T + offset) }).ToList(),
        KeyRanges = KeyRanges.Select(r => new TimeRange(Round(r.Start + offset), Round(r.End + offset))).ToList(),
        MoveRanges = MoveRanges.Select(r => new TimeRange(Round(r.Start + offset), Round(r.End + offset))).ToList(),
    };

    /// <summary>
    /// 連結した動画の記録: a ＋ b を offsetForB 秒ずらしたもの。片方が無ければある分だけ（その場合は入力の無い範囲を求めない）。
    /// 両方無ければ null。VideoFileName は空（保存時に入れる）。
    /// </summary>
    public static RecordingEvents? Merge(RecordingEvents? a, RecordingEvents? b, double offsetForB)
    {
        if (a is null && b is null) return null;
        var shiftedB = b?.Shifted(offsetForB);
        var result = new RecordingEvents { InputLogged = a is not null && b is not null && a.InputLogged && b.InputLogged };
        foreach (var e in new[] { a, shiftedB })
        {
            if (e is null) continue;
            result.Markers.AddRange(e.Markers);
            result.Clicks.AddRange(e.Clicks);
            result.KeyRanges.AddRange(e.KeyRanges);
            result.MoveRanges.AddRange(e.MoveRanges);
        }
        result.SortAll();
        return result;
    }

    /// <summary>時刻順に並べる。</summary>
    public void SortAll()
    {
        Markers.Sort();
        Clicks.Sort((x, y) => x.T.CompareTo(y.T));
        KeyRanges.Sort((x, y) => x.Start.CompareTo(y.Start));
        MoveRanges.Sort((x, y) => x.Start.CompareTo(y.Start));
    }

    /// <summary>読み込んだ値を整える（null の一覧を空に、数でない値・逆向きの範囲を除く）。</summary>
    internal void Normalize()
    {
        VideoFileName ??= "";
        Markers = (Markers ?? []).Where(double.IsFinite).ToList();
        Clicks = (Clicks ?? []).Where(c => c is not null && double.IsFinite(c.T)).Select(c => c.Button is null ? c with { Button = RecordedClick.Left } : c).ToList();
        KeyRanges = (KeyRanges ?? []).Where(IsValid).ToList();
        MoveRanges = (MoveRanges ?? []).Where(IsValid).ToList();
        SortAll();
    }

    private static bool IsValid(TimeRange r) => double.IsFinite(r.Start) && double.IsFinite(r.End) && r.End >= r.Start;

    internal static double Round(double seconds) => Math.Round(seconds, 3);
}

/// <summary>範囲の一覧を [[start, end], ...] の形で読み書きする。</summary>
public sealed class TimeRangePairListConverter : JsonConverter<List<TimeRange>>
{
    public override List<TimeRange>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return [];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("範囲の一覧は配列で書きます。");
        var list = new List<TimeRange>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("範囲は [開始, 終了] で書きます。");
            var values = new List<double>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                values.Add(reader.GetDouble());
            if (values.Count != 2) throw new JsonException("範囲は [開始, 終了] の 2 つの数で書きます。");
            list.Add(new TimeRange(values[0], values[1]));
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<TimeRange> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var r in value)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(r.Start);
            writer.WriteNumberValue(r.End);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// 録画の操作記録のファイル（動画と同じフォルダーの「動画名（拡張子なし）.events.json」）。
/// 無い・読めない・別の動画の記録なら null（記録を使う機能が使えないだけ）。
/// </summary>
public static class RecordingEventsFile
{
    public const string Suffix = ".events.json";

    /// <summary>videoPath の動画の記録ファイルのパス。</summary>
    public static string PathFor(string videoPath)
    {
        var full = Path.GetFullPath(videoPath);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + Suffix);
    }

    /// <summary>videoPath の動画の記録として保存する（VideoFileName は動画のファイル名にする）。</summary>
    public static void Save(string videoPath, RecordingEvents events)
    {
        events.SchemaVersion = RecordingEvents.CurrentSchemaVersion;
        events.VideoFileName = Path.GetFileName(videoPath);
        events.SortAll();
        AtomicFile.WriteAllText(PathFor(videoPath), JsonSerializer.Serialize(events, JsonDefaults.Options));
    }

    /// <summary>videoPath の動画の記録。無い・読めない・別の動画の記録なら null。</summary>
    public static RecordingEvents? TryReadFor(string videoPath) => TryReadFor(videoPath, out _);

    /// <summary>
    /// videoPath の動画の記録。無い・読めない・別の動画の記録なら null。
    /// ファイルはあるが読めなかった（壊れている・暗号化されている・形式が違う）ときは problemPath にフルパスを返す
    /// （呼び出し側が <see cref="FileReadProblem"/> の案内とともにログへ出す）。無い・別の動画の記録なら null。
    /// </summary>
    public static RecordingEvents? TryReadFor(string videoPath, out string? problemPath)
    {
        problemPath = null;
        string path;
        try
        {
            path = PathFor(videoPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!File.Exists(path)) return null;
        try
        {
            var events = JsonSerializer.Deserialize<RecordingEvents>(File.ReadAllText(path), JsonDefaults.Options);
            if (events is null)
            {
                problemPath = path;
                return null;
            }
            // 新しい版の記録は読まない（使えないだけで、読めないファイルではない）。
            if (events.SchemaVersion != RecordingEvents.CurrentSchemaVersion) return null;
            if (!string.Equals(events.VideoFileName, Path.GetFileName(videoPath), StringComparison.OrdinalIgnoreCase)) return null;
            events.Normalize();
            return events;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            problemPath = path;
            return null;
        }
    }
}
