using Microsoft.UI.Xaml.Controls;
using Player.App.Playback;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    private readonly TextBox _windowTitle = new() { Width = 220, MaxLength = 256, IsSpellCheckEnabled = false };
    private readonly ToggleSwitch _startFullscreen = new(), _startMaximized = new(), _keepWindowAspect = new(), _rememberPosition = new(), _snapEdges = new();
    private readonly NumberBox _screen = Number(-1, 32), _fit = Number(0, 100), _fitMin = Number(0, 100), _fitMax = Number(1, 100),
        _fitImage = Number(0, 100), _fitAudio = Number(0, 100), _positionX = Number(0, 100), _positionY = Number(0, 100),
        _minAspect = Number(0, 10, .1), _minAudioAspect = Number(0, 10, .1);
    private readonly ComboBox _startSize = Choices(("fit", "Fit each file"), ("height-session", "Remember height this session"),
        ("size-session", "Remember size this session"), ("height-persistent", "Remember height across launches"), ("size-persistent", "Remember size across launches"));

    private void InitializeWindowSettings()
    {
        AddRow(WindowFields, "Window title", "Choose the name shown in the taskbar and when switching windows. Use ${filename} for the file name, ${media-title} for its title or ${path} for its full location. Leave empty to show mpv.win.", _windowTitle, advanced: true);
        AddRow(WindowFields, "Start fullscreen", "Fill the screen the next time the app opens. Press Esc to leave fullscreen. This takes priority over Start maximized.", _startFullscreen);
        AddRow(WindowFields, "Start maximized", "Open the app at the largest normal window size next time, with the taskbar still available.", _startMaximized);
        AddRow(WindowFields, "Display", "Choose which monitor to use: 0 is the first, 1 is the second, and so on. Use -1 to stay on the current monitor. If a monitor is unavailable, the current one is used.", _screen, advanced: true);
        AddRow(WindowFields, "Initial video height (%)", "Choose how much of the available screen height the window uses when a video opens. Use 0 to start at the video's original size.", _fit, advanced: true);
        AddRow(WindowFields, "Minimum fitting height (%)", "Set the smallest starting window height as a percentage of the available screen. The window still needs enough room for its controls.", _fitMin, advanced: true);
        AddRow(WindowFields, "Maximum fitting height (%)", "Set the largest starting window height as a percentage of the available screen. The window is also kept within the screen width.", _fitMax, advanced: true);
        AddRow(WindowFields, "Initial image height (%)", "Choose how much of the available screen height to use when opening a still image. Use 0 for the image's original size.", _fitImage, advanced: true);
        AddRow(WindowFields, "Initial audio height (%)", "Choose how much of the available screen height to use for audio-only files. Use 0 to keep the current window height.", _fitAudio, advanced: true);
        AddRow(WindowFields, "Horizontal position (%)", "Choose where the window starts from left to right: 0 is left, 50 is centered and 100 is right. A remembered window position takes priority.", _positionX, advanced: true);
        AddRow(WindowFields, "Vertical position (%)", "Choose where the window starts from top to bottom: 0 is top, 50 is centered and 100 is bottom. A remembered window position takes priority.", _positionY, advanced: true);
        AddRow(WindowFields, "Initial size policy", "Choose whether the window fits each file or remembers its size. You can remember it until the app closes or keep it for the next launch too.", _startSize);
        AddRow(WindowFields, "Lock window to media aspect", "Keep the window the same shape as the video when you resize it. Turn this off to resize freely; the video keeps its shape with empty space around it.", _keepWindowAspect);
        AddRow(WindowFields, "Minimum window aspect ratio", "Set how wide the window must be compared with its height. For example, 1.7778 means 16:9. Use 0 to turn this limit off. The window must still fit your screen.", _minAspect, advanced: true);
        AddRow(WindowFields, "Minimum audio aspect ratio", "Set the minimum window width compared with its height for audio-only files. Use 0 for the default. The window must still fit your screen.", _minAudioAspect, advanced: true);
        AddRow(WindowFields, "Remember window position", "Open the app where you left it. If that spot is no longer on a connected monitor, the window moves back onto the screen.", _rememberPosition);
        AddRow(WindowFields, "Snap to display edges", "Help the window line up with nearby screen edges as you move it. Windows Snap still works as usual.", _snapEdges);
        AddReset(WindowFields, () => PopulateWindow(new()));
    }
    private void PopulateWindow(WindowSettings s)
    {
        _windowTitle.Text = s.Title; _startFullscreen.IsOn = s.Fullscreen; _startMaximized.IsOn = s.Maximized;
        _screen.Value = s.Screen; _fit.Value = s.AutoFit; _fitMin.Value = s.AutoFitSmaller; _fitMax.Value = s.AutoFitLarger;
        _fitImage.Value = s.AutoFitImage; _fitAudio.Value = s.AutoFitAudio; _positionX.Value = s.PositionX; _positionY.Value = s.PositionY;
        Select(_startSize, s.StartSize); _keepWindowAspect.IsOn = s.KeepAspect; _minAspect.Value = s.MinimumAspect;
        _minAudioAspect.Value = s.MinimumAudioAspect; _rememberPosition.IsOn = s.RememberPosition; _snapEdges.IsOn = s.SnapEdges;
    }
    private WindowSettings ReadWindowSettings() => new() {
        Title = _windowTitle.Text.Trim(), Fullscreen = _startFullscreen.IsOn, Maximized = _startMaximized.IsOn,
        Screen = double.IsFinite(_screen.Value) && _screen.Value == Math.Truncate(_screen.Value) ? (int)_screen.Value : -2,
        AutoFit = _fit.Value, AutoFitSmaller = _fitMin.Value, AutoFitLarger = _fitMax.Value, AutoFitImage = _fitImage.Value,
        AutoFitAudio = _fitAudio.Value, PositionX = _positionX.Value, PositionY = _positionY.Value, StartSize = Value(_startSize),
        KeepAspect = _keepWindowAspect.IsOn, MinimumAspect = _minAspect.Value, MinimumAudioAspect = _minAudioAspect.Value,
        RememberPosition = _rememberPosition.IsOn, SnapEdges = _snapEdges.IsOn };
}
