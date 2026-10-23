using Microsoft.UI.Xaml.Controls;
using Player.App.Playback;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    private SubtitlePreview? _subtitlePreview;
    private bool _previewQueued;

    private void InitializeSubtitlePreview()
    {
        _subtitlePreview = new SubtitlePreview();
        SubtitleFields.Children.Add(_subtitlePreview);
        _font.RegisterPropertyChangedCallback(ComboBox.TextProperty, (_, _) => QueueSubtitlePreview());
        foreach (var number in new[] { _size, _outline, _position, _shadowDistance, _shadowOpacity }) number.ValueChanged += (_, _) => QueueSubtitlePreview();
        _bold.Toggled += (_, _) => QueueSubtitlePreview();
        _italic.Toggled += (_, _) => QueueSubtitlePreview();
        _color.ColorChanged += (_, _) => QueueSubtitlePreview();
        _outlineColor.ColorChanged += (_, _) => QueueSubtitlePreview();
        _shadowEnabled.Toggled += (_, _) => QueueSubtitlePreview();
        _shadowColor.ColorChanged += (_, _) => QueueSubtitlePreview();
    }

    private void QueueSubtitlePreview()
    {
        if (_subtitlePreview is null || _category != "subtitles" || _previewQueued) return;
        _previewQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _previewQueued = false;
            if (_category != "subtitles") return;
            _subtitlePreview.Update(new PlaybackSettings {
                SubtitleFont = _font.Text, SubtitleSize = _size.Value, SubtitleOutline = _outline.Value,
                SubtitleBold = _bold.IsOn, SubtitleItalic = _italic.IsOn,
                SubtitleColor = Hex(_color), SubtitleOutlineColor = Hex(_outlineColor),
                SubtitleShadowEnabled = _shadowEnabled.IsOn, SubtitleShadowOffset = _shadowDistance.Value,
                SubtitleShadowColor = Hex(_shadowColor), SubtitleShadowOpacity = _shadowOpacity.Value,
                SubtitlePosition = _position.Value });
        });
    }
}
