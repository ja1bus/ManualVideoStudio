using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Imaging;

/// <summary>
/// IAnnotatedImageRenderer の WPF 実装。画像の書き出し（Core の ImageExportService）はスレッドを選ばないため、
/// 描画は専用の STA スレッドで行う（WPF の描画オブジェクトは STA が必要）。
/// </summary>
public sealed class AnnotatedImageRenderer : IAnnotatedImageRenderer
{
    public Task RenderToFileAsync(string baseImagePath, IReadOnlyList<Annotation> annotations, int annotationWidth, int annotationHeight,
        string outputPath, ImageFormat format, int jpegQuality, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var items = annotations.Select(a => a.Clone()).ToList();
        return StaThread.RunAsync(() =>
        {
            var image = AnnotationRenderer.Render(ImageFiles.Load(baseImagePath), items, annotationWidth, annotationHeight);
            ImageFiles.SaveAtomic(image, outputPath, format, AnnotatedImageFiles.JpegQualityLevel(jpegQuality));
        });
    }
}

/// <summary>処理を新しい STA スレッドで実行する。</summary>
public static class StaThread
{
    public static Task RunAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                // WPF がこのスレッドに作った Dispatcher を片付ける。
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        { IsBackground = true, Name = "AnnotationRender" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static Task<T> RunAsync<T>(Func<T> func)
    {
        T result = default!;
        return RunAsync(() => { result = func(); }).ContinueWith(t =>
        {
            t.GetAwaiter().GetResult();
            return result;
        }, TaskScheduler.Default);
    }
}
