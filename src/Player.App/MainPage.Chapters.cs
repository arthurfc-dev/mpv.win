using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Player.App.Playback;
using Windows.Foundation;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Player.App;

public sealed partial class MainPage
{
    private ChapterCatalog _visibleChapters = ChapterCatalog.Empty;
    private double _chapterDuration;
    private Canvas? _chapterCanvas;
    private Rectangle? _chapterTrack, _chapterDecrease;
    private Thumb? _chapterThumb;
    private ShapePath? _chapterBackground, _chapterProgress;
    private readonly RectangleGeometry _chapterProgressClip = new();

    private void UpdateChapters(bool loaded)
    {
        var catalog = _session?.Chapters ?? ChapterCatalog.Empty;
        if (!loaded || catalog.Generation != _snapshot.MediaGeneration) catalog = ChapterCatalog.Empty;
        if (!ReferenceEquals(catalog, _visibleChapters) || _chapterDuration != _snapshot.Duration)
        {
            _visibleChapters = catalog;
            _chapterDuration = _snapshot.Duration;
            RebuildChapterTrack();
        }
        int chapterIndex = _snapshot.Chapter;
        // Containers may place the first chapter a few milliseconds after
        // zero. Treat that lead-in as part of the first chapter in the UI.
        if (chapterIndex < 0 && catalog.Chapters.Count > 0 && catalog.Chapters[0].Time >= 0 &&
            _snapshot.Position <= catalog.Chapters[0].Time) chapterIndex = 0;
        string? chapterTitle = chapterIndex >= 0 && chapterIndex < catalog.Chapters.Count
            ? catalog.Chapters[chapterIndex].Title.ReplaceLineEndings(" ").Trim() : null;
        string file = _openedPath is null ? "Video" : System.IO.Path.GetFileNameWithoutExtension(_openedPath);
        string title = string.IsNullOrWhiteSpace(chapterTitle) ? file : chapterTitle;
        if (MediaTitle.Text != title)
        {
            MediaTitle.Text = title;
            ToolTipService.SetToolTip(MediaTitle, string.IsNullOrWhiteSpace(chapterTitle) ? _openedPath : $"{chapterTitle}\n{file}");
        }
    }

    private void OnChapterSeekBarLoaded(object sender, RoutedEventArgs e)
    {
        RebuildChapterTrack();
    }

    private bool EnsureChapterTrack()
    {
        if (_chapterCanvas is not null) return true;
        if (!SeekBar.IsLoaded) return false;
        SeekBar.ApplyTemplate();
        // Pinned WinUI template parts. If a future template changes, leave the
        // ordinary native slider intact rather than guessing its track bounds.
        _chapterTrack = FindSeekPart<Rectangle>(SeekBar, "HorizontalTrackRect");
        _chapterDecrease = FindSeekPart<Rectangle>(SeekBar, "HorizontalDecreaseRect");
        _chapterThumb = FindSeekPart<Thumb>(SeekBar, "HorizontalThumb");
        if (_chapterTrack is null || _chapterDecrease is null || _chapterThumb is null ||
            VisualTreeHelper.GetParent(_chapterTrack) is not Grid grid ||
            VisualTreeHelper.GetParent(_chapterDecrease) != grid || VisualTreeHelper.GetParent(_chapterThumb) != grid) return false;

        _chapterCanvas = new Canvas { IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(_chapterCanvas, AccessibilityView.Raw);
        Grid.SetRow(_chapterCanvas, Grid.GetRow(_chapterTrack));
        Grid.SetColumnSpan(_chapterCanvas, Grid.GetColumnSpan(_chapterTrack));
        _chapterBackground = new ShapePath();
        _chapterProgress = new ShapePath { Clip = _chapterProgressClip };
        // Follow the native state/theme brushes, including custom seek colors.
        _chapterBackground.SetBinding(ShapePath.FillProperty, new Binding { Source = _chapterTrack, Path = new PropertyPath("Fill") });
        _chapterProgress.SetBinding(ShapePath.FillProperty, new Binding { Source = _chapterDecrease, Path = new PropertyPath("Fill") });
        _chapterCanvas.Children.Add(_chapterBackground);
        _chapterCanvas.Children.Add(_chapterProgress);
        grid.Children.Insert(grid.Children.IndexOf(_chapterDecrease) + 1, _chapterCanvas);
        _chapterTrack.SizeChanged += (_, _) => RebuildChapterTrack();
        _chapterThumb.SizeChanged += (_, _) => RebuildChapterTrack();
        _chapterDecrease.SizeChanged += (_, _) => UpdateChapterProgress();
        SeekBar.IsEnabledChanged += (_, _) => RebuildChapterTrack();
        return true;
    }

    private static T? FindSeekPart<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && element.Name == name) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindSeekPart<T>(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
    }

