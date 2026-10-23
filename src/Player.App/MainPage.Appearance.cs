using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Player.App;

public sealed partial class MainPage
{
    private readonly SolidColorBrush _seekAccent = new(), _volumeAccent = new(), _playAccent = new(), _playForeground = new();

    private void InitializeAppearanceBrushes()
    {
        foreach (var (slider, brush) in new[] { (SeekBar, _seekAccent), (VolumeBar, _volumeAccent) })
        {
            // The native template binds the resting filled track to Foreground.
            // Resource overrides alone do not replace the already-resolved style setter.
            slider.Foreground = brush;
            foreach (string key in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
                "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" }) slider.Resources[key] = brush;
        }
        PlayButton.Background = _playAccent;
        PlayButton.Foreground = _playForeground;
        PlayButton.Resources["AccentButtonBackgroundPointerOver"] = _playAccent;
        PlayButton.Resources["AccentButtonBackgroundPressed"] = _playAccent;
        PlayButton.Resources["AccentButtonForegroundPointerOver"] = _playForeground;
        PlayButton.Resources["AccentButtonForegroundPressed"] = _playForeground;
    }

    internal void RefreshAppearance()
    {
        var s = _playbackSettings.State;
        _window?.ApplyAppTheme(s.AppTheme);
        var ui = new UISettings();
        var system = ui.GetColorValue(HighContrast ? UIColorType.Foreground : UIColorType.Accent);
        Windows.UI.Color Chosen(string hex) => s.UseSystemColors || HighContrast ? system : Windows.UI.Color.FromArgb(255,
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));
        _seekAccent.Color = Chosen(s.SeekBarColor); _volumeAccent.Color = Chosen(s.VolumeBarColor);
        _playAccent.Color = Chosen(s.PlayButtonColor);
        _playForeground.Color = HighContrast ? ui.GetColorValue(UIColorType.Background)
            : .2126 * _playAccent.Color.R + .7152 * _playAccent.Color.G + .0722 * _playAccent.Color.B > 145 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        ((SolidColorBrush)Application.Current.Resources["MpvWinNavigationAccentBrush"]).Color = Chosen(s.AccentColor);
    }
}
