using Microsoft.UI.Xaml.Controls;
using Player.App.Playback;

namespace Player.App;

public sealed partial class MainPage
{
    private uint? _finishedGeneration;
    internal void RefreshWindowTitle()
    {
        bool home = _homeRequested || _snapshot.State == PlaybackState.Empty;
        _window?.UpdateWindowTitle(home ? "" : _session?.WindowInfo.Title ?? "", home ? null : _openedPath);
    }

    private void UpdateAutomaticPlayback(PlaybackSnapshot state, bool wasLoading)
    {
        if (_session is null || _closing || state.MediaGeneration == 0) return;
        if ((_openedGeneration != state.MediaGeneration || state.State == PlaybackState.Loading || wasLoading)
            && _session.MediaPath(state.MediaGeneration) is { } path &&
            (_openedGeneration != state.MediaGeneration || !string.Equals(path, _openedPath, StringComparison.OrdinalIgnoreCase)))
        {
            _openedPath = path; _openedGeneration = state.MediaGeneration;
            MediaTitle.Text = Path.GetFileNameWithoutExtension(path);
            ToolTipService.SetToolTip(MediaTitle, Path.GetFileName(path));
            ++_seekRevision; _seeking = false;
        }
        if (state.PlaybackFinished == 0 || _finishedGeneration == state.MediaGeneration || _homeRequested) return;
        _finishedGeneration = state.MediaGeneration;
        if (_openedPath is not null && _historyGeneration != state.MediaGeneration)
        {
            _historyGeneration = state.MediaGeneration;
            HomeShell.RecordPlayed(_openedPath, state.Duration);
        }
        if (_playbackSettings.State.IdleMode == "yes")
        {
            _homeRequested = true;
            if (!_returnToBrowserOrigin) HomeShell.NavigateRecentHome();
            if (_window?.AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen) _window.ToggleFullscreen();
        }
        else DispatcherQueue.TryEnqueue(() =>
        {
            if (!_homeRequested && _snapshot.MediaGeneration == state.MediaGeneration && _snapshot.PlaybackFinished != 0)
                _window?.RequestClose();
        });
    }
}
