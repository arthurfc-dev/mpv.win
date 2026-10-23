using Microsoft.UI.Xaml.Controls;
using Player.App.Playback;

namespace Player.App;

public sealed partial class MainPage
{
    private readonly PlaybackSettingsStore _playbackSettings = new();
    private Task<string?> _settingsApplyTask = Task.FromResult<string?>(null);

    private async Task InitializePlaybackSettingsAsync()
    {
        _playbackSettings.UseRememberedAudio(await AudioStateStore.LoadAsync());
        try
        {
            foreach (var (option, value) in _playbackSettings.State.Options())
            {
                string effective = value;
                if (option == PlaybackOption.AudioDevice && value != "auto" && _session!.AudioDevices.Devices.Count > 0
                    && !_session.AudioDevices.Devices.Any(device => device.Name == value))
                { effective = "auto"; _playbackSettings.UseDefaultAudioDevice(); }
                await _session!.SetOptionAsync(option, effective);
            }
        }
        catch (Exception ex)
        {
            string warning = "Saved playback settings couldn't be applied. mpv.win tried the defaults. " + ex.Message;
            foreach (var (option, value) in new PlaybackSettings().Options())
                try { await _session!.SetOptionAsync(option, value); }
                catch (Exception fallback) { warning += $" {option}: {fallback.Message}"; }
            _playbackSettings.UseDefaults(warning);
        }
        HomeShell.ConfigureAudioDevices(() => _session?.AudioDevices ?? AudioDeviceCatalog.Empty);
        HomeShell.ConfigurePlaybackSettings(_playbackSettings.State, ApplyPlaybackSettingsAsync, _playbackSettings.Warning);
        _window?.UpdateWindowSettings(_playbackSettings.State.Window);
        RefreshAppearance();
        UpdateSeekLabels();
    }

    private Task<string?> ApplyPlaybackSettingsAsync(PlaybackSettings settings)
    {
        if (!_settingsApplyTask.IsCompleted) return Task.FromResult<string?>("Playback settings are already being applied.");
        return _settingsApplyTask = ApplyPlaybackSettingsCoreAsync(settings);
    }

    private async Task<string?> ApplyPlaybackSettingsCoreAsync(PlaybackSettings settings)
    {
        if (settings.Validate() is { } invalid) return invalid;
        if (_session is null || !_engineReady || _closing) return "The playback engine is not available.";
        var previous = _playbackSettings.State.Options().ToDictionary(item => item.Option, item => item.Value);
        var changed = new List<PlaybackOption>();
        try
        {
            foreach (var (option, value) in settings.Options())
            {
                if (previous[option] == value) continue;
                changed.Add(option);
                await _session.SetOptionAsync(option, value);
            }
            await _playbackSettings.SaveAsync(settings);
            _window?.UpdateWindowSettings(settings.Window);
            RefreshAppearance();
            _lastSubtitleClearance = double.NaN;
            UpdateSeekLabels();
            UpdateSubtitleClearance();
            return null;
        }
        catch (Exception ex)
        {
            bool restored = ex is not TimeoutException;
            foreach (var option in changed.AsEnumerable().Reverse())
                try { await _session.SetOptionAsync(option, previous[option]); }
                catch { restored = false; }
            _lastSubtitleClearance = double.NaN;
            return "Couldn't apply and save playback settings. " + ex.Message
                + (restored ? " Previous settings are retained." : " Some engine values could not be restored. Retry Apply or restart mpv.win.");
        }
    }

    private void UpdateSeekLabels()
    {
        string backward = $"Skip backward {_playbackSettings.State.BackwardSeconds:0.##} seconds";
        string forward = $"Skip forward {_playbackSettings.State.ForwardSeconds:0.##} seconds";
        ToolTipService.SetToolTip(SeekBackButton, backward + " (Left)");
        ToolTipService.SetToolTip(SeekForwardButton, forward + " (Right)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SeekBackButton, backward);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SeekForwardButton, forward);
    }

    internal void SeekStep(int direction) => SeekRelative(direction < 0
        ? -_playbackSettings.State.BackwardSeconds : _playbackSettings.State.ForwardSeconds);
}
