using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace Player.App;

public sealed partial class MainPage
{
    private DispatcherQueueTimer? _volumeFeedbackTimer;
    private bool _automaticVolumeFeedback;
    private bool _volumeFeedbackRequested, _volumeFeedbackQueued;
    private readonly string? _volumeDiagnosticPath = Environment.GetEnvironmentVariable("PLAYER_VOLUME_DIAGNOSTICS_PATH");

    internal void TraceVolumeInput(string message)
    {
        if (string.IsNullOrWhiteSpace(_volumeDiagnosticPath)) return;
        try { File.AppendAllText(_volumeDiagnosticPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal bool IsVolumePanelInput(DependencyObject? source)
    {
        for (var element = source; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, VolumeFlyoutContent)) return true;
        return false;
    }

    private void InitializeVolumeFeedback()
    {
        _volumeFeedbackTimer = DispatcherQueue.CreateTimer();
        _volumeFeedbackTimer.Interval = TimeSpan.FromSeconds(2);
        _volumeFeedbackTimer.IsRepeating = false;
        _volumeFeedbackTimer.Tick += OnVolumeFeedbackExpired;
        VolumeButton.Loaded += (_, _) => QueuePendingVolumeFeedback();
        VolumeFlyoutContent.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnVolumePanelPreviewKeyDown), true);
    }

    private void OnVolumePanelPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        TraceVolumeInput($"panel-preview key={e.Key} handled={e.Handled} source={e.OriginalSource?.GetType().Name}");
        foreach (var modifier in new[] { VirtualKey.Menu, VirtualKey.Control, VirtualKey.LeftWindows, VirtualKey.RightWindows })
            if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(modifier)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) return;
        AdjustVolume(e.Key == VirtualKey.Up ? 5 : -5);
        e.Handled = true;
    }

    private void ShowVolumeFeedback()
    {
        if (_volumeFeedbackTimer is null || _closing) return;
        _volumeFeedbackRequested = true;
        QueuePendingVolumeFeedback();
    }

    private void QueuePendingVolumeFeedback()
    {
        if (!_volumeFeedbackRequested || _volumeFeedbackQueued || _closing) return;
        _volumeFeedbackQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Run(() =>
        {
            _volumeFeedbackQueued = false;
            if (!_volumeFeedbackRequested || _closing || !_playbackVisible) { _volumeFeedbackRequested = false; return; }
            // RevealChrome may just have uncollapsed the controls. Defer until
            // layout supplies the anchor; Loaded/transition completion retry.
            if (!VolumeButton.IsLoaded || VolumeButton.ActualWidth <= 0 || VolumeButton.ActualHeight <= 0 ||
                PlaybackControls.Opacity <= 0)
            {
                TraceVolumeInput($"waiting-anchor loaded={VolumeButton.IsLoaded} width={VolumeButton.ActualWidth} opacity={PlaybackControls.Opacity}");
                return;
            }
            _volumeFeedbackRequested = false;
            OpenVolumeFeedback();
        }));
    }

    private void OpenVolumeFeedback()
    {
        if (_volumeFeedbackTimer is null) return;
        if (!VolumeFlyout.IsOpen)
        {
            // Never replace an audio/subtitle/options menu with feedback.
            if (_openFlyouts != 0) { TraceVolumeInput($"suppressed other-flyouts={_openFlyouts}"); return; }
            _automaticVolumeFeedback = true;
            TraceVolumeInput("show-at");
            VolumeFlyout.ShowAt(VolumeButton, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient });
        }
        if (!VolumeFlyout.IsOpen) { _volumeFeedbackRequested = true; return; }
        RestartVolumeFeedbackTimer();
    }

    private void OnVolumeFlyoutOpened(object sender, object args)
    {
        TraceVolumeInput("opened");
        _volumeFeedbackRequested = false;
        OnFlyoutOpened(sender, args);
        RestartVolumeFeedbackTimer();
    }

    private void RestartVolumeFeedbackTimer()
    {
        if (!_automaticVolumeFeedback || _volumeFeedbackTimer is null) return;
        _volumeFeedbackTimer.Stop();
        _volumeFeedbackTimer.Start();
    }

    private void OnVolumeFeedbackExpired(DispatcherQueueTimer sender, object args)
    {
        if (_automaticVolumeFeedback) VolumeFlyout.Hide();
    }

    private void KeepVolumeFlyoutOpen()
    {
        // Once the user interacts with the panel, it behaves like the normal
        // manually opened flyout and is no longer dismissed by our timer.
        _automaticVolumeFeedback = false;
        _volumeFeedbackRequested = false;
        _volumeFeedbackTimer?.Stop();
    }

    private void OnVolumeFeedbackInteraction(object sender, PointerRoutedEventArgs e) => KeepVolumeFlyoutOpen();
    private void OnVolumeFeedbackFocus(object sender, RoutedEventArgs e) => KeepVolumeFlyoutOpen();

    private void OnVolumeFlyoutClosed(object sender, object args)
    {
        TraceVolumeInput("closed");
        KeepVolumeFlyoutOpen();
        OnFlyoutClosed(sender, args);
    }

    private void StopVolumeFeedback()
    {
        KeepVolumeFlyoutOpen();
        if (_volumeFeedbackTimer is not null) _volumeFeedbackTimer.Tick -= OnVolumeFeedbackExpired;
        VolumeFlyoutContent.RemoveHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnVolumePanelPreviewKeyDown));
        VolumeFlyout.Hide();
    }
}
