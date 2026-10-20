using System.IO;
using System.Windows;
using System.Windows.Threading;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Logging;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.Services;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio;

/// <summary>起動・DI 構成・未処理例外の扱い。</summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    // Store 版は、振り替えられる前の実際の保存場所を使う（外部のブラウザー・エクスプローラー・FFmpeg と同じパスで扱うため）。
    private readonly AppPaths _paths = PackageIdentity.CreatePaths();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        AppPaths.Current = _paths;
        var migrated = PackageIdentity.MigrateLegacySettings(_paths);
        var tempError = AppTemp.Configure(_paths.TempDirectory);

        ProjectService.AppVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        _services = ConfigureServices();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Start ManualVideoStudio {Version}", ProjectService.AppVersion);
        _logger.LogInformation("Storage: {Kind}, settings={Settings}, local={Local}, temp={Temp}",
            _paths.IsPackaged ? "Store package " + _paths.PackageFamilyName : "standard", _paths.SettingsDirectory, _paths.LocalDirectory, AppTemp.Root);
        if (migrated.Count > 0) _logger.LogInformation("Migrated settings into the package storage: {Files}", string.Join(", ", migrated));
        if (tempError is not null) _logger.LogWarning("Temp folder unavailable, using the OS temp folder: {Error}", tempError);

        _services.GetRequiredService<ISettingsService>().Load();
        // 字幕のフォント一覧（選択肢と幅の測定に使う）は数秒かかるので裏で読み始める。
        _ = SubtitleFonts.LoadAsync();
        // 古いサムネイル・元画像のキャッシュを裏で整理する（30 日超と、合計 500 MB 超の分。失敗しても起動は止めない）。
        var cache = _paths.CacheDirectory;
        var cleanupLogger = _logger;
        _ = Task.Run(() =>
        {
            try { AppTemp.CleanupOld(TimeSpan.FromDays(1)); }
            catch (Exception ex) { cleanupLogger.LogWarning(ex, "Temp cleanup failed"); }
            try { FrameCacheCleaner.Cleanup(cache, FrameCacheCleaner.DefaultMaxAge, FrameCacheCleaner.DefaultMaxBytes, logger: cleanupLogger); }
            catch (Exception ex) { cleanupLogger.LogWarning(ex, "Cache cleanup failed"); }
        });
        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        _ = window.InitializeAsync(e.Args.FirstOrDefault());
    }

    private ServiceProvider ConfigureServices()
    {
        var sc = new ServiceCollection();
        var fileLogger = new FileLoggerProvider(_paths.LogDirectory);
        sc.AddLogging(b => b.ClearProviders().AddProvider(fileLogger).SetMinimumLevel(LogLevel.Information));
        sc.AddSingleton(_paths);
        sc.AddSingleton(new LogLocation(_paths.LogDirectory));

        // Core
        sc.AddSingleton<ISettingsService>(sp => new SettingsService(_paths.SettingsFile, sp.GetRequiredService<ILogger<SettingsService>>()));
        sc.AddSingleton<IPromptTemplateService>(sp => new PromptTemplateService(_paths.PromptTemplatesFile, sp.GetRequiredService<ILogger<PromptTemplateService>>()));
        sc.AddSingleton<IProcessRunner, ProcessRunner>();
        sc.AddSingleton<FFmpegLocator>();
        sc.AddSingleton<IFFmpegInfoService, FFmpegInfoService>();
        sc.AddSingleton<IMediaMetadataService, MediaMetadataService>();
        sc.AddSingleton<IFrameExtractionService>(sp => new FrameExtractionService(sp.GetRequiredService<IProcessRunner>(),
            _paths.CacheDirectory, sp.GetRequiredService<ILogger<FrameExtractionService>>()));
        sc.AddSingleton<IVideoExportService, VideoExportService>();
        sc.AddSingleton<ManualVideoStudio.Core.Join.IRecordingJoinService, ManualVideoStudio.Core.Join.RecordingJoinService>();
        sc.AddSingleton(new CaptionRequestStore(_paths.CaptionRequestDirectory));
        sc.AddSingleton<CaptionRequestService>();
        sc.AddSingleton<SubtitleSampleRenderer>();
        sc.AddSingleton<IGifExportService, GifExportService>();
        sc.AddSingleton<IRecordingProcessFactory>(sp => new FFmpegRecordingProcessFactory(sp.GetRequiredService<ILoggerFactory>()));
        // 録画は状態を持つため、録画画面を開くたびに作る。
        sc.AddTransient<IRecordingService, RecordingService>();
        sc.AddSingleton<IAnnotatedImageRenderer, AnnotatedImageRenderer>();
        sc.AddSingleton<ImageExportService>();
        sc.AddSingleton<ILocalAnalysisService, LocalAnalysisService>();
        sc.AddSingleton<IProjectService, ProjectService>();
        sc.AddSingleton<IRecycleBin, RecycleBin>();

        // UI
        sc.AddSingleton<IDialogService, DialogService>();
        sc.AddSingleton<IClipboardService, ClipboardService>();
        sc.AddSingleton<IShellService, ShellService>();
        sc.AddSingleton<IWindowService, WindowService>();
        sc.AddSingleton<FFmpegStatus>();
        sc.AddSingleton<SubtitleCheckService>();
        sc.AddSingleton<ICaptureTargetService, CaptureTargetService>();
        sc.AddSingleton<IAudioInputDeviceService, AudioInputDeviceService>();
        sc.AddTransient<RecorderViewModel>();
        sc.AddSingleton<MainViewModel>();
        sc.AddTransient<SettingsViewModel>();
        sc.AddTransient<AiAssistViewModel>();
        sc.AddTransient<ProjectListViewModel>();
        sc.AddSingleton<MainWindow>();
        return sc.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Exit");
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        WriteCrashLog(e.Exception);
        e.Handled = true;
        // 画面表示前の例外でプロセスだけが残らないよう、主画面が未表示なら終了する（開発メモ参照）。
        if (MainWindow is null || !MainWindow.IsLoaded)
        {
            MessageBox.Show($"起動できませんでした。{Environment.NewLine}{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}{_paths.CrashLogFile}",
                "Manual Video Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        MessageBox.Show(MainWindow, $"予期しないエラーが発生しました。{Environment.NewLine}{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}詳細はログを参照してください: {_paths.LogDirectory}",
            "Manual Video Studio", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void WriteCrashLog(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(_paths.LocalDirectory);
            File.AppendAllText(_paths.CrashLogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
