using Player.App.Playback;

namespace Player.App.Views;

public sealed partial class HomeView
{
    private PlaybackSettingsView? _settingsView;
    private PlaybackSettings _settingsSnapshot = new();
    private Func<PlaybackSettings, Task<string?>>? _applyPlaybackSettings;
    private string? _settingsWarning;
    private Func<AudioDeviceCatalog>? _audioDevicesProvider;
    internal void ConfigureAudioDevices(Func<AudioDeviceCatalog> provider)
    {
        _audioDevicesProvider = provider;
        if (_settingsView is not null) _settingsView.AudioDevicesProvider = provider;
    }

    internal void ConfigurePlaybackSettings(PlaybackSettings settings, Func<PlaybackSettings, Task<string?>> apply, string? warning)
    {
        _settingsSnapshot = settings;
        _settingsWarning = warning;
        _applyPlaybackSettings = async candidate =>
        {
            string? error = await apply(candidate);
            if (error is null) { _settingsSnapshot = candidate; _settingsWarning = null; }
            return error;
        };
        _settingsView?.Configure(_settingsSnapshot, _applyPlaybackSettings, _settingsWarning);
    }

    private void EnsureConfiguration()
    {
        if (_settingsView is not null) return;
        // Keep the native input controls out of normal startup and video-open
        // layout work. They are created only when Configuration is selected.
        _settingsView = new();
        _settingsView.AudioDevicesProvider = _audioDevicesProvider;
        _settingsView.BackToLibraryRequested += () =>
        {
            if (CanGoBack) GoBack();
            else Navigation.SelectedItem = HomeItem;
            FocusCurrentLocation();
        };
        PlaybackSettingsHost.Content = _settingsView;
        if (_applyPlaybackSettings is not null)
            _settingsView.Configure(_settingsSnapshot, _applyPlaybackSettings, _settingsWarning);
    }

    private void ShowConfiguration()
    {
        _searchTimer?.Stop();
        RememberLocation(new("settings", "Configuration", "settings"));
        EnsureConfiguration();
        Navigation.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        PlaybackSettingsHost.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        VideoLibrary.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        _catalog.Cancel();
        VideoLibrary.SetActive(false);
    }
}
