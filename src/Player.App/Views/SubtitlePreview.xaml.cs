using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Player.App.Playback;
using Windows.UI.Text;

namespace Player.App.Views;

// Illustrative draft preview. Player subtitles continue to be rendered by mpv;
// these offset text layers approximate an outline/shadow without a second decoder or
// another graphics dependency, and do not claim libass-identical metrics.
public sealed partial class SubtitlePreview : UserControl
{
    private const string ExampleText = "This is a subtitle preview.\nThe second line helps check readability.";
    private readonly List<TextBlock> _outlineLayers = [];
    private readonly TextBlock _shadow = new() { TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, MaxLines = 3, RenderTransform = new TranslateTransform() };
    private double _position = 100;

    public SubtitlePreview()
    {
        InitializeComponent();
        AutomationProperties.SetAccessibilityView(_shadow, AccessibilityView.Raw);
        CaptionLayers.Children.Insert(0, _shadow);
        for (int index = 0; index < 16; index++)
        {
            var outline = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black), RenderTransform = new TranslateTransform() };
            AutomationProperties.SetAccessibilityView(outline, AccessibilityView.Raw);
            _outlineLayers.Add(outline);
            CaptionLayers.Children.Insert(index + 1, outline);
        }
        Update(new PlaybackSettings());
    }

    internal void Update(PlaybackSettings style)
    {
        string font = string.IsNullOrWhiteSpace(style.SubtitleFont) ? "Arial" : style.SubtitleFont.Trim();
        if (font.Equals("sans-serif", StringComparison.OrdinalIgnoreCase)) font = "Arial";
        FontFamily family;
        try { family = new FontFamily(font); } catch (ArgumentException) { family = new FontFamily("Arial"); }
        double size = double.IsFinite(style.SubtitleSize) ? Math.Clamp(style.SubtitleSize, 1, 200) : 38;
        double thickness = double.IsFinite(style.SubtitleOutline) ? Math.Clamp(style.SubtitleOutline, 0, 10) : 1.65;
        _position = double.IsFinite(style.SubtitlePosition) ? Math.Clamp(style.SubtitlePosition, 0, 100) : 100;
        foreach (var text in _outlineLayers.Append(_shadow).Append(Caption))
        {
            text.Text = ExampleText;
            text.FontFamily = family;
            text.FontSize = size;
            text.FontWeight = style.SubtitleBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            text.FontStyle = style.SubtitleItalic ? FontStyle.Italic : FontStyle.Normal;
        }
        if (TryColor(style.SubtitleColor, out var foreground)) Caption.Foreground = new SolidColorBrush(foreground);
        _shadow.Visibility = style.SubtitleShadowEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (TryColor(style.SubtitleShadowColor, out var shadowColor)) _shadow.Foreground = new SolidColorBrush(shadowColor);
        _shadow.Opacity = double.IsFinite(style.SubtitleShadowOpacity) ? Math.Clamp(style.SubtitleShadowOpacity / 100, 0, 1) : .75;
        double distance = double.IsFinite(style.SubtitleShadowOffset) ? Math.Clamp(style.SubtitleShadowOffset, .5, 10) : 2;
        var shadowOffset = (TranslateTransform)_shadow.RenderTransform;
        shadowOffset.X = distance; shadowOffset.Y = distance;
        for (int index = 0; index < _outlineLayers.Count; index++)
        {
            var outline = _outlineLayers[index];
            outline.Visibility = thickness > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (TryColor(style.SubtitleOutlineColor, out var color)) outline.Foreground = new SolidColorBrush(color);
            double angle = index * Math.PI * 2 / _outlineLayers.Count;
            var offset = (TranslateTransform)outline.RenderTransform;
            offset.X = Math.Cos(angle) * thickness;
            offset.Y = Math.Sin(angle) * thickness;
        }
        PositionCaption();
    }

    private void OnCaptionSizeChanged(object sender, SizeChangedEventArgs args) => PositionCaption();
    private void PositionCaption()
    {
        if (CaptionLayers is null) return;
        double travel = Math.Max(0, 720 - 60 - CaptionLayers.ActualHeight);
        Canvas.SetTop(CaptionLayers, 30 + travel * _position / 100);
    }

    private void OnSceneLoaded(object sender, RoutedEventArgs args)
    {
        var visual = ElementCompositionPreview.GetElementVisual(Scene);
        var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new System.Numerics.Vector2(1280, 720);
        geometry.CornerRadius = new System.Numerics.Vector2(10);
        visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
    }

    private static bool TryColor(string? text, out Windows.UI.Color color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#') return false;
        if (!byte.TryParse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red)
            || !byte.TryParse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green)
            || !byte.TryParse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue)) return false;
        color = Windows.UI.Color.FromArgb(255, red, green, blue);
        return true;
    }
}
