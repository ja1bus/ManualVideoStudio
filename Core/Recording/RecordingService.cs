using System.Diagnostics;
using System.Text;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Recording;

public interface IRecordingService
{
    RecordingState State { get; }
    /// <summary>録画した時間（一時停止中の時間を含まない）。</summary>
    TimeSpan Elapsed { get; }
    int SegmentCount { get; }
    /// <summary>状態が変わった（任意のスレッドから発生する）。</summary>
    event EventHandler? StateChanged;
    /// <summary>録画中に ffmpeg が停止操作以外で終了した（デバイスの取り外し等）。引数は ffmpeg のエラー。任意のスレッドから発生する。</summary>
    event EventHandler<string>? Interrupted;

    Task StartAsync(RecordingStartRequest request, CancellationToken ct = default);
    Task PauseAsync();
    Task ResumeAsync(CancellationToken ct = default);
    Task<RecordingResult> StopAsync(CancellationToken ct = default);
    /// <summary>録画を中止する（ffmpeg を止め、途中のファイルを削除する）。</summary>
    Task AbortAsync();
}

public sealed record RecordingStartRequest(string FfmpegPath, string FfprobePath, ScreenRecordingOptions Options, string OutputPath, RecordingInfo Info);

/// <summary>
/// 画面録画の実行（UI 非依存）。一時停止は「ffmpeg を止めて区間を閉じる」、再開は「新しい区間を始める」で実現し、
/// 停止時に区間を再エンコードせずに 1 本の MP4 へ連結する。一時停止中の時間は動画に含まれない。
/// 区間は出力先フォルダー内の作業フォルダー（.mvs-rec-*）に Matroska で書き、成功したら削除する。
/// </summary>
public sealed class RecordingService(IRecordingProcessFactory factory, IProcessRunner runner, IMediaMetadataService metadata,
    ILogger<RecordingService>? logger = null) : IRecordingService
{
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private readonly ILogger _logger = logger ?? NullLogger<RecordingService>.Instance;
    private readonly List<string> _segments = [];
    private readonly Stopwatch _segmentClock = new();
    private TimeSpan _closedDuration;
    private RecordingStartRequest? _request;
    private string? _workDir;
    private IRecordingProcess? _current;
    private bool _stopping;

    public RecordingState State { get; private set; } = RecordingState.Idle;
    public TimeSpan Elapsed => _closedDuration + _segmentClock.Elapsed;
    public int SegmentCount => _segments.Count;
    /// <summary>作業フォルダー（失敗時に途中のファイルを残す場所）。</summary>
    public string? WorkDirectory => _workDir;

    public event EventHandler? StateChanged;
    public event EventHandler<string>? Interrupted;

    public async Task StartAsync(RecordingStartRequest request, CancellationToken ct = default)
    {
        if (State is not (RecordingState.Idle or RecordingState.Completed or RecordingState.Failed))
            throw new InvalidOperationException("録画中です。");
        _request = request;
        _segments.Clear();
        _closedDuration = TimeSpan.Zero;
        _segmentClock.Reset();
        var outDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!;
        Directory.CreateDirectory(outDir);
        _workDir = Path.Combine(outDir, $".mvs-rec-{Guid.NewGuid():N}");
        var di = Directory.CreateDirectory(_workDir);
        try { di.Attributes |= FileAttributes.Hidden; } catch (IOException) { }
        _logger.LogInformation("Recording start: {Region} {Fps}fps cursor={Cursor} audio={Audio} -> {Output}",
            request.Options.Region, request.Options.Fps, request.Options.DrawCursor, request.Options.Audio?.DisplayName ?? "none", request.OutputPath);
        SetState(RecordingState.Starting);
        try
        {
            await StartSegmentAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            SetState(RecordingState.Failed);
            TryDeleteWorkDir();
            throw;
        }
        SetState(RecordingState.Recording);
    }

    public async Task PauseAsync()
    {
        if (State != RecordingState.Recording) return;
        await CloseSegmentAsync().ConfigureAwait(false);
        SetState(RecordingState.Paused);
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        if (State != RecordingState.Paused) return;
        SetState(RecordingState.Starting);
        try
        {
            await StartSegmentAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 再開に失敗しても、それまでの区間は停止で保存できるよう一時停止に戻す。
            SetState(RecordingState.Paused);
            throw;
        }
        SetState(RecordingState.Recording);
    }

    public async Task<RecordingResult> StopAsync(CancellationToken ct = default)
    {
        if (_request is null || State is RecordingState.Idle or RecordingState.Completed or RecordingState.Failed)
            throw new InvalidOperationException("録画していません。");
        SetState(RecordingState.Stopping);
        await CloseSegmentAsync().ConfigureAwait(false);
        try
        {
            var result = await FinalizeAsync(_request, ct).ConfigureAwait(false);
            SetState(RecordingState.Completed);
            TryDeleteWorkDir();
            return result;
        }
        catch (OperationCanceledException)
        {
            SetState(RecordingState.Failed);
            TryDeleteWorkDir();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording finalize failed; segments kept in {Dir}", _workDir);
            SetState(RecordingState.Failed);
            if (ex is RecordingException) throw;
            throw new RecordingException("録画ファイルを作成できませんでした。", ex.Message, ex);
        }
    }

    public async Task AbortAsync()
    {
        if (_current is not null) await CloseSegmentAsync().ConfigureAwait(false);
        if (State is not (RecordingState.Idle or RecordingState.Completed)) SetState(RecordingState.Failed);
        TryDeleteWorkDir();
        _logger.LogInformation("Recording aborted");
    }

    private async Task StartSegmentAsync(CancellationToken ct)
    {
        var r = _request!;
        var name = $"seg{_segments.Count + 1:000}.mkv";
        var process = factory.Create(r.FfmpegPath, FFmpegArguments.ScreenRecordingSegment(r.Options, name), _workDir!);
        _stopping = false;
        process.Exited += OnProcessExited;
        // gdigrab は ffmpeg の起動直後から取り込みを始めるが、進捗（書き込み開始）の報告はエンコーダーの準備の後になる。
        // 経過時間を実際の録画の長さに合わせるため、時計は起動時から計る。
        _segmentClock.Restart();
        try
        {
            await process.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _segmentClock.Reset();
            process.Exited -= OnProcessExited;
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _current = process;
        _segments.Add(name);
    }

    private async Task CloseSegmentAsync()
    {
        var p = _current;
        if (p is null) return;
        _stopping = true;
        _segmentClock.Stop();
        _closedDuration += _segmentClock.Elapsed;
        _segmentClock.Reset();
        var graceful = await p.StopAsync(StopTimeout).ConfigureAwait(false);
        if (!graceful) _logger.LogWarning("Segment was killed: {Segment}", _segments.LastOrDefault());
        p.Exited -= OnProcessExited;
        await p.DisposeAsync().ConfigureAwait(false);
        _current = null;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_stopping || sender != _current) return;
        var tail = (sender as IRecordingProcess)?.ErrorTail ?? "";
        _logger.LogWarning("Recording process exited unexpectedly: {Tail}", tail);
        Interrupted?.Invoke(this, tail);
    }

    private async Task<RecordingResult> FinalizeAsync(RecordingStartRequest r, CancellationToken ct)
    {
        var dir = _workDir!;
        // 1 フレームも書かれなかった区間（空・壊れたファイル）は連結から外す。
        var usable = _segments.Where(s => File.Exists(Path.Combine(dir, s)) && new FileInfo(Path.Combine(dir, s)).Length > 1024).ToList();
        if (usable.Count == 0)
            throw new RecordingException("録画ファイルを作成できませんでした（録画されたデータがありません）。");
        const string listName = FFmpegArguments.ConcatListFileName;
        await File.WriteAllTextAsync(Path.Combine(dir, listName), FFmpegArguments.ConcatList(usable), new UTF8Encoding(false), ct).ConfigureAwait(false);
        var temp = Path.Combine(dir, "output.mp4");
        var run = await runner.RunAsync(new ProcessRequest(r.FfmpegPath, FFmpegArguments.ConcatRecordingSegments(usable, listName, temp))
        {
            WorkingDirectory = dir,
            Timeout = TimeSpan.FromMinutes(30),
        }, ct).ConfigureAwait(false);
        if (!run.Succeeded || !File.Exists(temp))
            throw new RecordingException($"録画ファイルを作成できませんでした（区間の連結に失敗。終了コード {run.ExitCode}）。途中のファイルは次のフォルダーに残しています: {dir}", run.ErrorTail(10));

        VideoMetadata meta;
        try
        {
            meta = await metadata.ProbeAsync(r.FfprobePath, temp, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RecordingException($"録画ファイルが壊れている可能性があります（動画情報を読めません）。途中のファイルは次のフォルダーに残しています: {dir}", ex.Message, ex);
        }
        if (!meta.HasVideo || meta.Duration <= 0)
            throw new RecordingException($"録画ファイルが壊れている可能性があります（映像がありません）。途中のファイルは次のフォルダーに残しています: {dir}");

        var output = Path.GetFullPath(r.OutputPath);
        File.Move(temp, output, overwrite: true);
        var info = r.Info;
        info.SegmentCount = usable.Count;
        _logger.LogInformation("Recording done: {Output} ({Duration:0.000}s, {Count} segments)", output, meta.Duration, usable.Count);
        return new RecordingResult(output, TimeSpan.FromSeconds(meta.Duration), usable.Count, info);
    }

    private void TryDeleteWorkDir()
    {
        var dir = _workDir;
        if (dir is null || !Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Failed to delete {Dir}", dir); }
    }

    private void SetState(RecordingState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
