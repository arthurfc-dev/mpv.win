using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Player.App.Presentation;
using Player.App.Library;
using Windows.Storage.Pickers;

namespace Player.App.Views;

public sealed partial class HomeView : UserControl
{
    internal event Action? OpenFileRequested;
    internal event Action<string>? OpenVideoRequested;
    private bool _active;
    private readonly List<(string Path, double Duration)> _pendingHistory = [];
    private string _homeSignature = string.Empty;
    private readonly LibraryPreferencesStore _store = new();
    private bool _preferencesReady;
    private bool? _galleryCompact;
    private readonly LibraryVideoCatalog _catalog = new();
    private readonly LibrarySidebar _sidebar;
    private readonly SidebarFolderTransitions _folderTransitions;
    internal nint OwnerHandle { get; set; }
    internal Func<bool>? HighContrastRequested { get; set; }
    internal async Task FlushPreferencesAsync()
    {
        await VideoLibrary.PendingRemoval;
        await _store.FlushAsync();
    }

    public HomeView()
    {
        InitializeComponent();
        InitializeLibrarySearch();

        VideoLibrary.SetLibraryCatalog(_catalog);
        _sidebar = new LibrarySidebar(Navigation, ComputerItem, CustomFoldersHeading, PinnedSeparator, PinnedFoldersHeading, _store, _catalog);
        _folderTransitions = new SidebarFolderTransitions(Navigation, () => HighContrastRequested?.Invoke() ?? false);
        _sidebar.ItemCreated += _folderTransitions.Watch;
        _folderTransitions.Watch(ComputerItem);
        VideoLibrary.OpenVideoRequested += path => OpenVideoRequested?.Invoke(path);
        VideoLibrary.OpenFolderRequested += NavigateFolder;
        VideoLibrary.ErrorOccurred += message => { LibraryNotice.Message = message; LibraryNotice.IsOpen = true; };
        Navigation.Expanding += (sender, args) =>
        {
            if (args.ExpandingItemContainer is NavigationViewItem item)
            {
                if (item == ComputerItem) _ = LoadFoldersAsync();
                else _sidebar.Expand(item);
                _folderTransitions.Expanding(item);
            }
        };
        Navigation.Collapsed += (sender, args) =>
        {
            if (args.CollapsedItemContainer is NavigationViewItem item) _folderTransitions.Collapsed(item);
        };
        _store.Changed += OnLibraryChanged;
        VideoLibrary.RefreshAvailabilityChanged += enabled => LibraryRefreshButton.IsEnabled = enabled;
        HomeToolbar.Changed += () =>
        {
            ApplyGallerySizing();
            if (VideoLibrary.Visibility == Visibility.Visible) VideoLibrary.OnPreferencesChanged();

            _store.Save();
        };
        Navigation.SelectedItem = HomeItem;
    }

