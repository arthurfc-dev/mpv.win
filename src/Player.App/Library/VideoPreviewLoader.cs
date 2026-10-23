using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Player.App.Library;

internal static class VideoPreviewLoader
{
    private static readonly SemaphoreSlim ExtractionSlot = new(1);
    internal static async Task<BitmapImage?> LoadAsync(StorageFile file, CancellationToken token, Action<TimeSpan>? durationAvailable = null)
    {
        // Shell handlers sometimes return a generic icon during extraction.
        // Retry once, then extract a frame natively for supported video codecs.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var shellTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            shellTimeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var thumbnail = await file.GetThumbnailAsync(attempt == 0 ? ThumbnailMode.VideosView : ThumbnailMode.SingleItem,
                    320, ThumbnailOptions.ResizeThumbnail).AsTask(shellTimeout.Token);
                token.ThrowIfCancellationRequested();
                if (thumbnail is { Type: ThumbnailType.Image, Size: > 0 }) return await DecodeAsync(thumbnail, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException) { }
            if (attempt == 0) await Task.Delay(200, token);
        }
        await ExtractionSlot.WaitAsync(token);
        try
        {
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(token);
            token.ThrowIfCancellationRequested();
            durationAvailable?.Invoke(clip.OriginalDuration);
            var composition = new MediaComposition();
            try
            {
                composition.Clips.Add(clip);
                using var frame = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(Math.Min(3, clip.OriginalDuration.TotalSeconds / 10)),
                    320, 0, VideoFramePrecision.NearestFrame).AsTask(token);
                return await DecodeAsync(frame, token);
            }
            finally { composition.Clips.Clear(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException)
        { token.ThrowIfCancellationRequested(); return null; }
        finally { ExtractionSlot.Release(); }
    }
    private static async Task<BitmapImage> DecodeAsync(IRandomAccessStream stream, CancellationToken token)
    {
        var image = new BitmapImage { DecodePixelWidth = 320 };
        await image.SetSourceAsync(stream).AsTask(token);
        token.ThrowIfCancellationRequested();
        return image;
    }
}
