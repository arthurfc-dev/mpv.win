using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Player.App.Views;
using Player.App.Library;

namespace Player.App.Presentation;

/// <summary>Shared dimensions for virtualized Home and Videos media cards.</summary>
internal sealed class MediaGridLayout
{
    private readonly GridView _grid;
    private bool _updating;
    private double _aspect = 16.0 / 9;
    internal double? Aspect { get; set; }
    internal double? TargetWidth { get; set; }

    internal void Apply(GalleryPreferences preferences, bool compact)
    {
        double aspect = compact || preferences.Layout == "Square" ? 1 : 16.0 / 9;
        double target = compact || preferences.Size == "Small" ? 168 : 240;
        if (Aspect == aspect && TargetWidth == target) return;
        Aspect = aspect;
        TargetWidth = target;
        Update();
    }

    internal MediaGridLayout(GridView grid)
    {
        _grid = grid;
        grid.Loaded += (_, _) => Update();
        grid.SizeChanged += (_, args) =>
        {
            MediaCard.StopHoverPreviews();
            if (args.NewSize.Width != args.PreviousSize.Width) Update();
        };
    }

    internal void Update()
    {
        if (_updating || _grid.ActualWidth <= 0 || _grid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        _updating = true;
        try
        {
            // Use window width, not pane width: the navigation pane collapses
            // at this same breakpoint and must not flip the cards back to wide.
            bool compact = (_grid.XamlRoot?.Size.Width ?? _grid.ActualWidth) < 1000;
            double aspect = Aspect ?? (compact ? 1 : 16.0 / 9);
            bool aspectChanged = _aspect != aspect;
            _aspect = aspect;
            // Match the actual content viewport, including the scrollbar gutter.
            double available = Math.Max(1, _grid.ActualWidth - _grid.Padding.Left - _grid.Padding.Right);
            double target = TargetWidth ?? (compact ? 176 : 240);
            // Keep the chosen card size stable. ItemsWrapGrid already wraps
            // columns as its viewport changes; stretching every card on each
            // pixel forced text, thumbnails and grouped rows to remeasure.
            double slot = Math.Min(target, Math.Floor(available));
            double height = Math.Round(Math.Max(1, slot - 16) / _aspect) + 80;
            if (panel.ItemWidth != slot) panel.ItemWidth = slot;
            if (panel.ItemHeight != height) panel.ItemHeight = height;
            // New/recycled cards are prepared by ContainerContentChanging.
            // Existing cards only need a walk when their crop actually changes.
            if (aspectChanged) PrepareRealized(_grid);
        }
        finally { _updating = false; }
    }

    internal void Prepare(GridViewItem container)
    {
        if (container.ContentTemplateRoot is MediaCard card) card.SetAspect(_aspect);
    }
    private void PrepareRealized(DependencyObject parent)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is GridViewItem item) Prepare(item);
            else PrepareRealized(child);
        }
    }
}
