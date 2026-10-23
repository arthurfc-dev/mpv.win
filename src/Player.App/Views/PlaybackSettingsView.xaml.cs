using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Player.App.Playback;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView : UserControl
{
    internal event Action? BackToLibraryRequested;
    private PlaybackSettings _current = new();
    private Func<PlaybackSettings, Task<string?>>? _apply;
    private bool? _activeVideoZeroCopy;
    private readonly NumberBox _back = Number(.1, 600), _forward = Number(.1, 600), _speed = Number(.25, 4, .25),
        _chapter = Number(-1, 60), _size = Number(1, 200), _outline = Number(0, 10, .25), _position = Number(0, 100);
    private readonly ToggleSwitch _exact = new(), _pitch = new(), _bold = new(), _italic = new(), _overrideAss = new();
    private readonly ComboBox _font = new() { IsEditable = true, Width = 220, ItemsSource = new[] { "sans-serif", "Segoe UI", "Arial", "Calibri", "Verdana", "Tahoma", "Times New Roman", "Consolas" } };
    private readonly ColorPicker _color = Picker(), _outlineColor = Picker();

    public PlaybackSettingsView()
    {
        InitializeComponent();
        InitializeSettingsFeedback();
        InitializeExtendedSettings();
        InitializeWindowSettings();
        InitializeVideoSettings();
        AddCategoryLink("Video", "Adjust video playback and picture quality.", "video");
        AddCategoryLink("Playback", "Change playback speed and choose what happens when a video ends.", "playback");
        AddCategoryLink("Audio", "Choose your volume, audio language, speakers or headphones.", "audio");
        AddCategoryLink("Subtitles", "Change how subtitles look and where they appear.", "subtitles");
        AddCategoryLink("Input", "Choose how far the skip buttons and arrow keys jump.", "input");
        AddCategoryLink("Appearance", "Light or dark theme and interface colors.", "appearance");
        AddCategoryLink("Window", "Choose how the window opens, resizes and remembers its position.", "window");
        InitializeExplorerSettings();
        AddRow(InputFields, "Skip backward (seconds)", "How far to jump back when you press Left or use the rewind button.", _back);
        AddRow(InputFields, "Skip forward (seconds)", "How far to jump ahead when you press Right or use the forward button.", _forward);
        AddRow(InputFields, "Precise skipping", "Jump to the exact time when skipping. Turn this off if skipping feels slow. Dragging the seek bar always stays precise.", _exact, advanced: true);
        AddRow(PlaybackFields, "Playback speed", "Choose how fast the video plays. 1 is normal, 0.5 is half speed and 2 is double speed.", _speed);
        AddRow(AudioFields, "Preserve audio pitch", "Keep voices sounding natural when you speed up or slow down a video.", _pitch);
        AddRow(PlaybackFields, "Previous chapter threshold (seconds)", "Choose how many seconds into a chapter the Previous button starts that chapter again. Before that point, it goes back one chapter. Use -1 to always go back one chapter.", _chapter, advanced: true);
        AddReset(PlaybackFields, () => PopulatePlayback(new()));
        InitializeAudioSettings();
        AddReset(AudioFields, () => PopulateAudio(new()));
        AddReset(InputFields, () => PopulateInput(new()));
        InitializeSubtitlePreview();
        SubtitleFields.Children.Add(new TextBlock { Text = "These settings change text subtitles. Subtitles with their own styling may keep that appearance; you can change this with Override ASS styling under advanced settings. Subtitles stored as pictures cannot change font.", TextWrapping = TextWrapping.Wrap });
        AddRow(SubtitleFields, "Font family", "Choose the lettering used for subtitles, or type the name of a font installed on your PC.", _font);
        AddRow(SubtitleFields, "Font size", "Make subtitles larger or smaller. They also resize to fit the player window.", _size);
        AddRow(SubtitleFields, "Bold", "Make subtitle text thicker and easier to see.", _bold);
        AddRow(SubtitleFields, "Italic", "Give subtitle text a slanted appearance.", _italic);
        AddRow(SubtitleFields, "Text color", "Choose the color of the subtitle text.", ColorButton(_color, "Subtitle text color"));
        AddRow(SubtitleFields, "Outline color", "Choose the color of the border around each letter. A dark border can help light text stand out.", ColorButton(_outlineColor, "Subtitle outline color"));
        AddRow(SubtitleFields, "Outline thickness", "Make the border around each letter thicker or thinner. Use 0 to remove it.", _outline);
        InitializeSubtitleShadow();
        AddRow(SubtitleFields, "Vertical position (%)", "Move subtitles up or down: 0 is at the top and 100 is near the bottom. The player temporarily moves them above visible playback controls.", _position, advanced: true);
        AddRow(SubtitleFields, "Override ASS styling", "Replace the video's own subtitle font, colors and layout with your settings. This can change specially styled subtitles.", _overrideAss, advanced: true);
        AddReset(SubtitleFields, () => PopulateSubtitles(new()));
        WatchDraftInput(AdvancedSettingsSwitch);
        Populate(_current);
        CaptureSavedDraft();
        SettingsNavigation.SelectedItem = GeneralCategory;
    }

    internal void Configure(PlaybackSettings current, Func<PlaybackSettings, Task<string?>> apply, string? warning)
    {
        _current = current;
        _activeVideoZeroCopy ??= current.Video.ZeroCopy;
        _apply = apply;
        Populate(current);
        CaptureSavedDraft();
        if (warning is not null) ShowNotice(warning, InfoBarSeverity.Warning);
    }

    private static NumberBox Number(double min, double max, double step = 1) => new()
    { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 200 };
    private void AddRow(StackPanel parent, string title, string description, FrameworkElement input, bool advanced = false)
    {
        WatchDraftInput(input);
        AutomationProperties.SetName(input, title);
        AutomationProperties.SetHelpText(input, description);
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        labels.Children.Add(new TextBlock { Text = description, Style = (Style)Resources["SettingsDescription"] });
        row.Children.Add(labels);
        Grid.SetColumn(input, 1); input.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(input);
        bool? stacked = null;
        row.SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < 560;
            if (stacked == compact) return;
            stacked = compact;
            Grid.SetColumn(input, compact ? 0 : 1); Grid.SetRow(input, compact ? 1 : 0);
            Grid.SetColumnSpan(input, compact ? 2 : 1);
            input.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            input.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        };
        var container = new Border { Child = row, Style = (Style)Resources["SettingsRow"] };
        parent.Children.Add(container);
        if (advanced || parent == VideoRenderFields || parent == VideoScalingFields)
            _advancedRows.Add((container, input));
    }
    private void AddReset(StackPanel parent, Action reset)
    {
        var button = new Button { Content = "Restore defaults", HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => ResetVisibleSettings(reset); parent.Children.Add(button);
    }
    private void Populate(PlaybackSettings settings)
    {
        _populatingDraft = true;
        try { AdvancedSettingsSwitch.IsOn = settings.ShowAdvancedSettings; PopulatePlayback(settings); PopulateSubtitles(settings); PopulateAudio(settings); PopulateInput(settings); PopulateAppearance(settings); PopulateWindow(settings.Window); PopulateVideo(settings.Video); UpdateAdvancedSettingsVisibility(); }
        finally { _populatingDraft = false; }
        QueueDraftStateUpdate();
    }
    private void PopulatePlayback(PlaybackSettings settings)
    {
        _speed.Value = settings.Speed;
        _chapter.Value = settings.ChapterThreshold;
        PopulateExtendedPlayback(settings);
    }
    private void PopulateAudio(PlaybackSettings settings) { _pitch.IsOn = settings.PitchCorrection; PopulateExtendedAudio(settings); }
    private void PopulateInput(PlaybackSettings settings)
    {
        _back.Value = settings.BackwardSeconds; _forward.Value = settings.ForwardSeconds; _exact.IsOn = settings.ExactSeeking;
    }
    private void PopulateSubtitles(PlaybackSettings settings)
    {
        // Select the matching item as well as Text: editable ComboBox's initial
        // template otherwise displays an empty selection until first interaction.
        var fonts = ((IEnumerable<string>)_font.ItemsSource).ToList();
        if (!fonts.Contains(settings.SubtitleFont)) { fonts.Add(settings.SubtitleFont); _font.ItemsSource = fonts; }
        _font.SelectedItem = settings.SubtitleFont;
        _font.Text = settings.SubtitleFont; _size.Value = settings.SubtitleSize; _outline.Value = settings.SubtitleOutline;
        _bold.IsOn = settings.SubtitleBold; _italic.IsOn = settings.SubtitleItalic;
        _color.Color = ParseColor(settings.SubtitleColor); _outlineColor.Color = ParseColor(settings.SubtitleOutlineColor);
        _position.Value = settings.SubtitlePosition; _overrideAss.IsOn = settings.OverrideAssStyle;
        PopulateSubtitleShadow(settings);
    }
    private void OnDiscard(object sender, RoutedEventArgs args) { Populate(_current); SettingsNotice.IsOpen = false; }
    private async void OnApply(object sender, RoutedEventArgs args)
    {
        if (_apply is null || _applyingSettings) return;
        ApplyButton.Focus(FocusState.Programmatic); // Commit any active NumberBox edit first.
        var candidate = ReadDraft();
        if (candidate == _savedDraft) { UpdateDraftState(); return; }
        if (candidate.Validate() is { } validation) { ShowNotice(validation, InfoBarSeverity.Error); return; }
        _applyingSettings = true;
        UpdateDraftState();
        IsEnabled = false;
        try
        {
            string? error = await _apply(candidate);
            if (error is null)
            {
                _current = candidate;
                CaptureSavedDraft();
                SettingsNotice.IsOpen = false;
                ShowSettingsToast("Settings applied.");
                if (_activeVideoZeroCopy != candidate.Video.ZeroCopy)
                    ShowNotice("Settings saved. Restart mpv.win to apply the D3D11 decoder zero-copy change.", InfoBarSeverity.Warning);
            }
            else ShowNotice(error, InfoBarSeverity.Error);
        }
        catch (Exception ex) { ShowNotice("Couldn't apply settings. " + ex.Message, InfoBarSeverity.Error); }
        finally { IsEnabled = true; _applyingSettings = false; UpdateDraftState(); }
    }

    private PlaybackSettings ReadDraft()
    {
        var candidate = new PlaybackSettings { ShowAdvancedSettings = AdvancedSettingsSwitch.IsOn, BackwardSeconds = _back.Value, ForwardSeconds = _forward.Value,
            ExactSeeking = _exact.IsOn, Speed = _speed.Value, PitchCorrection = _pitch.IsOn, ChapterThreshold = _chapter.Value,
            SubtitleFont = _font.Text.Trim(), SubtitleSize = _size.Value, SubtitleOutline = _outline.Value,
            SubtitleBold = _bold.IsOn, SubtitleItalic = _italic.IsOn, SubtitleColor = Hex(_color),
            SubtitleOutlineColor = Hex(_outlineColor), SubtitlePosition = _position.Value, OverrideAssStyle = _overrideAss.IsOn };
        candidate = ReadExtendedSettings(candidate);
        candidate = ReadAudioSettings(candidate);
        candidate = ReadSubtitleShadow(candidate);
        candidate = candidate with { Window = ReadWindowSettings(), Video = ReadVideoSettings() };
        return candidate;
    }
    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        if (severity == InfoBarSeverity.Success) { ShowSettingsToast(message); return; }
        SettingsNotice.Severity = severity; SettingsNotice.Message = message; SettingsNotice.IsOpen = true;
    }
}
