using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Player.App.Playback;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    internal Func<AudioDeviceCatalog>? AudioDevicesProvider { get; set; }
    private readonly NumberBox _startupVolume = Number(0, 100);
    private readonly ToggleSwitch _startupMute = new(), _rememberVolume = new(), _rememberDevice = new();
    private readonly TextBox _audioLanguages = new() { Width = 220, MaxLength = 127, IsSpellCheckEnabled = false, PlaceholderText = "pt-BR,en,ja" };
    private readonly TextBox _audioExtensions = new() { Width = 220, MaxLength = 127, IsSpellCheckEnabled = false };
    private readonly ComboBox _externalAudio = Choices(("no", "Off"), ("exact", "Exact filename"), ("fuzzy", "Filename contains video name"), ("all", "All audio files in folder"));
    private readonly ComboBox _audioDevice = new() { Width = 180 };

    private void InitializeAudioSettings()
    {
        AddRow(AudioFields, "Startup volume (%)", "Choose the volume when the app opens. 0 is silent and 100 is full volume. If Remember volume and mute is on, your saved volume is used instead.", _startupVolume);
        AddRow(AudioFields, "Start muted", "Start with sound turned off. A saved mute setting takes priority when Remember volume and mute is on.", _startupMute);
        AddRow(AudioFields, "Remember volume and mute", "Keep your volume and mute setting the next time you open the app.", _rememberVolume);
        AddRow(AudioFields, "Preferred audio languages", "Enter language codes in the order you prefer, separated by commas. For example, pt-BR,en,ja tries Brazilian Portuguese, then English, then Japanese when a video opens.", _audioLanguages);
        AddRow(AudioFields, "Automatically load external audio", "Look for separate audio files in the video's folder when you open it. Choose how closely their filenames must match, or leave this off.", _externalAudio, advanced: true);
        var deviceRow = new Grid { Width = 220, ColumnSpacing = 8 };
        deviceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        deviceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        deviceRow.Children.Add(_audioDevice);
        AutomationProperties.SetName(_audioDevice, "Audio output device");
        var refresh = new Button { Width = 32, Height = 32, Padding = new Thickness(0), Content = new FontIcon { Glyph = "\uE72C", FontSize = 14 } };
        AutomationProperties.SetName(refresh, "Refresh audio devices"); ToolTipService.SetToolTip(refresh, "Refresh audio devices");
        refresh.Click += (_, _) => RefreshAudioDevices(); Grid.SetColumn(refresh, 1); deviceRow.Children.Add(refresh);
        AddRow(AudioFields, "Audio output device", "Choose the speakers or headphones to use, or follow the Windows default. A remembered device can take priority the next time the app opens.", deviceRow);
        AddRow(AudioFields, "Remember audio device", "Use the speakers or headphones you last selected in the player when you open the app again.", _rememberDevice);
        AddRow(AudioFields, "Audio file extensions", "Choose which audio file types to look for beside a video or add to a folder playlist. Separate names with commas, such as mp3,flac,wav, without dots. This does not change Windows defaults.", _audioExtensions, advanced: true);
    }
    private void RefreshAudioDevices(string? desired = null)
    {
        desired ??= (_audioDevice.SelectedItem as ComboBoxItem)?.Tag as string ?? _current.AudioDevice;
        var catalog = AudioDevicesProvider?.Invoke() ?? AudioDeviceCatalog.Empty;
        _audioDevice.Items.Clear();
        _audioDevice.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = "auto" });
        foreach (var device in catalog.Devices.Where(device => device.Name != "auto"))
        {
            var item = new ComboBoxItem { Content = device.Description, Tag = device.Name };
            ToolTipService.SetToolTip(item, device.Description); _audioDevice.Items.Add(item);
        }
        if (!_audioDevice.Items.OfType<ComboBoxItem>().Any(item => item.Tag as string == desired))
            _audioDevice.Items.Add(new ComboBoxItem { Content = "Unavailable saved device", Tag = desired });
        Select(_audioDevice, desired);
    }
    private void PopulateExtendedAudio(PlaybackSettings s)
    {
        _startupVolume.Value = s.StartupVolume; _startupMute.IsOn = s.StartupMuted;
        _rememberVolume.IsOn = s.RememberVolume; _rememberDevice.IsOn = s.RememberAudioDevice;
        _audioLanguages.Text = s.AudioLanguages; _audioExtensions.Text = s.AudioExtensions;
        Select(_externalAudio, s.AudioFileAuto); RefreshAudioDevices(s.AudioDevice);
    }
    private PlaybackSettings ReadAudioSettings(PlaybackSettings s) => s with {
        StartupVolume = _startupVolume.Value, StartupMuted = _startupMute.IsOn, RememberVolume = _rememberVolume.IsOn,
        RememberAudioDevice = _rememberDevice.IsOn, AudioDevice = Value(_audioDevice), AudioFileAuto = Value(_externalAudio),
        AudioLanguages = string.Join(',', _audioLanguages.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        AudioExtensions = string.Join(',', _audioExtensions.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant() };
}
