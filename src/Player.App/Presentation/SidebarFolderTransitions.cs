using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Player.App.Presentation;

// Adds motion to the native hierarchy without replacing its keyboard behavior,
// layout, or item templates. The child-host lookup matches the pinned WinUI
// template; if that part changes, ordinary native navigation still works.
internal sealed class SidebarFolderTransitions(NavigationView navigation, Func<bool> highContrast)
{
    private sealed class State
    {
        internal bool Watched;
        internal bool HostAnimated;
        internal FrameworkElement? Host;
        internal Microsoft.UI.Dispatching.DispatcherQueueTimer? IndicatorCleanup;
    }
    private readonly ConditionalWeakTable<FrameworkElement, State> _states = new();
    private readonly UISettings _settings = new();
    private bool Enabled => _settings.AnimationsEnabled && !highContrast();

    internal void WatchRootItems()
    {
        PrepareItems(navigation.MenuItems);
        if (navigation.SettingsItem is NavigationViewItem settings) Watch(settings);
    }

    internal void Watch(NavigationViewItem item)
    {
        var state = _states.GetOrCreateValue(item);
        if (state.Watched) return;
        state.Watched = true;
        item.RegisterPropertyChangedCallback(NavigationViewItem.IsSelectedProperty, (_, _) => SelectionChanged(item));
        item.RegisterPropertyChangedCallback(NavigationViewItem.IsChildSelectedProperty, (_, _) => SelectionChanged(item));
        item.RegisterPropertyChangedCallback(NavigationViewItem.IsExpandedProperty, (_, _) => SelectionChanged(item));
        item.Loaded += (_, _) => item.DispatcherQueue.TryEnqueue(() => PrepareItem(item));
        item.Unloaded += (_, _) => state.IndicatorCleanup?.Stop();
        if (item.IsLoaded) item.DispatcherQueue.TryEnqueue(() => PrepareItem(item));
    }

    internal void Expanding(NavigationViewItem item)
    {
        PrepareItems(navigation.MenuItems);
        // The native template realizes its child host lazily on first expansion.
        item.DispatcherQueue.TryEnqueue(() =>
        {
            PrepareItems(navigation.MenuItems);
            AttachHost(item, true);
        });
    }

    internal void Collapsed(NavigationViewItem item)
    {
        PrepareItems(navigation.MenuItems);
        AttachHost(item, false);
        ClipInactiveIndicator(item);
    }

    private static bool ShowsIndicator(NavigationViewItem item) => item.IsSelected || (item.IsChildSelected && !item.IsExpanded);
    private void SelectionChanged(NavigationViewItem item)
    {
        if (ShowsIndicator(item) && FindPart(item, "SelectionIndicator") is { } selected) selected.Clip = null;
        var state = _states.GetOrCreateValue(item);
        if (state.IndicatorCleanup is null)
        {
            state.IndicatorCleanup = item.DispatcherQueue.CreateTimer();
            state.IndicatorCleanup.IsRepeating = false;
            state.IndicatorCleanup.Interval = TimeSpan.FromMilliseconds(350);
            state.IndicatorCleanup.Tick += (_, _) => ClipInactiveIndicator(item);
        }
        state.IndicatorCleanup.Stop(); state.IndicatorCleanup.Start();
    }
    private static void ClipInactiveIndicator(NavigationViewItem item)
    {
        if (FindPart(item, "SelectionIndicator") is not { } indicator) return;
        // Keep native geometry/selection animations intact. Once they settle,
        // clip inactive markers to prevent residual subpixel strokes. Clipping
        // preserves measured dimensions for the next native transition.
        indicator.Clip = ShowsIndicator(item) ? null : new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 0, 0) };
    }

    private void PrepareItems(IEnumerable<object> items)
    {
        foreach (var item in items.OfType<NavigationViewItem>())
        {
            Watch(item);
            if (item.IsLoaded) PrepareItem(item);
            PrepareItems(item.MenuItems);
        }
    }

    private void PrepareItem(NavigationViewItem item)
    {
        // Do not animate the item visual's Offset. NavigationView owns the
        // selection indicator's movement between these rows; a second implicit
        // layout animation can leave the previous indicator at a subpixel edge.
        AttachHost(item, false);
    }

    private void AttachHost(NavigationViewItem item, bool reveal)
    {
        var state = _states.GetOrCreateValue(item);
        var host = FindPart(item, "NavigationViewItemMenuItemsHost");
        if (host is null) return;
        if (!ReferenceEquals(state.Host, host)) { state.Host = host; state.HostAnimated = false; }
        var visual = ElementCompositionPreview.GetElementVisual(host);
        if (!Enabled)
        {
            if (!state.HostAnimated) return;
            ElementCompositionPreview.SetImplicitShowAnimation(host, null);
            ElementCompositionPreview.SetImplicitHideAnimation(host, null);
            visual.StopAnimation("Opacity"); visual.Opacity = 1;
            state.HostAnimated = false;
            return;
        }
        if (!state.HostAnimated)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(host, Fade(visual.Compositor, true));
            ElementCompositionPreview.SetImplicitHideAnimation(host, Fade(visual.Compositor, false));
            state.HostAnimated = true;
        }
        // Also cover the first expansion, whose Visibility change can precede
        // attachment of implicit animations to the newly realized host.
        if (reveal && host.Visibility == Visibility.Visible)
            visual.StartAnimation("Opacity", Fade(visual.Compositor, true));
    }

    private static ScalarKeyFrameAnimation Fade(Compositor compositor, bool show)
    {
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.Duration = TimeSpan.FromMilliseconds(show ? 180 : 140);
        if (show) fade.InsertKeyFrame(0, 0);
        else fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, show ? 1 : 0, Ease(compositor));
        return fade;
    }

    private static CubicBezierEasingFunction Ease(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));

    private static FrameworkElement? FindPart(DependencyObject parent, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement host && host.Name == name) return host;
            if (child is NavigationViewItem) continue;
            if (FindPart(child, name) is { } found) return found;
        }
        return null;
    }
}
