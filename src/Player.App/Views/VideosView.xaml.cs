using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Player.App.Library;
using Player.App.Presentation;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Player.App.Views;

public sealed partial class VideosView : UserControl
{
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not GridViewItem container) return;
        if (args.InRecycleQueue)
        {
            if (container.ContentTemplateRoot is MediaCard recycled) recycled.SetPointerOver(false);
            ReleasePreview(container); return;
        }
        if (args.Phase > 0 && !ReferenceEquals(container.Content, args.Item)) return;
        if (args.Phase == 0) args.RegisterUpdateCallback(1, OnContainerContentChanging);
        container.GotFocus -= OnCardContainerGotFocus; container.GotFocus += OnCardContainerGotFocus;
        container.LostFocus -= OnCardContainerLostFocus; container.LostFocus += OnCardContainerLostFocus;
        container.PointerEntered -= OnCardContainerPointerEntered; container.PointerEntered += OnCardContainerPointerEntered;
        container.PointerExited -= OnCardContainerPointerExited; container.PointerExited += OnCardContainerPointerExited;
        _layout.Prepare(container);
        if (args.Item is VideoLibraryItem item) { ConfigureMenu(container, item); LoadPreview(container, item); }
    }
    private void OnCardContainerPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is GridViewItem { ContentTemplateRoot: MediaCard card }) card.SetPointerOver(true);
    }
    private void OnCardContainerPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not GridViewItem { ContentTemplateRoot: MediaCard card } container) return;
        // Ignore routed child exits while crossing the thumbnail, labels, or
        // buttons. The complete native card, including its padding, owns hover.
        if (container.IsLoaded)
        {
            var point = args.GetCurrentPoint(container).Position;
            if (point.X >= 0 && point.Y >= 0 && point.X < container.ActualWidth && point.Y < container.ActualHeight) return;
        }
        card.SetPointerOver(false);
    }
    private void OnCardContainerGotFocus(object sender, RoutedEventArgs args)
    {
        if (sender is GridViewItem { ContentTemplateRoot: MediaCard card }) card.UpdateFocusActions(true);
    }
    private void OnCardContainerLostFocus(object sender, RoutedEventArgs args)
    {
        if (sender is GridViewItem { ContentTemplateRoot: MediaCard card }) card.UpdateFocusActions(false);
    }

    private void LoadRealizedPreviews()
    {
        if (!_active) return;
        Visit(VideosGrid);
        void Visit(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is GridViewItem { Content: VideoLibraryItem item } container) LoadPreview(container, item);
                else Visit(child);
            }
        }
    }

    private void LoadPreview(FrameworkElement container, VideoLibraryItem item)
    {
        if (!_active || _activeLifetime is null || item.IsFolder) return;
        if (_previews.TryGetValue(container, out var current) && current.Item == item) return;
        ReleasePreview(container);
        var lease = new PreviewLease(item, CancellationTokenSource.CreateLinkedTokenSource(_activeLifetime.Token));
        _previews.Add(container, lease);
        if (_thumbnailCache.TryGetValue(item.Path, out var cached)) item.Thumbnail = cached;
        _ = LoadPreviewAsync(lease, lease.Cancellation.Token);
    }

    private async Task LoadPreviewAsync(PreviewLease lease, CancellationToken token)
    {
        lease.Item.BeginPreview();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await LoadPreviewAttemptAsync(lease, timeout.Token);
                token.ThrowIfCancellationRequested();
                if (lease.Item.Thumbnail is not null) return;
                if (attempt == 0) await Task.Delay(800, token);
            }
        }
        catch (OperationCanceledException) { }
        finally { lease.Item.EndPreview(); }
    }

    private async Task LoadPreviewAttemptAsync(PreviewLease lease, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await _previewSlots.WaitAsync(token);
            entered = true;
            var file = await StorageFile.GetFileFromPathAsync(lease.Item.Path).AsTask(token);
            if (!lease.Item.MetadataRead)
            {
                try
                {
                    var properties = await file.Properties.GetVideoPropertiesAsync().AsTask(token);
                    token.ThrowIfCancellationRequested();
                    lease.Item.SetDuration(properties.Duration);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
                {
                    token.ThrowIfCancellationRequested();
                    lease.Item.SetDuration(TimeSpan.Zero, false);
                }
            }
            if (lease.Item.Thumbnail is not null) return;
            var image = await VideoPreviewLoader.LoadAsync(file, token, duration =>
            {
                if (lease.Item.DurationText == "—") lease.Item.SetDuration(duration);
            });
            token.ThrowIfCancellationRequested();
            if (image is null) return;
            lease.Item.Thumbnail = image;
            if (!_thumbnailCache.ContainsKey(lease.Item.Path)) _thumbnailOrder.Enqueue(lease.Item.Path);
            _thumbnailCache[lease.Item.Path] = image;
            while (_thumbnailOrder.Count > 48) _thumbnailCache.Remove(_thumbnailOrder.Dequeue());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or NotSupportedException)
        {
            // Keep the file available to libmpv when Windows cannot preview it.
        }
        finally { if (entered) _previewSlots.Release(); }
    }

    private void ReleasePreview(FrameworkElement container)
    {
        if (!_previews.Remove(container, out var lease)) return;
        lease.Cancellation.Cancel();
        lease.Cancellation.Dispose();
        if (!_previews.Values.Any(other => other.Item == lease.Item)) lease.Item.Thumbnail = null;
    }

    private void ReleasePreviews()
    {
        foreach (var container in _previews.Keys.ToArray()) ReleasePreview(container);
    }
}

public sealed class VideoLibraryItem(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public bool IsFolder { get; init; }
    public string Title { get; init; } = System.IO.Path.GetFileNameWithoutExtension(path);
    public string FileName { get; } = System.IO.Path.GetFileName(path);
    internal DateTime? Created { get; set; }
    internal DateTime? Modified { get; set; }
    internal DateTime? Taken { get; set; }
    internal bool TakenRead { get; set; }
    public string DurationText { get; internal set; } = "—";
    public string AccessibleName => IsFolder ? $"{Title}, folder, {DurationText}" : $"{Title}, duration {DurationText}";
    internal bool MetadataRead { get; private set; }
    private ImageSource? _thumbnail;
    private int _pendingPreviews;
    public bool IsPreviewLoading => _thumbnail is null && _pendingPreviews > 0;
    internal void BeginPreview() { ++_pendingPreviews; PropertyChanged?.Invoke(this, new(nameof(IsPreviewLoading))); }
    internal void EndPreview() { --_pendingPreviews; PropertyChanged?.Invoke(this, new(nameof(IsPreviewLoading))); }
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        internal set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new(nameof(IsPreviewLoading)));
        }
    }
    internal void SetDuration(TimeSpan duration, bool complete = true)
    {
        MetadataRead = complete;
        DurationText = duration <= TimeSpan.Zero ? "—" : duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
        PropertyChanged?.Invoke(this, new(nameof(DurationText)));
        PropertyChanged?.Invoke(this, new(nameof(AccessibleName)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