    private void RebuildChapterTrack()
    {
        // Loading a collapsed control does not guarantee its template children
        // exist yet. Retry when it gains a size or receives chapter metadata.
        if (!EnsureChapterTrack() || _chapterCanvas is null || _chapterTrack is null || _chapterDecrease is null ||
            _chapterBackground is null || _chapterProgress is null || _chapterThumb is null) return;
        double width = _chapterTrack.ActualWidth, height = _chapterTrack.ActualHeight;
        bool enabled = !HighContrast && SeekBar.IsEnabled && double.IsFinite(_chapterDuration) && _chapterDuration > 0 &&
            width > _chapterThumb.ActualWidth && height > 0 && _visibleChapters.Chapters.Count > 1;
        _chapterCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _chapterTrack.Opacity = _chapterDecrease.Opacity = enabled ? 0 : 1;
        if (!enabled) return;

        double thumb = _chapterThumb.ActualWidth;
        // Explicit sizes are necessary for paths hosted in a Canvas: its own
        // measure pass intentionally contributes no desired size to the slider.
        _chapterBackground.Width = _chapterProgress.Width = width;
        _chapterBackground.Height = _chapterProgress.Height = height;
        // WinUI geometries have a single owner. Each Path must receive its own
        // geometry and figures; assigning one geometry to both throws on open.
        var backgroundSegments = new PathGeometry { FillRule = FillRule.Nonzero };
        var progressSegments = new PathGeometry { FillRule = FillRule.Nonzero };
        void AddSegment(double left, double segmentWidth)
        {
            backgroundSegments.Figures.Add(ChapterSegment(left, segmentWidth, height));
            progressSegments.Figures.Add(ChapterSegment(left, segmentWidth, height));
        }
        double start = 0;
        // Adjacent tiny chapters share a visible division. Bound work to the
        // track's available pixels and keep the actual timestamps unmodified.
        // The first chapter owns the start of the track even if its timestamp
        // is slightly positive. Only later chapters introduce a division.
        foreach (double time in _visibleChapters.Chapters.Skip(1).Select(c => c.Time)
                     .Where(t => double.IsFinite(t) && t > 0 && t < _chapterDuration).Distinct().Order())
        {
            double x = thumb / 2 + time / _chapterDuration * (width - thumb);
            if (x - start < 8 || width - x < 8) continue;
            AddSegment(start, x - start - 2);
            start = x + 2;
        }
        AddSegment(start, Math.Max(0, width - start));
        _chapterBackground.Data = backgroundSegments;
        _chapterProgress.Data = progressSegments;
        UpdateChapterProgress();
    }

    private static PathFigure ChapterSegment(double x, double width, double height)
    {
        double radius = Math.Min(height / 2, width / 2);
        var figure = new PathFigure { StartPoint = new Point(x + radius, 0), IsClosed = true, IsFilled = true };
        void Line(double endX, double endY) => figure.Segments.Add(new LineSegment { Point = new Point(endX, endY) });
        void Arc(double endX, double endY) => figure.Segments.Add(new ArcSegment
        {
            Point = new Point(endX, endY), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise
        });
        Line(x + width - radius, 0); Arc(x + width, radius);
        Line(x + width, height - radius); Arc(x + width - radius, height);
        Line(x + radius, height); Arc(x, height - radius);
        Line(x, radius); Arc(x + radius, 0);
        return figure;
    }

    private void UpdateChapterProgress()
    {
        if (_chapterCanvas?.Visibility != Visibility.Visible || _chapterTrack is null || _chapterThumb is null) return;
        double width = _chapterTrack.ActualWidth, thumb = _chapterThumb.ActualWidth;
        double fraction = (SeekBar.Value - SeekBar.Minimum) / Math.Max(1, SeekBar.Maximum - SeekBar.Minimum);
        // The native thumb travels across track width minus its own width.
        _chapterProgressClip.Rect = new Rect(0, 0, Math.Clamp(thumb / 2 + fraction * (width - thumb), 0, width), _chapterTrack.ActualHeight);
    }
}
