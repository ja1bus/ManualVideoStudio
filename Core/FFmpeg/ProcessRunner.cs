using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.FFmpeg;

public sealed class ProcessRequest
{
    public ProcessRequest(string fileName, IReadOnlyList<string> arguments)
    {
        FileName = fileName;
        Arguments = arguments;
    }

    public string FileName { get; }
    /// <summary>引数は 1 要素 1 引数。シェル文字列連結はしない（ProcessStartInfo.ArgumentList に渡す）。</summary>
    public IReadOnlyList<string> Arguments { get; }
    public string? WorkingDirectory { get; init; }
    public Action<string>? OnStandardOutputLine { get; init; }
    public Action<string>? OnStandardErrorLine { get; init; }
    public TimeSpan? Timeout { get; init; }
    /// <summary>保持する stdout/stderr の最大文字数（末尾を残す）。</summary>
    public int MaxCapturedChars { get; init; } = 2_000_000;
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>エラー表示用に stderr の末尾数行を返す。</summary>
    public string ErrorTail(int lines = 12)
    {
        var all = StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(Environment.NewLine, all.TakeLast(lines));
    }
}

public interface IProcessRunner
{
    /// <summary>
    /// プロセスを非同期に実行する。キャンセル時はプロセスツリーを終了させ OperationCanceledException を投げる。
    /// タイムアウト時は TimeoutException。
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 外部プロセス実行。stdout / stderr は非同期イベントで同時に読み出すためパイプ詰まりによるデッドロックが起きない。
/// 子プロセスは Job Object に所属させ、アプリが異常終了しても ffmpeg.exe が残らないようにする。
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner>? logger = null)
        => _logger = logger ?? NullLogger<ProcessRunner>.Instance;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (request.WorkingDirectory is not null) psi.WorkingDirectory = request.WorkingDirectory;
        foreach (var a in request.Arguments) psi.ArgumentList.Add(a);

        var display = CommandLineFormatter.Format(request.FileName, request.Arguments);
        _logger.LogInformation("Process start: {Command}", display);

        var stdout = new BoundedTextBuffer(request.MaxCapturedChars);
        var stderr = new BoundedTextBuffer(request.MaxCapturedChars);
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { stdoutDone.TrySetResult(); return; }
            stdout.AppendLine(e.Data);
            SafeInvoke(request.OnStandardOutputLine, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { stderrDone.TrySetResult(); return; }
            stderr.AppendLine(e.Data);
            SafeInvoke(request.OnStandardErrorLine, e.Data);
        };

        var sw = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"プロセスを開始できません: {request.FileName}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogError(ex, "Process start failed: {File}", request.FileName);
            throw new InvalidOperationException($"プロセスを開始できません: {request.FileName}（{ex.Message}）", ex);
        }
        ChildProcessTracker.Add(process);
        try { process.StandardInput.Close(); } catch (IOException) { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = request.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Process timeout after {Elapsed}: {Command}", sw.Elapsed, display);
                throw new TimeoutException($"処理が時間内に終わりませんでした（{request.Timeout}）。");
            }
            _logger.LogInformation("Process canceled: {Command}", display);
            throw new OperationCanceledException("処理をキャンセルしました。", cancellationToken);
        }
        catch (TimeoutException)
        {
            // 出力の読み終わり待ちがタイムアウトした場合（孫プロセスがパイプを握っている等）。結果はそのまま返す。
        }

        sw.Stop();
        var result = new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), sw.Elapsed);
        if (result.Succeeded)
            _logger.LogInformation("Process exit 0 ({Elapsed:0.0}s)", sw.Elapsed.TotalSeconds);
        else
            _logger.LogWarning("Process exit {Code} ({Elapsed:0.0}s): {Command}{NewLine}{Tail}",
                result.ExitCode, sw.Elapsed.TotalSeconds, display, Environment.NewLine, result.ErrorTail(30));
        return result;
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Process kill failed");
        }
    }

    private void SafeInvoke(Action<string>? callback, string line)
    {
        if (callback is null) return;
        try { callback(line); }
        catch (Exception ex) { _logger.LogWarning(ex, "Output callback failed"); }
    }

    /// <summary>末尾を優先して保持するテキストバッファ（巨大な stderr でメモリを使い切らないため）。</summary>
    private sealed class BoundedTextBuffer(int maxChars)
    {
        private readonly StringBuilder _sb = new();
        private readonly object _lock = new();

        public void AppendLine(string line)
        {
            lock (_lock)
            {
                _sb.Append(line).Append('\n');
                if (_sb.Length > maxChars) _sb.Remove(0, _sb.Length - maxChars);
            }
        }

        public override string ToString()
        {
            lock (_lock) return _sb.ToString();
        }
    }
}

/// <summary>ログ表示用のコマンドライン整形（実行には使わない）。</summary>
public static class CommandLineFormatter
{
    public static string Format(string fileName, IEnumerable<string> arguments)
        => string.Join(' ', new[] { fileName }.Concat(arguments).Select(Quote));

    private static string Quote(string a)
        => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or ';' or '&' or '|')
            ? "\"" + a.Replace("\"", "\\\"") + "\""
            : a;
}
