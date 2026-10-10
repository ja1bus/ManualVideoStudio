namespace ManualVideoStudio.Core.Persistence;

/// <summary>
/// ファイルを読めなかったときの案内（v1.4.2）。
/// PC によってはファイルを置いた瞬間に拡張子で暗号化されるため、読み込みの失敗には「壊れているか暗号化されている」可能性と
/// 対象のフルパスを示し、暗号化を解除してから開き直せるようにする。
/// 拡張子から分かる形式では先頭のバイト（シグネチャ）を確かめ、形式と合わなければ暗号化の可能性が高いと示す。
/// </summary>
public static class FileReadProblem
{
    public const string Hint = "ファイルが壊れているか、暗号化されている可能性があります。暗号化されている場合は解除してから、もう一度開いてください。";

    /// <summary>
    /// 利用者に見せるメッセージ。「見出し」「形式の判定（分かる場合）」「案内」「フルパス」の順。
    /// </summary>
    public static string Message(string headline, string path)
    {
        var full = SafeFullPath(path);
        var lines = new List<string> { headline };
        switch (Check(full))
        {
            case SignatureCheck.Unreadable:
                lines.Add("ファイルを開けません（読み取りが拒否されました）。");
                break;
            case SignatureCheck.Mismatch:
                lines.Add($"ファイルの中身が {FormatName(full)} の形式になっていません。");
                break;
        }
        lines.Add(Hint);
        lines.Add("");
        lines.Add("ファイル: " + full);
        return string.Join(Environment.NewLine, lines);
    }

    public enum SignatureCheck { Unknown, Match, Mismatch, Unreadable }

    /// <summary>先頭のバイトが拡張子の形式と合うか。判定できない拡張子は Unknown。</summary>
    public static SignatureCheck Check(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!Signatures.ContainsKey(ext) && ext != ".json") return SignatureCheck.Unknown;
        byte[] head;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            head = new byte[64];
            var n = fs.Read(head, 0, head.Length);
            Array.Resize(ref head, n);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex is FileNotFoundException or DirectoryNotFoundException ? SignatureCheck.Unknown : SignatureCheck.Unreadable;
        }
        if (head.Length == 0) return SignatureCheck.Mismatch;
        if (ext == ".json") return LooksLikeJson(head) ? SignatureCheck.Match : SignatureCheck.Mismatch;
        return Signatures[ext](head) ? SignatureCheck.Match : SignatureCheck.Mismatch;
    }

    private static readonly Dictionary<string, Func<byte[], bool>> Signatures = new()
    {
        [".mp4"] = IsIsoMedia,
        [".m4v"] = IsIsoMedia,
        [".mov"] = IsIsoMedia,
        [".mkv"] = h => StartsWith(h, 0x1A, 0x45, 0xDF, 0xA3),
        [".webm"] = h => StartsWith(h, 0x1A, 0x45, 0xDF, 0xA3),
        [".avi"] = h => StartsWith(h, (byte)'R', (byte)'I', (byte)'F', (byte)'F'),
        [".wmv"] = h => StartsWith(h, 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11),
        [".png"] = h => StartsWith(h, 0x89, (byte)'P', (byte)'N', (byte)'G'),
        [".jpg"] = h => StartsWith(h, 0xFF, 0xD8, 0xFF),
        [".jpeg"] = h => StartsWith(h, 0xFF, 0xD8, 0xFF),
        [".gif"] = h => StartsWith(h, (byte)'G', (byte)'I', (byte)'F', (byte)'8'),
    };

    private static string FormatName(string path) => Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

    /// <summary>MP4 / MOV 系: 先頭のボックスが ftyp（まれに moov・mdat・free・wide・skip）。</summary>
    private static bool IsIsoMedia(byte[] h)
    {
        if (h.Length < 8) return false;
        var type = System.Text.Encoding.ASCII.GetString(h, 4, 4);
        return type is "ftyp" or "moov" or "mdat" or "free" or "wide" or "skip";
    }

    /// <summary>JSON: BOM と空白を除いた最初の文字が { または [。</summary>
    private static bool LooksLikeJson(byte[] h)
    {
        int i = h.Length >= 3 && h[0] == 0xEF && h[1] == 0xBB && h[2] == 0xBF ? 3 : 0;
        while (i < h.Length && h[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i < h.Length && h[i] is (byte)'{' or (byte)'[';
    }

    private static bool StartsWith(byte[] h, params byte[] sig) => h.Length >= sig.Length && h.AsSpan(0, sig.Length).SequenceEqual(sig);

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}

/// <summary>起動時に読めなかった設定ファイルなど、画面が出てから知らせる案内（v1.4.2）。</summary>
public static class FileLoadProblems
{
    private static readonly List<string> Messages = [];

    public static void Report(string message)
    {
        lock (Messages) Messages.Add(message);
    }

    /// <summary>溜まった案内を取り出して空にする。</summary>
    public static IReadOnlyList<string> TakeAll()
    {
        lock (Messages)
        {
            var all = Messages.ToList();
            Messages.Clear();
            return all;
        }
    }
}
