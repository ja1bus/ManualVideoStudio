using System.Diagnostics;
using System.Text;
using ManualVideoStudio.Core.FFmpeg;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Recording;

/// <summary>録画中の ffmpeg 1 本（一時停止で区切られた 1 区間）。テストでは偽物に差し替える。</summary>
public interface IRecordingProcess : IAsyncDisposable
{
    /// <summary>起動し、フレームの書き込みが始まるまで待つ。始まる前に終了したら RecordingException。</summary>
    Task StartAsync(CancellationToken ct);
    /// <summary>標準入力に "q" を送って正常終了させる。timeout 内に終わらなければ強制終了して false。</summary>
    Task<bool> StopAsync(TimeSpan timeout);
    bool HasExited { get; }
    /// <summary>stderr の末尾（エラー表示用）。</summary>
    string ErrorTail { get; }
    /// <summary>ffmpeg が終了した（停止操作以外で終わった場合の検知にも使う）。</summary>
    event EventHandler? Exited;
}

public interface IRecordingProcessFactory
{
    IRecordingProcess Create(string ffmpegPath, IReadOnlyList<string> arguments, string workingDirectory);
}

public sealed class FFmpegRecordingProcessFactory(ILoggerFactory? loggerFactory = null) : IRecordingProcessFactory
{
    public IRecordingProcess Create(string ffmpegPath, IReadOnlyList<string> arguments, string workingDirectory)
        => new FFmpegRecordingProcess(ffmpegPath, arguments, workingDirectory,
            loggerFactory?.CreateLogger<FFmpegRecordingProcess>() ?? NullLogger<FFmpegRecordingProcess>.Instance);
}

/// <summary>
/// 録画用の ffmpeg。通常の ProcessRunner と違い、停止用に標準入力を開いたままにする。
/// 子プロセスは Job Object に入れ、アプリが異常終了しても残らないようにする。
/// </summary>
public sealed class FFmpegRecordingProcess(string ffmpegPath, IReadOnlyList<string> arguments, string workingDirectory, ILogger logger)
    : IRecordingProcess
{
    public static TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(15);

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _stderr = new();
    private Process? _process;

    public bool HasExited => _process is null || _process.HasExited;

    public string ErrorTail
    {
        get
        {
            lock (_stderr)
            {
                var lines = _stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return string.Join(Environment.NewLine, lines.TakeLast(12));
            }
        }
    }

    public event EventHandler? Exited;

    public async Task StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        logger.LogInformation("Recording process start: {Command}", CommandLineFormatter.Format(ffmpegPath, arguments));

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            // -progress の frame=N（N > 0）で書き込み開始とみなす。
            if (e.Data is { } line && line.StartsWith("frame=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(6), out var frames) && frames > 0)
                _started.TrySetResult();
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr)
            {
                _stderr.Append(e.Data).Append('\n');
                if (_stderr.Length > 20_000) _stderr.Remove(0, _stderr.Length - 20_000);
            }
        };
        p.Exited += (_, _) =>
        {
            // stderr の読み取りが終わるのを待ってから、エラーの説明を作る。
            try { p.WaitForExit(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _started.TrySetException(new RecordingException(DescribeStartFailure(ErrorTail), ErrorTail));
            logger.LogInformation("Recording process exited: {Code}", SafeExitCode(p));
            Exited?.Invoke(this, EventArgs.Empty);
        };
        try
        {
            if (!p.Start()) throw new RecordingException($"ffmpeg を起動できません: {ffmpegPath}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new RecordingException($"ffmpeg を起動できません: {ffmpegPath}（{ex.Message}）", null, ex);
        }
        _process = p;
        ChildProcessTracker.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartTimeout);
        try
        {
            await _started.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
            if (ct.IsCancellationRequested) throw;
            throw new RecordingException("録画を開始できませんでした（ffmpeg が応答しません）。", ErrorTail);
        }
    }

    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        var p = _process;
        if (p is null || p.HasExited) return true;
        try
        {
            await p.StandardInput.WriteAsync("q").ConfigureAwait(false);
            await p.StandardInput.FlushAsync().ConfigureAwait(false);
            p.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            logger.LogWarning(ex, "Failed to send q to ffmpeg");
        }
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Recording process did not stop in {Timeout}; killing", timeout);
            Kill();
            return false;
        }
    }

    private void Kill()
    {
        try
        {
            if (_process is { HasExited: false } p)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Recording process kill failed");
        }
    }

    /// <summary>ffmpeg のエラーから、利用者向けの開始失敗の説明を作る。</summary>
    public static string DescribeStartFailure(string errorTail)
    {
        if (errorTail.Contains("Failed to capture image", StringComparison.Ordinal))
            return "録画を開始できませんでした（画面を取り込めません）。画面のロック中、リモートデスクトップの画面が最小化されている、"
                + "管理者権限の画面（UAC の確認など）が表示されている場合は録画できません。";
        if (errorTail.Contains("Could not find audio", StringComparison.Ordinal) || errorTail.Contains("dshow", StringComparison.OrdinalIgnoreCase))
            return "録画を開始できませんでした（マイクを開けません）。マイクが接続されているか、他のアプリが使用中でないか確認してください。";
        if (errorTail.Contains("outside", StringComparison.OrdinalIgnoreCase))
            return "録画を開始できませんでした（録画範囲が画面の外にあります）。範囲を画面の内側に移動してください。";
        return "録画を開始できませんでした。";
    }

    private static int? SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return null; }
    }

    public ValueTask DisposeAsync()
    {
        Kill();
        _process?.Dispose();
        return ValueTask.CompletedTask;
    }
}
