using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace ManualVideoStudio.Services;

/// <summary>
/// フォント選択欄の 1 項目。Windows（GDI）のファミリー名 1 つに対応する。
/// VideoCaptionLight の Services/FontCatalog.cs から移植（v1.5。字幕の幅を測るため標準の書体のファイル RegularFace を追加）。
/// </summary>
public sealed class FontChoice
{
    public FontChoice(string name, string displayName, bool supportsJapanese, IReadOnlyList<string> allNames, IReadOnlyList<string> postScriptNames,
        Uri? regularFace = null)
    {
        Name = name;
        DisplayName = displayName;
        SupportsJapanese = supportsJapanese;
        AllNames = allNames;
        PostScriptNames = postScriptNames;
        RegularFace = regularFace;
    }

    /// <summary>標準の太さ・字形の書体のフォントファイル（無ければ最初の書体）。字幕の幅を測るのに使う。</summary>
    public Uri? RegularFace { get; }

    /// <summary>ffmpeg（libass）の FontName に渡す名前（英語の GDI ファミリー名）。設定にもこれを保存する。</summary>
    public string Name { get; }

    /// <summary>一覧に出す名前。日本語名があれば日本語名。</summary>
    public string DisplayName { get; }

    /// <summary>かな・漢字の字形を持つか。持たないフォントでは日本語が別のフォントで描画される。</summary>
    public bool SupportsJapanese { get; }

    /// <summary>言語ごとのファミリー名すべて。保存値が日本語名だった場合にも見つけられるようにする。</summary>
    public IReadOnlyList<string> AllNames { get; }

    /// <summary>
    /// このファミリーに属する書体の PostScript 名。libass はログにこの名前で「実際に使ったフォント」を出すので、
    /// 指定どおりのフォントが使われたかを正確に判定できる。読めなかった場合は空。
    /// </summary>
    public IReadOnlyList<string> PostScriptNames { get; }

    /// <summary>一覧の 2 列目。日本語名で表示しているときは英語名、日本語が無いフォントはその旨。</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.Equals(DisplayName, Name, StringComparison.Ordinal))
            {
                parts.Add(Name);
            }

            if (!SupportsJapanese)
            {
                parts.Add("日本語なし");
            }

            return string.Join(" / ", parts);
        }
    }

    public override string ToString() => DisplayName;
}

/// <summary>Windows にインストールされているフォントの一覧と、既定フォントへのフォールバック（仕様書 20.2）。</summary>
public static class FontCatalog
{
    /// <summary>保存済みのフォントが無いときに順に試す。先頭は仕様の既定値 Meiryo。</summary>
    private static readonly string[] FallbackNames =
    {
        "Meiryo", "Yu Gothic UI", "Yu Gothic", "BIZ UDPGothic", "BIZ UDGothic", "MS UI Gothic", "MS Gothic", "Meiryo UI",
    };

    // force_style の値は「,」で区切られ、フィルタ記法では「' : \ ; [ ]」が特別な意味を持つ。
    // こうした文字を含むフォント名は ffmpeg に正しく渡せないので、一覧に出さない（実在するフォントでは見たことがない）。
    private static readonly char[] UnsafeNameChars = { '\'', ',', ':', '\\', ';', '[', ']', '=' };

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    /// <summary>
    /// インストール済みフォントを、日本語対応フォント → その他 の順に並べて返す。
    /// <para>
    /// 名前は WPF のファミリー名ではなく、書体ごとの Win32（GDI）ファミリー名を使う。
    /// WPF は太さ違いを 1 ファミリーにまとめる（例: 「BIZ UDPMincho」の Medium）が、libass が照合するのは
    /// Win32 の名前（例: 「BIZ UDPMincho Medium」）で、WPF の名前を渡すと見つからずに Arial で描画される。
    /// </para>
    /// <para>
    /// 可変フォント（Noto Sans JP、Bahnschrift、Segoe UI Variable など）の太さ違い（例: 「Noto Sans JP Black」）は
    /// Windows が合成した名前で、フォントファイルには無い。libass はこの名前では見つけられないので一覧から外し、
    /// ファイルに実在するファミリー名（例: 「Noto Sans JP」）だけを残す。
    /// </para>
    /// 書体を 1 つずつ開くため初回は数秒かかる。UI スレッド以外から呼ぶこと。
    /// </summary>
    public static IReadOnlyList<FontChoice> LoadInstalledFonts()
    {
        var builders = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);