    private async void OnViewLoaded(object sender, RoutedEventArgs args)
    {
        UpdatePaneHeader();
        _folderTransitions.WatchRootItems();
        if (_preferencesReady) return;
        await _store.LoadAsync();
        _preferencesReady = true;
        MediaSearch.IsEnabled = true;
        ConfigureLibrary();
        AddFolderButton.IsEnabled = true;
        HomeToolbar.SetPreferences(_store.ForView("home"));
        foreach (var item in _pendingHistory) _store.RecordPlayed(item.Path, item.Duration);
        _pendingHistory.Clear();
        ShowStorageError();
        if (ReferenceEquals(Navigation.SelectedItem, Navigation.SettingsItem)) ShowConfiguration();
        else if (Navigation.SelectedItem is NavigationViewItem selected) SelectPage(selected);
    }
    internal void RecordPlayed(string path, double duration)
    {
        if (_preferencesReady) _store.RecordPlayed(path, duration);
        else _pendingHistory.Add((path, duration));
    }
    internal void UpdateRecentDuration(string path, double duration)
    {
        if (_preferencesReady) _store.UpdateRecentDuration(path, duration);
        else
            for (int i = 0; i < _pendingHistory.Count; i++)
                if (_pendingHistory[i].Path.Equals(path, StringComparison.OrdinalIgnoreCase)) _pendingHistory[i] = (path, duration);
    }
    private void OnHomeFilterSelected(object sender, RoutedEventArgs args)
    {
        if (sender is RadioMenuFlyoutItem item) SelectHomeFilter(item);
    }
    private void SelectHomeFilter(RadioMenuFlyoutItem item)
    {
        foreach (var option in HomeSectionsMenu.Items.OfType<RadioMenuFlyoutItem>()) option.IsChecked = ReferenceEquals(option, item);
        HomeFilterLabel.Text = item.Text;
        if (item.Icon is FontIcon icon) HomeFilterIcon.Glyph = icon.Glyph;
        string label = $"Home sections: {item.Text}";
        ToolTipService.SetToolTip(HomeFilter, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HomeFilter, label);
        VideoLibrary.SetHomeFilter(item.Tag as string ?? "all");
    }
    private void NavigateFolder(string path)
    {
        if (_sidebar.FindFolder(path) is { } existing) Navigation.SelectedItem = existing;
        else
        {
            Navigation.SelectedItem = null;
            SelectPage(new NavigationViewItem { Content = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                Tag = new SidebarFolder(new VideoFolderNode(path), FolderSection.Custom, false) });
        }
    }
    private void ShowStorageError()
    {
        LibraryNotice.Message = _store.Error ?? string.Empty;
        LibraryNotice.IsOpen = _store.Error is not null;
    }
    private void OnLibraryChanged()
    {
        ShowStorageError();
        if (!_preferencesReady) return;
        bool selectedFolder = (Navigation.SelectedItem as NavigationViewItem)?.Tag is SidebarFolder;
        bool scopeChanged = ConfigureLibrary();
        string signature = string.Join("|", _store.State.RecentVideos.Select(item => item.Path + item.LastPlayed.Ticks + ":" + item.DurationSeconds))
            + string.Join("|", _store.State.LibraryFolders) + string.Join("|", _store.State.PinnedFolders);
        bool homeChanged = signature != _homeSignature;
        _homeSignature = signature;
        if (!scopeChanged && !(VideoLibrary.IsHome && homeChanged)) return;
        VideoLibrary.InvalidateLibrary();
        if (scopeChanged && selectedFolder) Navigation.SelectedItem = VideosItem;
        else VideoLibrary.SetActive(_active && VideoLibrary.Visibility == Visibility.Visible);
    }
    private bool ConfigureLibrary() => _sidebar.Configure();

    private async Task AddFolderAsync()
    {
        if (!_preferencesReady || OwnerHandle == 0) return;
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                var ancestor = _store.State.ExcludedFolders.FirstOrDefault(path => LibraryVideoCatalog.IsWithin(folder.Path, path)
                    && !folder.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (ancestor is not null)
                {
                    LibraryNotice.Message = $"This folder is inside an excluded folder ({ancestor}). Restore that folder from the This Computer menu first.";
                    LibraryNotice.IsOpen = true;
                    return;
                }
                _store.AddLibraryFolder(folder.Path);
                Navigation.SelectedItem = VideosItem;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        { LibraryNotice.Message = "Couldn't choose a folder. " + ex.Message; LibraryNotice.IsOpen = true; }
    }
    private async Task LoadFoldersAsync()
    {
        if (!_active || !_preferencesReady) return;
        try { await _catalog.GetAsync(false, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LibraryNotice.Message = "Couldn't finish finding videos. " + ex.Message; LibraryNotice.IsOpen = true; }
    }
    private void OnFoldersMenuOpening(object sender, object args)
    {
        FoldersMenu.Items.Clear();
        var add = new MenuFlyoutItem { Text = "Add a folder", Icon = new FontIcon { Glyph = "\uE8F4" }, IsEnabled = _preferencesReady };
        add.Click += async (_, _) => await AddFolderAsync();
        FoldersMenu.Items.Add(add);
        var restore = new MenuFlyoutSubItem { Text = "Restore excluded folder", Icon = new FontIcon { Glyph = "\uE7A7" },
            IsEnabled = _store.State.ExcludedFolders.Count > 0 };
        foreach (var path in _store.State.ExcludedFolders)
        {
            var item = new MenuFlyoutItem { Text = path, Icon = new FontIcon { Glyph = "\uE8B7" } };
            item.Click += (_, _) => _store.RestoreFolder(path);
            restore.Items.Add(item);
        }
        FoldersMenu.Items.Add(restore);
        var remove = new MenuFlyoutSubItem { Text = "Remove added folder", Icon = new FontIcon { Glyph = "\uE711" },
            IsEnabled = _store.State.LibraryFolders.Count > 0 };
        foreach (var path in _store.State.LibraryFolders)
        {
            var item = new MenuFlyoutItem { Text = path, Icon = new FontIcon { Glyph = "\uE8B7" } };
            item.Click += (_, _) => _store.RemoveLibraryFolder(path); remove.Items.Add(item);
        }
        FoldersMenu.Items.Add(remove);
    }
    private void OnDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) => UpdatePaneHeader();
    private void UpdatePaneHeader()
    {
        if (Navigation is not null)
            Navigation.IsPaneToggleButtonVisible = Navigation.DisplayMode != NavigationViewDisplayMode.Expanded;
    }

    internal void SetReady(bool ready) => OpenFileButton.IsEnabled = ready;
    internal void SetActive(bool active)
    {
        _active = active;
        if (!active) _searchTimer?.Stop();
        if (!active) _catalog.Cancel();
        else if (ReferenceEquals(Navigation.SelectedItem, ComputerItem)) _ = LoadFoldersAsync();
        VideoLibrary.SetActive(active && _preferencesReady && VideoLibrary.Visibility == Visibility.Visible);
    }
    internal void NavigateRecentHome()
    {
        SelectHomeFilter(HomeSectionsMenu.Items.OfType<RadioMenuFlyoutItem>().First(item => (string)item.Tag == "recent"));
        Navigation.SelectedItem = HomeItem;
    }
    internal void FocusCurrentLocation()
    {
        if (PlaybackSettingsHost.Visibility == Visibility.Visible) _settingsView?.FocusCategory();
        else if (_locationIndex >= 0 && _locations[_locationIndex].Tag is SearchLocation) MediaSearch.Focus(FocusState.Programmatic);
        else if (Navigation.SelectedItem is Control selected) selected.Focus(FocusState.Programmatic);
        else Navigation.Focus(FocusState.Programmatic);
    }
    private void OnOpenFile(object sender, RoutedEventArgs args) => OpenFileRequested?.Invoke();

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (PageHeading is null) return;
        if (args.IsSettingsSelected)
        {
            ShowConfiguration();
        }
        else if (args.SelectedItem is NavigationViewItem selected) SelectPage(selected);
    }

