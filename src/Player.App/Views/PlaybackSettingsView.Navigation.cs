using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    private string _category = "general";
    private int _categoryRevision;
    private readonly Dictionary<string, double> _categoryOffsets = new();

    internal void FocusCategory()
    {
        if (SettingsNavigation.SelectedItem is Control selected) selected.Focus(FocusState.Programmatic);
    }

    private void OnCategorySelected(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string category } item || CategoryHeading is null) return;
        _categoryOffsets[_category] = CategoryScroll.VerticalOffset;
        _category = category;
        CategoryHeading.Text = item.Content as string ?? "General";
        CategoryDescription.Text = category switch
        {
            "video" => "Adjust video playback and picture quality. Turn on advanced settings for more options.",
            "playback" => "Choose how videos play, where they resume and what happens when they finish.",
            "appearance" => "Choose a light or dark look and your favorite colors. Select Apply to save your changes.",
            "audio" => "Choose your volume, preferred language, speakers or headphones.",
            "window" => "Choose how the window opens and resizes. Startup changes take effect next time you open the app.",
            "subtitles" => "Change how subtitles look. Select Apply to use your changes in videos.",
            "input" => "Choose how far to jump when using the skip buttons or arrow keys.",
            _ => "Choose a settings category or set up opening videos from File Explorer."
        };
        GeneralFields.Visibility = category == "general" ? Visibility.Visible : Visibility.Collapsed;
        PlaybackFields.Visibility = category == "playback" ? Visibility.Visible : Visibility.Collapsed;
        AudioFields.Visibility = category == "audio" ? Visibility.Visible : Visibility.Collapsed;
        SubtitleFields.Visibility = category == "subtitles" ? Visibility.Visible : Visibility.Collapsed;
        InputFields.Visibility = category == "input" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceFields.Visibility = category == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        WindowFields.Visibility = category == "window" ? Visibility.Visible : Visibility.Collapsed;
        VideoFields.Visibility = category == "video" ? Visibility.Visible : Visibility.Collapsed;
        if (category == "subtitles") QueueSubtitlePreview();
        if (category == "audio") RefreshAudioDevices();
        int revision = ++_categoryRevision;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (revision == _categoryRevision) CategoryScroll.ChangeView(null, _categoryOffsets.GetValueOrDefault(category), null, disableAnimation: true);
        });
    }

    private void OnCategoryInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if ((args.InvokedItemContainer as NavigationViewItem)?.Tag as string == "library")
        { BackToLibraryRequested?.Invoke(); return; }
        if (sender.DisplayMode != NavigationViewDisplayMode.Expanded) sender.IsPaneOpen = false;
    }

    private void OnCategorySizeChanged(object sender, SizeChangedEventArgs args)
    {
        var padding = args.NewSize.Width < 600 ? new Thickness(20, 16, 20, 24) : new Thickness(32, 16, 32, 24);
        if (CategoryLayout.Padding != padding) CategoryLayout.Padding = padding;
    }

    private void AddCategoryLink(string title, string description, string category)
    {
        var labels = new StackPanel { Spacing = 6 };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        labels.Children.Add(new TextBlock { Text = description, Style = (Style)Resources["SettingsDescription"] });
        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(labels);
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 12 };
        Grid.SetColumn(chevron, 1); layout.Children.Add(chevron);
        var button = new Button { Content = layout, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16), CornerRadius = new CornerRadius(6) };
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, description);
        button.Click += (_, _) => SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems.OfType<NavigationViewItem>()
            .First(item => item.Tag as string == category);
        GeneralFields.Children.Add(button);
    }
}
