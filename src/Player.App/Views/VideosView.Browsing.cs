using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Player.App.Library;
using Player.App.Presentation;
using Windows.Storage;

namespace Player.App.Views;

public sealed partial class VideosView
{
    private readonly ObservableCollection<GallerySection> _sections = [];
    private readonly List<VideoLibraryItem> _allItems = [];
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderListing> _folderCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly MediaGridLayout _layout;
    private readonly SemaphoreSlim _previewSlots = new(2);
    private readonly Dictionary<FrameworkElement, PreviewLease> _previews = [];
    private readonly Dictionary<string, ImageSource> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _thumbnailOrder = new();
    private sealed record PreviewLease(VideoLibraryItem Item, CancellationTokenSource Cancellation);
    private LibraryPreferencesStore? _store;
    private LibraryVideoCatalog? _library;
    private GalleryPreferences _preferences = new();
    private CancellationTokenSource? _activeLifetime, _scanLifetime, _sortLifetime;
    private string _source = "videos", _homeFilter = "all", _searchQuery = "";
    private string? _folder;
    private bool _active, _loaded, _compact, _removing;
    private int _scanVersion, _sortVersion;
    private Task _removalTask = Task.CompletedTask;
    internal Task PendingRemoval => _removalTask;
    private bool LibrarySource => _source is "videos" or "computer" or "home" or "search";
    internal bool IsHome => _source == "home";
    internal nint OwnerHandle { get; set; }
    internal event Action<string>? OpenVideoRequested, OpenFolderRequested, ErrorOccurred;
    internal event Action<bool>? RefreshAvailabilityChanged;

    public VideosView()
    {
        InitializeComponent();
        _layout = new MediaGridLayout(VideosGrid);
        var source = new CollectionViewSource { IsSourceGrouped = true, Source = _sections };
        VideosGrid.ItemsSource = source.View;
    }

    internal void SetLibraryCatalog(LibraryVideoCatalog catalog)
    {
        _library = catalog;
        catalog.Progress += progress =>
        {
            if (!_active || _loaded || _source != "videos" || _sections.Count != 1) return;
            var section = _sections[0];
            foreach (var path in progress.Files) section.Add(new VideoLibraryItem(path));
            CountLabel.Text = $"{progress.Total:N0} videos found · Scanning…";
            if (section.Count > 0) StatusPanel.Visibility = Visibility.Collapsed;
        };
    }

