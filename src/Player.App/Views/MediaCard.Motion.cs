using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Player.App.Views;

public sealed partial class MediaCard
{
    private static WeakReference<MediaCard>? _motionOwner;
    private CancellationTokenSource? _motionLifetime;
    private MediaPlayer? _motionPlayer;
    private MediaPlayerElement? _motionElement;
    internal static void StopHoverPreviews()
    {
        if (_motionOwner?.TryGetTarget(out var owner) == true) owner.StopMotion();
    }
    private async void BeginMotion()
    {
        if (IsFolder || string.IsNullOrEmpty(FilePath) || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        StopHoverPreviews();
        _motionOwner = new(this);
        var lifetime = _motionLifetime = new();
        var token = lifetime.Token;
        string path = FilePath;
        try
        {
            // A short dwell avoids spinning up a decoder when merely passing
            // over a card. One muted player is shared by ownership, never one
            // decoder per gallery item or an encoded preview cache.
            await Task.Delay(450, token);
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            token.ThrowIfCancellationRequested();
            if (!_pointerInside || path != FilePath || !IsLoaded) return;
            var player = _motionPlayer = new MediaPlayer { IsMuted = true, AutoPlay = false };
            player.CommandManager.IsEnabled = false;
            _motionElement = new MediaPlayerElement { AreTransportControlsEnabled = false, Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            _motionElement.SetMediaPlayer(player);
            MotionHost.Children.Add(_motionElement);
            UpdateMotionClip();
            player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || _motionPlayer != player) return;
                var duration = player.PlaybackSession.NaturalDuration;
                // Play up to ten seconds from a point near the beginning.
                player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Min(3, duration.TotalSeconds / 10));
                MotionHost.Visibility = Visibility.Visible;
                player.Play();
                _ = FinishMotionAsync(player, token);
            });
            player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_motionPlayer == player) StopMotion(); });
            player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_motionPlayer == player) StopMotion(); });
            player.Source = MediaSource.CreateFromStorageFile(file);
            // Bound initialization as well as playback; unsupported Windows
            // codecs retain their static card and remain playable via libmpv.
            await Task.Delay(TimeSpan.FromSeconds(15), token);
            if (_motionPlayer == player) StopMotion();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        { if (ReferenceEquals(_motionLifetime, lifetime)) StopMotion(); }
    }
    private async Task FinishMotionAsync(MediaPlayer player, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            if (_motionPlayer == player) StopMotion();
        }
        catch (OperationCanceledException) { }
    }
    private void StopMotion()
    {
        if (_motionOwner?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, this)) _motionOwner = null;
        _motionLifetime?.Cancel(); _motionLifetime?.Dispose(); _motionLifetime = null;
        MotionHost.Visibility = Visibility.Collapsed;
        _motionElement?.SetMediaPlayer(null);
        MotionHost.Children.Clear(); _motionElement = null;
        if (_motionPlayer is { } player)
        {
            _motionPlayer = null;
            player.Pause(); player.Source = null; player.Dispose();
        }
    }
    private void UpdateMotionClip()
    {
        if (_motionElement is null) return;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(MotionHost);
        var rounded = visual.Compositor.CreateRoundedRectangleGeometry();
        rounded.Size = new System.Numerics.Vector2((float)ThumbnailArea.ActualWidth, (float)ThumbnailArea.ActualHeight);
        rounded.CornerRadius = new System.Numerics.Vector2(4, 4);
        visual.Clip = visual.Compositor.CreateGeometricClip(rounded);
    }
}
