using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    private readonly List<(FrameworkElement Row, FrameworkElement Input)> _advancedRows = [];

    private void OnAdvancedSettingsToggled(object sender, RoutedEventArgs args)
    {
        UpdateAdvancedSettingsVisibility();
        QueueDraftStateUpdate();
    }

    private void UpdateAdvancedSettingsVisibility()
    {
        // XAML may raise Toggled while InitializeComponent is still running.
        if (AdvancedSettingsSwitch is null || VideoRenderSection is null || VideoLibplaceboSection is null) return;
        var visibility = AdvancedSettingsSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (row, _) in _advancedRows) row.Visibility = visibility;
        VideoRenderSection.Visibility = VideoLibplaceboSection.Visibility = visibility;
    }

    private void ResetVisibleSettings(Action reset)
    {
        // Simple-mode reset must not silently erase hidden tuning. Capture the
        // original controls, including picker values hosted outside the visual tree.
        List<Action> restore = AdvancedSettingsSwitch.IsOn ? [] : _advancedRows
            .Select(row => CaptureInput(row.Input)).ToList();
        reset();
        foreach (var action in restore) action();
    }

    private static Action CaptureInput(FrameworkElement input)
    {
        switch (input)
        {
            case NumberBox number:
                double value = number.Value;
                string text = number.Text;
                return () => { number.Value = value; number.Text = text; };
            case ToggleSwitch toggle:
                bool on = toggle.IsOn;
                return () => toggle.IsOn = on;
            case ComboBox combo:
                object? selected = combo.SelectedItem;
                return () => combo.SelectedItem = selected;
            case TextBox editor:
                string content = editor.Text;
                return () => editor.Text = content;
            case Button { Flyout: Flyout { Content: ColorPicker picker } }:
                var color = picker.Color;
                return () => picker.Color = color;
            default:
                return () => { };
        }
    }
}
