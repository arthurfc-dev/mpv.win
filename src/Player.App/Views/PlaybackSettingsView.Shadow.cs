using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Player.App.Playback;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    private readonly ToggleSwitch _shadowEnabled = new();
    private readonly NumberBox _shadowDistance = Number(.5, 10, .5), _shadowOpacity = Number(0, 100, 5);
    private readonly ColorPicker _shadowColor = Picker();
    private Button? _shadowColorButton;

    private void InitializeSubtitleShadow()
    {
        AddRow(SubtitleFields, "Drop shadow", "Add a shadow behind subtitles to help them stand out against the video.", _shadowEnabled);
        AddRow(SubtitleFields, "Shadow distance", "Move the shadow farther from the letters. A smaller value keeps it closer to the text.", _shadowDistance);
        _shadowColorButton = ColorButton(_shadowColor, "Subtitle shadow color");
        AddRow(SubtitleFields, "Shadow color", "Choose the color of the subtitle shadow.", _shadowColorButton);
        AddRow(SubtitleFields, "Shadow opacity (%)", "Choose how visible the shadow is. 0 hides it and 100 makes it fully solid.", _shadowOpacity);
        _shadowEnabled.Toggled += (_, _) => UpdateShadowControls();
        UpdateShadowControls();
    }
    private void UpdateShadowControls()
    {
        _shadowDistance.IsEnabled = _shadowOpacity.IsEnabled = _shadowEnabled.IsOn;
        if (_shadowColorButton is not null) _shadowColorButton.IsEnabled = _shadowEnabled.IsOn;
    }
    private void PopulateSubtitleShadow(PlaybackSettings settings)
    {
        _shadowDistance.Value = settings.SubtitleShadowOffset;
        _shadowOpacity.Value = settings.SubtitleShadowOpacity;
        _shadowColor.Color = ParseColor(settings.SubtitleShadowColor);
        _shadowEnabled.IsOn = settings.SubtitleShadowEnabled;
        UpdateShadowControls();
    }
    private PlaybackSettings ReadSubtitleShadow(PlaybackSettings settings) => settings with {
        SubtitleShadowEnabled = _shadowEnabled.IsOn, SubtitleShadowOffset = _shadowDistance.Value,
        SubtitleShadowColor = Hex(_shadowColor), SubtitleShadowOpacity = _shadowOpacity.Value };
}
