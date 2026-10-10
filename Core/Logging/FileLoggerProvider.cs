using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.Core.Logging;

/// <summary>
/// 開発者向け詳細ログ（%LOCALAPPDATA%\ManualVideoStudio\logs\app-yyyyMMdd.log）。
/// 利用者向けの表示はメッセージボックス・ステータスバーで行い、例外全文や ffmpeg の stderr はこちらへ書く。
/// 外部ライブラリに依存しない最小実装。古いログは保持日数を過ぎたら削除する。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information, int retentionDays = 14)
    {
        _directory = directory;
        _minLevel = minLevel;
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var f in Directory.EnumerateFiles(directory, "app-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-retentionDays)) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public string CurrentLogFile => Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(this, ShortName(n)));

    public void Dispose() { }

    private static string ShortName(string category)
    {
        var i = category.LastIndexOf('.');
        return i >= 0 ? category[(i + 1)..] : category;
    }

    private void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
          .Append(level switch
          {
              LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
              LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "   ",
          })
          .Append(" [").Append(category).Append("] ").Append(message);
        if (ex is not null) sb.AppendLine().Append(ex);
        sb.AppendLine();
        lock (_lock)
        {
            try { File.AppendAllText(CurrentLogFile, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* ログ失敗で処理を止めない */ }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
