using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace Player.App.Views;

public sealed partial class MediaCard : UserControl
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(MediaCard), new PropertyMetadata(string.Empty, OnPathChanged));
    public static readonly DependencyProperty IsFolderProperty = DependencyProperty.Register(
        nameof(IsFolder), typeof(bool), typeof(MediaCard), new PropertyMetadata(false, OnContentChanged));
    public string FilePath { get => (string)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public bool IsFolder { get => (bool)GetValue(IsFolderProperty); set => SetValue(IsFolderProperty, value); }
    public event RoutedEventHandler? PlayRequested, MoreRequested;
    internal FrameworkElement MoreAnchor => CardMoreButton;
    private bool _pointerInside;
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(MediaCard), new PropertyMetadata(string.Empty, OnContentChanged));
    public static readonly DependencyProperty DurationTextProperty = DependencyProperty.Register(
        nameof(DurationText), typeof(string), typeof(MediaCard), new PropertyMetadata(string.Empty, OnContentChanged));
    public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(
        nameof(Thumbnail), typeof(ImageSource), typeof(MediaCard), new PropertyMetadata(null, OnContentChanged));
    public static readonly DependencyProperty IsPreviewLoadingProperty = DependencyProperty.Register(
        nameof(IsPreviewLoading), typeof(bool), typeof(MediaCard), new PropertyMetadata(false, OnContentChanged));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string DurationText { get => (string)GetValue(DurationTextProperty); set => SetValue(DurationTextProperty, value); }
    public ImageSource? Thumbnail { get => (ImageSource?)GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }
    public bool IsPreviewLoading { get => (bool)GetValue(IsPreviewLoadingProperty); set => SetValue(IsPreviewLoadingProperty, value); }
    private double _aspect = 16.0 / 9;

    public MediaCard() { InitializeComponent(); UpdateContent(); }

    private static void OnContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MediaCard)sender).UpdateContent();
    private static void OnPathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (MediaCard)sender;
        card._pointerInside = false; card.StopMotion(); card.CardActions.Visibility = Visibility.Collapsed;
    }

    private void UpdateContent()
    {
        if (TitleLabel is null) return;
        TitleLabel.Text = Title;
        DurationLabel.Text = DurationText;
        Artwork.ImageSource = Thumbnail;
        FallbackIcon.Glyph = IsFolder ? "\uE8B7" : "\uE714";
        FallbackIcon.Visibility = Thumbnail is null && !IsPreviewLoading ? Visibility.Visible : Visibility.Collapsed;
        PreviewProgress.IsActive = IsPreviewLoading;
        PreviewProgress.Visibility = IsPreviewLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetAspect(double aspect)
    {
        if (_aspect == aspect) return;
        _aspect = aspect;
        UpdateHeight();
    }
    private void OnThumbnailSizeChanged(object sender, SizeChangedEventArgs args) { UpdateHeight(); UpdateMotionClip(); }
    private void UpdateHeight()
    {
        if (ThumbnailArea.ActualWidth <= 0) return;
        double height = Math.Round(ThumbnailArea.ActualWidth / _aspect);
        if (Math.Abs(ThumbnailArea.Height - height) > 0.1) ThumbnailArea.Height = height;
    }

    internal void SetPointerOver(bool inside)
    {
        if (inside)
        {
            if (_pointerInside) return;
            _pointerInside = true; CardActions.Visibility = Visibility.Visible;
            BeginMotion();
        }
        else
        {
            _pointerInside = false; StopMotion();
            if (!ContainsFocus()) CardActions.Visibility = Visibility.Collapsed;
        }
    }
    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args) => CardActions.Visibility = Visibility.Visible;
    internal void UpdateFocusActions(bool focused)
    {
        CardActions.Visibility = focused || _pointerInside || ContainsFocus() ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnLostFocus(object sender, RoutedEventArgs args)
    {
        if (!_pointerInside && !ContainsFocus()) CardActions.Visibility = Visibility.Collapsed;
    }
    private bool ContainsFocus() => XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused
        && IsDescendant(focused);
    private bool IsDescendant(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, this)) return true;
        return false;
    }
    private void OnCardUnloaded(object sender, RoutedEventArgs args) { _pointerInside = false; StopMotion(); }
    private void OnPlayClick(object sender, RoutedEventArgs args) { StopMotion(); PlayRequested?.Invoke(this, args); }
    private void OnMoreClick(object sender, RoutedEventArgs args) { StopMotion(); MoreRequested?.Invoke(this, args); }
}
