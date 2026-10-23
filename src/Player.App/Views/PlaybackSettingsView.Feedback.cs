using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Player.App.Playback;
using Player.App.Presentation;
using Windows.UI.ViewManagement;
using Windows.Globalization.NumberFormatting;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    private PlaybackSettings? _savedDraft;
    private bool _populatingDraft, _draftUpdateQueued, _applyingSettings;
    private readonly HashSet<FrameworkElement> _watchedDraftInputs = [];
    private readonly Dictionary<NumberBox, (double Value, string Text)> _savedNumbers = [];
    private readonly AccessibilitySettings _toastAccessibility = new();
    private OverlayTransition? _settingsToastTransition;
    private DispatcherQueueTimer? _settingsToastTimer;

    private void InitializeSettingsFeedback()
    {
        _settingsToastTransition = new OverlayTransition(SettingsToastHost, slide: true, collapseWhenHidden: true,
            isHighContrast: () => _toastAccessibility.HighContrast);
        _settingsToastTransition.SetVisible(false, animate: false);
        _settingsToastTimer = DispatcherQueue.CreateTimer();
        _settingsToastTimer.Interval = TimeSpan.FromSeconds(4);
        _settingsToastTimer.IsRepeating = false;
        _settingsToastTimer.Tick += (_, _) => _settingsToastTransition.SetVisible(false);
        Unloaded += (_, _) =>
        {
            _settingsToastTimer.Stop();
            _settingsToastTransition.SetVisible(false, animate: false);
        };
    }

    private void ShowSettingsToast(string message)
    {
        SettingsToastMessage.Message = message;
        _settingsToastTimer?.Stop();
        _settingsToastTransition?.SetVisible(true);
        _settingsToastTimer?.Start();
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            (FrameworkElementAutomationPeer.FromElement(SettingsToastMessage)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(SettingsToastMessage))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void CaptureSavedDraft()
    {
        // Compare the normalized control round-trip, so casing/formatting in a
        // saved file cannot make an untouched page look modified.
        _savedDraft = ReadDraft();
        _savedNumbers.Clear();
        foreach (var number in _watchedDraftInputs.OfType<NumberBox>())
            _savedNumbers[number] = (number.Value, number.Text);
        UpdateDraftState();
    }

    private void WatchDraftInput(FrameworkElement input)
    {
        if (!_watchedDraftInputs.Add(input)) return;
        switch (input)
        {
            case NumberBox number:
                number.ValueChanged += (_, _) => QueueDraftStateUpdate();
                number.RegisterPropertyChangedCallback(NumberBox.TextProperty, (_, _) => QueueDraftStateUpdate());
                break;
            case ToggleSwitch toggle:
                toggle.Toggled += (_, _) => QueueDraftStateUpdate();
                break;
            case ComboBox combo:
                combo.SelectionChanged += (_, _) => QueueDraftStateUpdate();
                combo.RegisterPropertyChangedCallback(ComboBox.TextProperty, (_, _) => QueueDraftStateUpdate());
                break;
            case TextBox text:
                text.TextChanged += (_, _) => QueueDraftStateUpdate();
                break;
            case ColorPicker picker:
                picker.ColorChanged += (_, _) => QueueDraftStateUpdate();
                break;
            case Panel panel:
                foreach (var child in panel.Children.OfType<FrameworkElement>()) WatchDraftInput(child);
                break;
        }
    }

    private void QueueDraftStateUpdate()
    {
        if (_savedDraft is null || _populatingDraft || _draftUpdateQueued) return;
        _draftUpdateQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _draftUpdateQueued = false;
            UpdateDraftState();
        });
    }

    private void UpdateDraftState()
    {
        if (_savedDraft is null || _populatingDraft) return;
        bool changed = ReadDraft() != _savedDraft || HasPendingNumberEdit();
        if (changed) ApplyButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        else ApplyButton.ClearValue(FrameworkElement.StyleProperty); // Restore the implicit native button style.
        ApplyButton.IsEnabled = changed && _apply is not null && !_applyingSettings;
        RevertButton.IsEnabled = changed && !_applyingSettings;
    }

    private bool HasPendingNumberEdit()
    {
        // Native NumberBox commits typed text on focus loss. Allow Apply to
        // become available while typing, rather than requiring an extra Tab.
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is not NumberBox number || !_savedNumbers.TryGetValue(number, out var saved)) continue;
            if (number.NumberFormatter is INumberParser parser && parser.ParseDouble(number.Text) is double value)
                return !value.Equals(saved.Value);
            return number.Text != saved.Text;
        }
        return false;
    }
}