    private void SelectPage(NavigationViewItem selected)
    {
        PlaybackSettingsHost.Visibility = Visibility.Collapsed;
        Navigation.Visibility = Visibility.Visible;
        string tag = selected.Tag is SearchLocation ? "search" : selected.Tag is SidebarFolder folder ? folder.Node.Path : selected.Tag as string ?? "home";
        string query = (selected.Tag as SearchLocation)?.Query ?? "";
        SynchronizeSearchText(query);
        RememberLocation(new(LocationKey(selected.Tag), selected.Content as string ?? "Home", selected.Tag));
        bool home = tag == "home";
        PageHeading.Text = selected.Content as string ?? "Home";
        HomeFilter.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        bool videos = tag is "home" or "videos" or "favorites" or "computer" or "search" || System.IO.Path.IsPathFullyQualified(tag);
        VideoLibrary.Visibility = videos ? Visibility.Visible : Visibility.Collapsed;
        if (videos && _preferencesReady) VideoLibrary.Configure(_store, tag, System.IO.Path.IsPathFullyQualified(tag) ? tag : null,
            selected.Tag is SidebarFolder, searchQuery: query);
        VideoLibrary.SetActive(_active && videos && _preferencesReady);
        HomeToolbar.Visibility = home || videos ? Visibility.Visible : Visibility.Collapsed;
        LibraryRefreshButton.Visibility = videos ? Visibility.Visible : Visibility.Collapsed;
        if (_preferencesReady && (home || videos)) HomeToolbar.SetPreferences(_store.ForView(home ? "home" : tag));
        ApplyGallerySizing();
        VideoLibrary.OwnerHandle = OwnerHandle;
        OpenFileButton.Visibility = Visibility.Visible;
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (sender.DisplayMode != NavigationViewDisplayMode.Expanded) sender.IsPaneOpen = false;
    }

    private async void OnAddFolder(object sender, RoutedEventArgs args) => await AddFolderAsync();

    private void OnRefreshLibrary(object sender, RoutedEventArgs args) => VideoLibrary.Refresh();
    private void OnHomeSizeChanged(object sender, SizeChangedEventArgs args) => ApplyGallerySizing();
    private void OnContentSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var padding = args.NewSize.Width < 600 ? new Thickness(20, 12, 20, 24) : new Thickness(40, 12, 40, 32);
        if (ContentLayout.Padding != padding) ContentLayout.Padding = padding;
        ApplyGallerySizing();
    }
    private void ApplyGallerySizing()
    {
        if (VideoLibrary is null || HomeToolbar is null) return;
        double width = XamlRoot?.Size.Width ?? ActualWidth;
        double contentWidth = ContentLayout.ActualWidth - ContentLayout.Padding.Left - ContentLayout.Padding.Right;
        // Match the navigation breakpoint; also handle a narrow content pane.
        bool compact = width < 1000 || contentWidth < 480;
        if (_galleryCompact == compact) return;
        _galleryCompact = compact;
        HomeFilter.Width = compact ? 60 : 180;
        HomeFilterLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HomeToolbar.SetCompact(compact);

        VideoLibrary.SetCompact(compact);
    }
}
