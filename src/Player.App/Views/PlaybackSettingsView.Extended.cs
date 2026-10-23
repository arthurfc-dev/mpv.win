using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Player.App.Playback;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    private readonly ComboBox _keepOpen = Choices(("yes", "Keep final file open"), ("no", "Release finished file"), ("always", "Keep every file open"));
    private readonly ToggleSwitch _keepPause = new(), _savePosition = new(), _autoTracks = new(), _systemColors = new();
    private readonly ComboBox _idleMode = Choices(("yes", "Stay open"), ("no", "Close after playback"), ("once", "Stay open until first playback ends"));
    private readonly ComboBox _hrSeek = Choices(("absolute", "Absolute seeks"), ("yes", "Always"), ("no", "Keyframes"), ("default", "Format default"));
    private readonly ComboBox _autoPlaylist = Choices(("no", "Selected file only"), ("filter", "Videos in folder"), ("same", "Same media type"));
    private readonly ComboBox _theme = Choices(("system", "Use Windows setting"), ("light", "Light"), ("dark", "Dark"));
    private readonly TextBox _repeatFile = new() { Width = 220, MaxLength = 7 }, _repeatPlaylist = new() { Width = 220, MaxLength = 7 };
    private readonly TextBox _resumeOptions = new() { Width = 220, MaxLength = 127, IsSpellCheckEnabled = false };
    private readonly ColorPicker _accentPicker = Picker(), _playPicker = Picker(), _seekPicker = Picker(), _volumePicker = Picker();

    private static ColorPicker Picker()
    {
        var picker = new ColorPicker { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false,
            IsMoreButtonVisible = false, IsColorSliderVisible = true, IsHexInputVisible = true,
            // Native spectrum minimum (256) + preview (44) + spacing (12).
            // A narrower picker clips the right corners off the strip/gradient.
            IsColorChannelTextInputVisible = true, MinWidth = 312, Width = 320, CornerRadius = new CornerRadius(6) };
        RoundedColorPicker.Attach(picker);
        return picker;
    }
    private static ComboBox Choices(params (string Value, string Label)[] choices)
    {
        var box = new ComboBox { Width = 220 };
        foreach (var choice in choices) box.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        return box;
    }
    private static void Select(ComboBox box, string value) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == value);
    private static string Value(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private void InitializeExtendedSettings()
    {
        _font.Loaded += (_, _) => { if (string.IsNullOrWhiteSpace(_font.Text)) _font.Text = _font.SelectedItem as string ?? _current.SubtitleFont; };
        AddRow(PlaybackFields, "Keep file open at the end", "Choose whether to keep the last picture on screen or release the finished video. With a folder playlist, you can also stop at the end of every file.", _keepOpen, advanced: true);
        AddRow(PlaybackFields, "Pause at the end", "Pause when a video finishes and stays open. Turn this off to resume playing automatically when you seek backward from the end.", _keepPause, advanced: true);
        AddRow(PlaybackFields, "When playback finishes", "Choose whether the app stays open or closes after the last video. This applies when Keep file open at the end is set to Release finished file.", _idleMode);
        AddRow(PlaybackFields, "Repeat current file", "Enter no to play once, inf to repeat forever, or a number for how many extra times to repeat. The player's Loop button can temporarily change this.", _repeatFile, advanced: true);
        AddRow(PlaybackFields, "Remember position on close", "Save where you stopped when closing the app or opening another video, so you can continue there next time.", _savePosition);
        AddRow(PlaybackFields, "Resume options", "Choose what to remember for each video, separated by commas. Use start for your place, speed for playback speed, volume for loudness, aid for the audio track and sid for subtitles. Other supported names are sub-delay, audio-delay, sub-pos and sub-visibility.", _resumeOptions, advanced: true);
        AddRow(PlaybackFields, "High-resolution seeking", "Choose when mpv should find the exact frame during a jump. Exact jumps may take longer. The seek bar and Precise skipping setting take priority.", _hrSeek, advanced: true);
        AddRow(PlaybackFields, "Select tracks automatically", "Let the player choose video, audio and subtitle tracks when you open a file. Turning this off may leave the video or sound disabled.", _autoTracks, advanced: true);
        AddRow(PlaybackFields, "Repeat folder playlist", "Enter no to play the playlist once, inf to repeat forever, or a number for the total times to play it. Use force to keep retrying files that fail. Requires a folder playlist.", _repeatPlaylist, advanced: true);
        AddRow(PlaybackFields, "Create playlist from folder", "Choose whether opening a video also adds other media from the same folder to a playlist. Subfolders are not included.", _autoPlaylist);
        AddRow(AppearanceFields, "App theme", "Choose a light or dark appearance, or let the app follow Windows. Controls over the video stay dark for readability.", _theme);
        AddRow(AppearanceFields, "Use Windows accent colors", "Use the highlight color chosen in Windows. Choosing a custom color below turns this off. Windows high contrast always takes priority.", _systemColors);
        AddRow(AppearanceFields, "Navigation highlight", "Choose the color of the marker beside the selected sidebar item.", ColorButton(_accentPicker, "Navigation highlight color", UseCustomColors));
        AddRow(AppearanceFields, "Play button", "Choose the background color of the main play and pause button.", ColorButton(_playPicker, "Play button color", UseCustomColors));
        AddRow(AppearanceFields, "Seek bar", "Choose the color of the progress line and the handle you drag through a video.", ColorButton(_seekPicker, "Seek bar color", UseCustomColors));
        AddRow(AppearanceFields, "Volume bar", "Choose the color of the volume line and its handle.", ColorButton(_volumePicker, "Volume bar color", UseCustomColors));
        AddReset(AppearanceFields, () => PopulateAppearance(new()));
    }
    private void UseCustomColors() => _systemColors.IsOn = false;
    private Button ColorButton(ColorPicker picker, string name, Action? beginEdit = null)
    {
        WatchDraftInput(picker);
        AutomationProperties.SetName(picker, name);
        var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(picker.Color) };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        void Update() { ((SolidColorBrush)swatch.Background).Color = picker.Color; label.Text = Hex(picker); }
        picker.ColorChanged += (_, _) => Update(); Update();
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 }; content.Children.Add(swatch); content.Children.Add(label);
        var flyout = new Flyout { Content = picker };
        flyout.Opened += (_, _) => beginEdit?.Invoke();
        var button = new Button { Content = content, Width = 220, CornerRadius = new CornerRadius(4), Flyout = flyout };
        AutomationProperties.SetName(button, name); return button;
    }
    private void PopulateExtendedPlayback(PlaybackSettings s)
    {
        Select(_keepOpen, s.KeepOpen); _keepPause.IsOn = s.KeepOpenPause; Select(_idleMode, s.IdleMode);
        _repeatFile.Text = s.LoopFile; _savePosition.IsOn = s.SavePosition; _resumeOptions.Text = s.WatchLaterOptions;
        Select(_hrSeek, s.HighResolutionSeek); _autoTracks.IsOn = s.TrackAutoSelection;
        _repeatPlaylist.Text = s.LoopPlaylist; Select(_autoPlaylist, s.AutoCreatePlaylist);
    }
    private void PopulateAppearance(PlaybackSettings s)
    {
        Select(_theme, s.AppTheme); _systemColors.IsOn = s.UseSystemColors;
        _accentPicker.Color = ParseColor(s.AccentColor); _playPicker.Color = ParseColor(s.PlayButtonColor);
        _seekPicker.Color = ParseColor(s.SeekBarColor); _volumePicker.Color = ParseColor(s.VolumeBarColor);
    }
    private PlaybackSettings ReadExtendedSettings(PlaybackSettings s) => s with {
        KeepOpen = Value(_keepOpen), KeepOpenPause = _keepPause.IsOn, IdleMode = Value(_idleMode),
        LoopFile = _repeatFile.Text.Trim(), SavePosition = _savePosition.IsOn,
        WatchLaterOptions = string.Join(',', _resumeOptions.Text.Split(',').Select(x => x.Trim()).Distinct()),
        HighResolutionSeek = Value(_hrSeek), TrackAutoSelection = _autoTracks.IsOn,
        LoopPlaylist = _repeatPlaylist.Text.Trim(), AutoCreatePlaylist = Value(_autoPlaylist),
        AppTheme = Value(_theme), UseSystemColors = _systemColors.IsOn, AccentColor = Hex(_accentPicker),
        PlayButtonColor = Hex(_playPicker), SeekBarColor = Hex(_seekPicker), VolumeBarColor = Hex(_volumePicker) };
    private static string Hex(ColorPicker picker) => $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
    private static Windows.UI.Color ParseColor(string hex) => Windows.UI.Color.FromArgb(255,
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));
}