    internal void Configure(LibraryPreferencesStore store, string source, string? folder = null, bool directOnly = false, string? searchQuery = null)
    {
        string query = searchQuery ?? "";
        bool changed = _source != source || !string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase) || _searchQuery != query;
        if (changed) { SetActive(false); _loaded = false; }
        _store = store; _source = source; _folder = folder; _searchQuery = query;
        _preferences = store.ForView(source);
        FolderLabel.Visibility = Visibility.Collapsed;
        _layout.Apply(_preferences, _compact);
    }

    internal void SetHomeFilter(string filter)
    {
        if (_homeFilter == filter) return;
        _homeFilter = filter;
        if (IsHome) { _loaded = false; if (_active) _ = RefreshAsync(); }
    }
    internal void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact; _layout.Apply(_preferences, compact);
    }
    internal async void OnPreferencesChanged()
    {
        _layout.Apply(_preferences, _compact);
        if (_active && _loaded) await SortAsync(_activeLifetime!.Token);
    }
    internal void InvalidateLibrary()
    {
        SetActive(false); _loaded = false; _folderCache.Clear();
    }
    internal void Refresh() => _ = RefreshAsync(true);
    internal void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (!active)
        {
            MediaCard.StopHoverPreviews();
            if (BusyRing.IsActive) _loaded = false;
            ++_scanVersion; ++_sortVersion;
            _sortLifetime?.Cancel(); _scanLifetime?.Cancel();
            _activeLifetime?.Cancel(); _activeLifetime?.Dispose(); _activeLifetime = null;
            ReleasePreviews(); _thumbnailCache.Clear(); _thumbnailOrder.Clear();
            BusyRing.IsActive = false; BusyRing.Visibility = Visibility.Collapsed;
            RefreshAvailabilityChanged?.Invoke(true);
            return;
        }
        _activeLifetime = new();
        if (!_loaded) _ = RefreshAsync();
        else DispatcherQueue.TryEnqueue(LoadRealizedPreviews);
    }

    private async Task RefreshAsync(bool refresh = false)
    {
        if (!_active || _activeLifetime is null || _store is null) return;
        _scanLifetime?.Cancel(); _scanLifetime?.Dispose();
        _scanLifetime = CancellationTokenSource.CreateLinkedTokenSource(_activeLifetime.Token);
        var token = _scanLifetime.Token; int version = ++_scanVersion;
        _sortLifetime?.Cancel(); ++_sortVersion;
        _loaded = false;
        MediaCard.StopHoverPreviews(); ReleasePreviews();
        _sections.Clear(); _allItems.Clear();
        if (refresh) { _folderCache.Clear(); _thumbnailCache.Clear(); _thumbnailOrder.Clear(); }
        SetStatus(_source == "search" ? "Searching mpv.win's library" : "Finding your videos",
            _source == "search" ? "Matching video names and folder paths across your library locations."
                : "Looking in your selected Windows and library folders.", true);
        CountLabel.Text = string.Empty; ToolTipService.SetToolTip(CountLabel, null);
        RefreshAvailabilityChanged?.Invoke(false);
        try
        {
            int skipped = 0;
            if (_source == "search")
            {
                string query = _searchQuery;
                var snapshot = await _library!.GetAsync(refresh, token);
                var matches = await Task.Run(() => LibrarySearch.Find(snapshot.Files, query, token), token);
                token.ThrowIfCancellationRequested();
                if (version != _scanVersion) return;
                var section = new GallerySection { HeaderVisible = false };
                section.Entries.AddRange(matches.Select(path => new VideoLibraryItem(path)));
                _sections.Add(section);
                skipped = snapshot.Skipped;
            }
            else if (_source == "videos")
            {
                var section = new GallerySection { HeaderVisible = false };
                _sections.Add(section);
                var pending = _library!.GetAsync(refresh, token);
                foreach (var path in _library.Discovered) section.Add(new VideoLibraryItem(path));
                var snapshot = await pending;
                section.Entries.AddRange(snapshot.Files.Select(path => new VideoLibraryItem(path)));
                skipped = snapshot.Skipped;
            }
            else if (_source == "favorites")
            {
                var section = new GallerySection { HeaderVisible = false };
                section.Entries.AddRange(_store.State.Favorites.Select(path => new VideoLibraryItem(path)));
                _sections.Add(section);
            }
            else if (_source is "computer" or "home")
            {
                if (IsHome && _homeFilter is "all" or "recent")
                {
                    var recent = _store.State.RecentVideos.ToArray();
                    var existing = await Task.Run(() => recent.Where(item => File.Exists(item.Path)).ToArray(), token);
                    var section = new GallerySection { Key = "recent", Heading = "Recent videos", Glyph = "\uE81C", Recent = true };
                    foreach (var saved in existing)
                    {
                        var item = new VideoLibraryItem(saved.Path);
                        if (saved.DurationSeconds > 0) item.SetDuration(TimeSpan.FromSeconds(saved.DurationSeconds));
                        section.Entries.Add(item);
                    }
                    section.SetExpanded(_expanded.GetValueOrDefault(section.Key, true));
                    _sections.Add(section);
                    if (section.Count > 0)
                    {
                        StatusPanel.Visibility = Visibility.Collapsed;
                        CountLabel.Text = $"{section.Count:N0} recent videos · Finding folders…";
                    }
                }
                if (!IsHome || _homeFilter is "all" or "computer" or "custom")
                {
                    var snapshot = await _library!.GetAsync(refresh, token);
                    skipped = snapshot.Skipped;
                    if (!IsHome || _homeFilter is "all" or "computer")
                        foreach (var root in _store.DefaultFolders.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
                            if (snapshot.Folders.TryGetValue(root.Path, out var node) && node.TotalVideoCount > 0)
                                AddFolderSection("computer:" + root.Path, root.Name, node, snapshot.Files);
                    if (IsHome && _homeFilter is "all" or "custom")
                        foreach (var path in _store.State.LibraryFolders.ToArray())
                            if (snapshot.ExistingRoots.Contains(path) && snapshot.Folders.TryGetValue(path, out var node))
                                AddFolderSection("custom:" + path, "Added · " + node.Name, node, snapshot.Files);
                }
                if (IsHome && _homeFilter is "all" or "pinned")
                    foreach (var path in _store.State.PinnedFolders.ToArray())
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var listing = await GetFolderAsync(path, token);
                            skipped += listing.Skipped;
                            AddFolderSection("pinned:" + path, "Pinned · " + listing.Root.Name, listing.Root, listing.Files);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                    }
            }
            else if (_folder is not null)
            {
                if (refresh && _library!.Roots.Any(root => LibraryVideoCatalog.IsWithin(_folder, root)))
                    await _library.GetAsync(true, token);
                var listing = await GetFolderAsync(_folder, token);
                skipped = listing.Skipped;
                AddFolderSection("folder:" + _folder, listing.Root.Name, listing.Root, listing.Files);
            }
            token.ThrowIfCancellationRequested();
            _allItems.AddRange(_sections.SelectMany(section => section.Entries));
            await SortAsync(token);
            token.ThrowIfCancellationRequested();
            if (version != _scanVersion) return;
            _loaded = true;
            int videos = _allItems.Where(item => !item.IsFolder).Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int folders = _allItems.Where(item => item.IsFolder).Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            CountLabel.Text = $"{videos:N0} {(videos == 1 ? "video" : "videos")}" + (folders > 0 ? $" · {folders:N0} folders" : string.Empty);
            if (_source == "search") CountLabel.Text += $" · “{_searchQuery}”";
            if (skipped > 0) ToolTipService.SetToolTip(CountLabel, $"{skipped:N0} locations couldn't be read. Refresh to try again.");
            if (_allItems.Count == 0) SetStatus(_source == "search" ? "No matching videos" : IsHome ? "Welcome to mpv.win" : "No videos found",
                _source == "search" ? "Try another video name or folder path. Search covers all mpv.win library locations."
                    : IsHome ? "Videos you play appear in Recent videos. Choose another Home filter, or add a folder to your library."
                    : _source == "favorites" ? "Right-click a video and choose Add to favorites." : "Add a folder or choose Refresh to look again.", false);
            else StatusPanel.Visibility = Visibility.Collapsed;
            DispatcherQueue.TryEnqueue(LoadRealizedPreviews);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        { if (version == _scanVersion) SetStatus("Couldn't finish loading this collection", ex.Message, false); }
        finally
        {
            if (version == _scanVersion)
            {
                BusyRing.IsActive = false; BusyRing.Visibility = Visibility.Collapsed;
                RefreshAvailabilityChanged?.Invoke(true);
            }
        }
    }

    private void AddFolderSection(string key, string heading, VideoFolderNode node, IReadOnlyList<string> files)
    {
        var section = new GallerySection { Key = key, Heading = heading, FolderPath = node.Path };
        foreach (var child in node.Children.Values.Where(child => child.TotalVideoCount > 0))
        {
            int count = child.DirectVideoCount + child.Children.Values.Count(node => node.TotalVideoCount > 0);
            section.Entries.Add(new VideoLibraryItem(child.Path) { IsFolder = true, Title = child.Name,
                DurationText = $"{count:N0} {(count == 1 ? "item" : "items")}" });
        }
        section.Entries.AddRange(files.Where(path => string.Equals(Path.GetDirectoryName(path), node.Path, StringComparison.OrdinalIgnoreCase))
            .Select(path => new VideoLibraryItem(path)));
        section.SetExpanded(_expanded.GetValueOrDefault(section.Key, true));
        _sections.Add(section);
        if (section.Count > 0) StatusPanel.Visibility = Visibility.Collapsed;
    }

    private async Task<FolderListing> GetFolderAsync(string path, CancellationToken token)
    {
        if (_library!.Snapshot is { } snapshot && snapshot.Folders.TryGetValue(path, out var node))
            return new(node, snapshot.Files.Where(file => LibraryVideoCatalog.IsWithin(file, path)).ToArray(), snapshot.Skipped);
        if (_folderCache.TryGetValue(path, out var cached)) return cached;
        foreach (var parent in _folderCache.Values)
        {
            if (!LibraryVideoCatalog.IsWithin(path, parent.Root.Path)) continue;
            VideoFolderNode? child = parent.Root;
            string location = parent.Root.Path;
            foreach (var segment in Path.GetRelativePath(location, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                location = Path.Combine(location, segment);
                if (child is null || !child.Children.TryGetValue(location, out var next)) { child = null; break; }
                child = next;
            }
            if (child is not null)
                return new(child, parent.Files.Where(file => LibraryVideoCatalog.IsWithin(file, path)).ToArray(), parent.Skipped);
        }
        var listing = await FolderListing.LoadAsync(path, _store!.State.ExcludedFolders.ToArray(), token);
        token.ThrowIfCancellationRequested();
        _folderCache[path] = listing;
        return listing;
    }

    private async Task SortAsync(CancellationToken token)
    {
        _sortLifetime?.Cancel(); _sortLifetime?.Dispose();
        _sortLifetime = CancellationTokenSource.CreateLinkedTokenSource(token); token = _sortLifetime.Token;
        int version = ++_sortVersion;
        var sections = _sections.ToArray();
        string sort = _preferences.Sort; bool descending = _preferences.Descending;
        try
        {
            var candidates = sections.Where(section => !section.Recent).SelectMany(section => section.Entries).ToArray();
            if (sort is "Created" or "Modified") await Task.Run(() =>
            {
                foreach (var item in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        FileSystemInfo info = item.IsFolder ? new DirectoryInfo(item.Path) : new FileInfo(item.Path);
                        if (info.Exists) { item.Created = info.CreationTimeUtc; item.Modified = info.LastWriteTimeUtc; }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }, token);
            if (sort == "Taken") foreach (var item in candidates.Where(item => !item.IsFolder && !item.TakenRead))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.Path).AsTask(token);
                    var values = await file.Properties.RetrievePropertiesAsync(["System.Photo.DateTaken"]).AsTask(token);
                    if (values.TryGetValue("System.Photo.DateTaken", out var value) && value is DateTimeOffset date) item.Taken = date.UtcDateTime;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException) { }
                item.TakenRead = true;
            }
            token.ThrowIfCancellationRequested();
            if (version != _sortVersion) return;
            ReleasePreviews();
            foreach (var section in sections)
            {
                if (!section.Recent)
                {
                    Func<VideoLibraryItem, DateTime?> date = sort switch { "Taken" => item => item.Taken, "Created" => item => item.Created, _ => item => item.Modified };
                    IOrderedEnumerable<VideoLibraryItem> ordered = section.Entries.OrderBy(item => !item.IsFolder);
                    if (sort == "Name") ordered = descending ? ordered.ThenByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                        : ordered.ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase);
                    else ordered = descending ? ordered.ThenBy(item => !date(item).HasValue).ThenByDescending(date)
                        : ordered.ThenBy(item => !date(item).HasValue).ThenBy(date);
                    var result = ordered.ToArray(); section.Entries.Clear(); section.Entries.AddRange(result);
                }
                section.SetExpanded(_expanded.GetValueOrDefault(section.Key, true));
            }
            DispatcherQueue.TryEnqueue(LoadRealizedPreviews);
        }
        catch (OperationCanceledException) { }
    }

    private void SetStatus(string title, string detail, bool loading)
    {
        StatusTitle.Text = title; StatusDetail.Text = detail; StatusPanel.Visibility = Visibility.Visible;
        BusyRing.IsActive = loading; BusyRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnSectionToggle(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not GallerySection section) return;
        MediaCard.StopHoverPreviews();
        section.SetExpanded(!section.Expanded); _expanded[section.Key] = section.Expanded;
    }
    private void OnVideoClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not VideoLibraryItem item) return;
        MediaCard.StopHoverPreviews();
        if (item.IsFolder) OpenFolderRequested?.Invoke(item.Path); else OpenVideoRequested?.Invoke(item.Path);
    }
    private async Task PlayAsync(VideoLibraryItem item)
    {
        MediaCard.StopHoverPreviews();
        if (!item.IsFolder) { OpenVideoRequested?.Invoke(item.Path); return; }
        if (_activeLifetime is null) return;
        var token = _activeLifetime.Token;
        try
        {
            var listing = await GetFolderAsync(item.Path, token);
            token.ThrowIfCancellationRequested();
            if (listing.Files.FirstOrDefault() is { } first) OpenVideoRequested?.Invoke(first);
            else ErrorOccurred?.Invoke("This folder has no available videos to play.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorOccurred?.Invoke(ex.Message); }
    }
    private async void OnCardPlay(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is VideoLibraryItem item) await PlayAsync(item);
    }
    private void OnCardMore(object sender, RoutedEventArgs args)
    {
        if (sender is MediaCard { DataContext: VideoLibraryItem item } card) CreateMenu(item).ShowAt(card.MoreAnchor);
    }
    private MenuFlyout CreateMenu(VideoLibraryItem item)
    {
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = "\uE768" } };
        play.Click += async (_, _) => await PlayAsync(item); menu.Items.Add(play);
        if (item.IsFolder)
        {
            bool pinned = _store!.State.PinnedFolders.Contains(item.Path, StringComparer.OrdinalIgnoreCase);
            var pin = new MenuFlyoutItem { Text = pinned ? "Unpin folder" : "Pin folder", Icon = new FontIcon { Glyph = "\uE718" } };
            pin.Click += (_, _) => { if (pinned) _store.Unpin(item.Path); else _store.Pin(item.Path); };
            menu.Items.Add(pin);
        }
        if (!item.IsFolder)
        {
            var favorite = new MenuFlyoutItem { Text = _store!.IsFavorite(item.Path) ? "Remove from favorites" : "Add to favorites", Icon = new FontIcon { Glyph = "\uEB52" } };
            favorite.Click += (_, _) => { _store.ToggleFavorite(item.Path); if (_source == "favorites") _ = RefreshAsync(); };
            menu.Items.Add(favorite);
        }
        var remove = new MenuFlyoutItem { Text = "Remove", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += async (_, _) =>
        {
            if (_removing) return;
            _removalTask = RemoveAsync(item);
            await _removalTask;
        };
        menu.Items.Add(remove);
        return menu;
    }
    private void ConfigureMenu(FrameworkElement container, VideoLibraryItem item)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            MediaCard.StopHoverPreviews(); menu.Items.Clear();
            var populated = CreateMenu(item);
            while (populated.Items.Count > 0) { var entry = populated.Items[0]; populated.Items.RemoveAt(0); menu.Items.Add(entry); }
        };
        container.ContextFlyout = menu;
    }
    private async Task RemoveAsync(VideoLibraryItem item)
    {
        if (_removing || XamlRoot is null) return;
        _removing = true; MediaCard.StopHoverPreviews();
        bool attempted = false, invalidated = false;
        try
        {
            var content = new StackPanel { Spacing = 16 };
            content.Children.Add(new TextBlock { Text = item.Path, TextWrapping = TextWrapping.Wrap });
            var acknowledge = new CheckBox { Content = new TextBlock { Text = "Send this folder and all files and subfolders inside it to the Recycle Bin.",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 340 } };
            if (item.IsFolder) content.Children.Add(acknowledge);
            else content.Children.Add(new TextBlock { Text = "This video will be sent to the Recycle Bin.", TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = item.IsFolder ? "Remove this folder?" : "Remove this video?",
                Content = content, PrimaryButtonText = "Move to Recycle Bin", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = !item.IsFolder };
            acknowledge.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            acknowledge.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            ReleasePreviews();
            attempted = true;
            bool removed = await ShellRecycle.SendAsync(item.Path, OwnerHandle);
            _library!.Invalidate(); _folderCache.Clear(); _loaded = false;
            invalidated = true;
            if (removed) _store!.ForgetRemovedPath(item.Path);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { ErrorOccurred?.Invoke("Couldn't remove this item. " + ex.Message); }
        finally
        {
            // Cancellation or an error can follow a partial folder operation.
            // Refresh both the shared sidebar tree and the visible collection.
            if (attempted)
            {
                if (!invalidated) { _library!.Invalidate(); _folderCache.Clear(); _loaded = false; }
                if (_active && _activeLifetime is { } lifetime) _ = ReloadTreeAsync(lifetime.Token);
                if (_active && !BusyRing.IsActive) await RefreshAsync();
            }
            _removing = false;
        }
    }
    private async Task ReloadTreeAsync(CancellationToken token)
    {
        try { await _library!.GetAsync(false, token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorOccurred?.Invoke(ex.Message); }
    }
}
