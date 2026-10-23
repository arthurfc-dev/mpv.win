using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Player.App.Playback;

namespace Player.App;

public sealed partial class MainPage
{
    private void AddAudioOutputMenu(MenuFlyout menu)
    {
        menu.Items.Add(new MenuFlyoutSeparator());
        var outputs = new MenuFlyoutSubItem { Text = "Audio output device", Icon = new FontIcon { Glyph = "\uE7F5" } };
        var catalog = _session?.AudioDevices ?? AudioDeviceCatalog.Empty;
        var devices = new[] { new AudioOutputDevice("auto", "Windows default") }
            .Concat(catalog.Devices.Where(device => device.Name != "auto"));
        foreach (var device in devices)
        {
            var choice = new RadioMenuFlyoutItem { Text = device.Description, Tag = device.Name, GroupName = "AudioOutputs",
                IsChecked = device.Name == catalog.Selected, MaxWidth = 460 };
            choice.Click += OnAudioOutputSelected;
            outputs.Items.Add(choice);
        }
        menu.Items.Add(outputs);
    }

    private async void OnAudioOutputSelected(object sender, RoutedEventArgs args)
    {
        if (_session is null || _closing || sender is not RadioMenuFlyoutItem { Tag: string name }) return;
        try { await _session.SetOptionAsync(PlaybackOption.AudioDevice, name); }
        catch (Exception ex) { if (!_closing) ShowError("Couldn't select this audio output. " + ex.Message); }
    }

    private bool _muteChangePending;
    private async void OnToggleMute(object sender, RoutedEventArgs args)
    {
        if (_muteChangePending || _session is null || !_engineReady || _closing) return;
        _muteChangePending = true;
        bool muted = !_session.ReadSnapshot().Muted;
        RevealChrome();
        try
        {
            await _session.SetOptionAsync(PlaybackOption.Mute, muted ? "yes" : "no");
            if (!_closing) UpdateMutePresentation(muted, VolumeBar.Value);
        }
        catch (Exception ex) { if (!_closing) ShowError("Couldn't change mute. " + ex.Message); }
        finally { _muteChangePending = false; }
    }

    private bool? _shownMuteState;
    private bool _shownZeroVolume;
    private void UpdateMutePresentation(bool muted, double volume)
    {
        bool zeroVolume = volume <= 0;
        if (_shownMuteState == muted && _shownZeroVolume == zeroVolume) return;
        _shownMuteState = muted; _shownZeroVolume = zeroVolume;
        VolumeIcon.Glyph = VolumeFlyoutIcon.Glyph = muted || volume <= 0 ? "\uE74F" : "\uE767";
        string action = muted ? "Unmute" : "Mute";
        AutomationProperties.SetName(VolumeFlyoutMuteButton, action);
        ToolTipService.SetToolTip(VolumeFlyoutMuteButton, action);
    }

    private async Task SaveRememberedAudioAsync()
    {
        if (_session is null || !_engineReady) return;
        var state = _session.ReadSnapshot();
        var preferences = _playbackSettings.State;
        try
        {
            await AudioStateStore.SaveAsync(new(preferences.RememberVolume ? Math.Clamp(state.Volume, 0, 100) : null,
                preferences.RememberVolume ? state.Muted : null,
                preferences.RememberAudioDevice ? _session.AudioDevices.Selected : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { App.WriteStartupFailure(ex, "Couldn't save audio state on exit"); }
    }
}