        foreach (var family in Fonts.SystemFontFamilies)
        {
            // 複合フォント（Global User Interface など）は WPF だけの仮想フォントで、ffmpeg からは使えない。
            if (family.FamilyMaps.Count > 0)
            {
                continue;
            }

            foreach (var typeface in SafeTypefaces(family))
            {
                GlyphTypeface glyphs;
                try
                {
                    if (!typeface.TryGetGlyphTypeface(out glyphs))
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    // 壊れたフォントファイルなど。一覧から外すだけでよい。
                    continue;
                }

                var win32Names = glyphs.Win32FamilyNames;
                var name = NameIn(win32Names, English) ?? win32Names.Values.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(UnsafeNameChars) >= 0)
                {
                    continue;
                }

                if (!builders.TryGetValue(name, out var builder))
                {
                    builder = new Builder(name, NameIn(win32Names, Japanese) ?? name, SupportsJapanese(glyphs));
                    builders[name] = builder;
                }

                foreach (var alias in win32Names.Values)
                {
                    builder.Names.Add(alias);
                }

                builder.AddFace(glyphs.FontUri, typeface.Weight == FontWeights.Normal && typeface.Style == FontStyles.Normal);
            }
        }

        // 同じファイルが複数のファミリーに現れる（可変フォントの太さ違いなど）ので、書体ごとに 1 回だけ読む。
        // Uri.Equals は TTC の書体番号（#1 などのフラグメント）を無視するので、フラグメント込みの文字列で区別する。
        var faces = new Dictionary<string, FontFaceInfo?>(StringComparer.OrdinalIgnoreCase);
        FontFaceInfo? ReadFace(Uri uri)
        {
            if (!faces.TryGetValue(uri.AbsoluteUri, out var info))
            {
                info = FontFileNames.TryRead(uri);
                faces[uri.AbsoluteUri] = info;
            }

            return info;
        }

        var comparer = StringComparer.Create(Japanese, ignoreCase: true);
        return builders.Values
            .Select(b => b.Build(ReadFace))
            .OfType<FontChoice>()
            .OrderByDescending(f => f.SupportsJapanese)
            .ThenBy(f => f.DisplayName, comparer)
            .ToList();
    }

    /// <summary>
    /// 保存済みのフォント名に対応する項目を探す。無ければ Meiryo → 他の日本語フォント → 先頭 の順で代わりを返し、
    /// <paramref name="note"/> に利用者へ伝える文を入れる。
    /// </summary>
    public static FontChoice? Resolve(IReadOnlyList<FontChoice> fonts, string? preferred, out string? note)
    {
        note = null;
        if (fonts.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferred) && Find(fonts, preferred) is { } exact)
        {
            return exact;
        }

        var fallback = FallbackNames.Select(n => Find(fonts, n)).FirstOrDefault(f => f is not null)
                       ?? fonts.FirstOrDefault(f => f.SupportsJapanese)
                       ?? fonts[0];

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            note = $"フォント「{preferred}」がこの PC に無いため、「{fallback.DisplayName}」を使います。";
        }

        return fallback;
    }

    public static FontChoice? Find(IReadOnlyList<FontChoice> fonts, string name)
        => fonts.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
           ?? fonts.FirstOrDefault(f => f.AllNames.Contains(name, StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<Typeface> SafeTypefaces(FontFamily family)
    {
        try
        {
            return family.GetTypefaces().ToList();
        }
        catch (Exception)
        {
            return Array.Empty<Typeface>();
        }
    }

    private static string? NameIn(IDictionary<CultureInfo, string> names, CultureInfo culture)
        => names.TryGetValue(culture, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>ひらがな・カタカナ・漢字の字形を持つか。</summary>
    private static bool SupportsJapanese(GlyphTypeface glyphs)
    {
        try
        {
            var map = glyphs.CharacterToGlyphMap;
            return map.ContainsKey('あ') && map.ContainsKey('ア') && map.ContainsKey('字');
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class Builder(string name, string displayName, bool supportsJapanese)
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase) { name };

        /// <summary>書体のフォントファイル。キーはフラグメント（TTC の書体番号）込みの URI 文字列。</summary>
        private Dictionary<string, Uri> Faces { get; } = new(StringComparer.OrdinalIgnoreCase);

        private Uri? _regular;

        public void AddFace(Uri uri, bool regular)
        {
            Faces.TryAdd(uri.AbsoluteUri, uri);
            if (_regular is null || regular && !_regularIsNormal)
            {
                _regular = uri;
                _regularIsNormal = regular;
            }
        }

        private bool _regularIsNormal;

        /// <summary>可変フォントの合成名だけでできたファミリー（libass が見つけられない）なら null。</summary>
        public FontChoice? Build(Func<Uri, FontFaceInfo?> readFace)
        {
            var usable = Faces.Values
                .Select(readFace)
                .Where(i => i is null || !i.IsVariable || i.FamilyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (usable.Count == 0)
            {
                return null;
            }

            var postScriptNames = usable
                .Select(i => i?.PostScriptName)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return new FontChoice(name, displayName, supportsJapanese, Names.ToArray(), postScriptNames, _regular);
        }
    }
}


/// <summary>フォントファイルから読んだ書体の情報。</summary>
/// <param name="PostScriptName">name ID 6。libass はログにこの名前で「実際に使ったフォント」を出す。</param>
/// <param name="FamilyNames">name ID 1（全言語）。libass はこの名前でフォントを探す。</param>
/// <param name="IsVariable">可変フォント（fvar テーブルあり）か。</param>
internal sealed record FontFaceInfo(string? PostScriptName, IReadOnlyList<string> FamilyNames, bool IsVariable);

/// <summary>
/// フォントファイルの name テーブルを直接読む。WPF からは PostScript 名や可変フォントかどうかを取れないため。
/// TTC（複数書体を含むファイル）にも対応する。
/// </summary>
internal static class FontFileNames
{
    private const ushort FamilyNameId = 1;
    private const ushort PostScriptNameId = 6;

    /// <summary>読めなければ null。フォントの判定に使うだけなので、失敗しても処理は続ける。</summary>
    public static FontFaceInfo? TryRead(Uri uri)
    {
        try
        {
            if (!uri.IsFile)
            {
                return null;
            }

            // TTC の何番目の書体かは URI のフラグメント（#1 など）で表される。
            var index = 0;
            if (uri.Fragment.Length > 1)
            {
                _ = int.TryParse(uri.Fragment[1..], NumberStyles.None, CultureInfo.InvariantCulture, out index);
            }

            using var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream, index);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static FontFaceInfo? Read(Stream stream, int faceIndex)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        long offsetTable = 0;
        if (ReadTag(reader, 0) == "ttcf")
        {
            stream.Position = 8;
            var numFonts = ReadUInt32(reader);
            if (faceIndex < 0 || faceIndex >= numFonts || numFonts > 1024)
            {
                return null;
            }

            stream.Position = 12 + (4L * faceIndex);
            offsetTable = ReadUInt32(reader);
        }

        stream.Position = offsetTable + 4;
        var numTables = ReadUInt16(reader);
        if (numTables > 512)
        {
            return null;
        }

        long? nameTable = null;
        var isVariable = false;
        for (var i = 0; i < numTables; i++)
        {
            var entry = offsetTable + 12 + (16L * i);
            switch (ReadTag(reader, entry))
            {
                case "name":
                    stream.Position = entry + 8;
                    nameTable = ReadUInt32(reader);
                    break;
                case "fvar":
                    isVariable = true;
                    break;
            }
        }

        if (nameTable is null)
        {
            return null;
        }

        var records = ReadNameRecords(reader, nameTable.Value);
        var postScript = records
            .Where(r => r.Id == PostScriptNameId)
            .OrderByDescending(r => r.IsWindowsEnglish)
            .Select(r => r.Text)
            .FirstOrDefault();
        var families = records
            .Where(r => r.Id == FamilyNameId)
            .Select(r => r.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FontFaceInfo(postScript, families, isVariable);
    }

    private sealed record NameRecord(ushort Id, string Text, bool IsWindowsEnglish);

    private static List<NameRecord> ReadNameRecords(BinaryReader reader, long nameTable)
    {
        var stream = reader.BaseStream;
        stream.Position = nameTable + 2;
        var count = ReadUInt16(reader);
        var storage = nameTable + ReadUInt16(reader);

        var records = new List<NameRecord>();
        for (var i = 0; i < count; i++)
        {
            stream.Position = nameTable + 6 + (12L * i);
            var platform = ReadUInt16(reader);
            var encoding = ReadUInt16(reader);
            var language = ReadUInt16(reader);
            var id = ReadUInt16(reader);
            var length = ReadUInt16(reader);
            var offset = ReadUInt16(reader);

            if (id is not (FamilyNameId or PostScriptNameId) || length == 0)
            {
                continue;
            }

            stream.Position = storage + offset;
            var bytes = reader.ReadBytes(length);

            // Windows / Unicode の名前は UTF-16BE。Macintosh の名前は 1 バイト文字（英語の Roman のみ使う）。
            string text;
            if (platform is 0 or 3)
            {
                text = Encoding.BigEndianUnicode.GetString(bytes);
            }
            else if (platform == 1 && encoding == 0)
            {
                text = Encoding.ASCII.GetString(bytes);
            }
            else
            {
                continue;
            }

            records.Add(new NameRecord(id, text, platform == 3 && language == 0x0409));
        }

        return records;
    }

    private static string ReadTag(BinaryReader reader, long position)
    {
        reader.BaseStream.Position = position;
        return Encoding.ASCII.GetString(reader.ReadBytes(4));
    }

    private static ushort ReadUInt16(BinaryReader reader)
    {
        var b = reader.ReadBytes(2);
        return b.Length == 2 ? (ushort)((b[0] << 8) | b[1]) : throw new EndOfStreamException();
    }

    private static uint ReadUInt32(BinaryReader reader)
    {
        var b = reader.ReadBytes(4);
        return b.Length == 4 ? (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]) : throw new EndOfStreamException();
    }
}
