using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Player.App.Presentation;

/// <summary>Animates UI overlays independently of the video surface and its layout.</summary>
internal sealed class OverlayTransition(FrameworkElement element, bool slide, bool collapseWhenHidden, Func<bool> isHighContrast)
{
    private readonly UISettings _settings = new();
    private bool _visible = true;
    private int _revision;
    private ScalarKeyFrameAnimation? _opacity;
    private Vector3KeyFrameAnimation? _translation;
    internal event Action<bool>? Completed;

    internal void SetVisible(bool visible, bool animate = true)
    {
        if (_visible == visible && animate) return;
        _visible = visible;
        int revision = ++_revision;

        if (!animate || !_settings.AnimationsEnabled || isHighContrast())
        {
            StopAnimations();
            element.Opacity = visible ? 1 : 0;
            if (slide) element.Translation = visible ? Vector3.Zero : new Vector3(0, 28, 0);
            if (collapseWhenHidden) element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Completed?.Invoke(visible);
            return;
        }

        if (element.Visibility == Visibility.Collapsed)
        {
            element.Opacity = 0;
            if (slide) element.Translation = new Vector3(0, 28, 0);
            element.Visibility = Visibility.Visible;
        }
        var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        if (_opacity is null)
        {
            _opacity = compositor.CreateScalarKeyFrameAnimation();
            _opacity.Target = "Opacity";
            _opacity.Duration = TimeSpan.FromMilliseconds(250);
            _opacity.InsertExpressionKeyFrame(0, "this.StartingValue");
        }
        _opacity.InsertKeyFrame(1, visible ? 1 : 0, easing);
        element.StartAnimation(_opacity);
        if (slide)
        {
            if (_translation is null)
            {
                _translation = compositor.CreateVector3KeyFrameAnimation();
                _translation.Target = "Translation";
                _translation.Duration = TimeSpan.FromMilliseconds(250);
                _translation.InsertExpressionKeyFrame(0, "this.StartingValue");
            }
            _translation.InsertKeyFrame(1, visible ? Vector3.Zero : new Vector3(0, 28, 0), easing);
            element.StartAnimation(_translation);
        }
        batch.Completed += (_, _) =>
        {
            if (_revision == revision)
            {
                StopAnimations();
                element.Opacity = visible ? 1 : 0;
                if (slide) element.Translation = visible ? Vector3.Zero : new Vector3(0, 28, 0);
                if (!visible && collapseWhenHidden) element.Visibility = Visibility.Collapsed;
                Completed?.Invoke(visible);
            }
            batch.Dispose();
        };
        batch.End();
    }

    internal void Stop()
    {
        ++_revision;
        StopAnimations();
        element.Opacity = _visible ? 1 : 0;
        if (slide) element.Translation = _visible ? Vector3.Zero : new Vector3(0, 28, 0);
        _opacity?.Dispose();
        _translation?.Dispose();
        _opacity = null;
        _translation = null;
    }

    private void StopAnimations()
    {
        if (_opacity is not null) element.StopAnimation(_opacity);
        if (_translation is not null) element.StopAnimation(_translation);
    }
}
