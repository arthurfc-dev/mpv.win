using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Player.App.Library;

namespace Player.App.Presentation;

internal enum FolderSection { Standard, Custom }
internal sealed record SidebarFolder(VideoFolderNode Node, FolderSection Section, bool IsRoot);

// Keeps the two navigation categories independent of the deduplicated catalog.
internal sealed class LibrarySidebar
{
    private readonly NavigationView _navigation;
    private readonly NavigationViewItem _computer;
    private readonly NavigationViewItemHeader _customHeading, _pinnedHeading;
    private readonly NavigationViewItemSeparator _pinnedSeparator;
    private readonly LibraryPreferencesStore _store;
    private readonly LibraryVideoCatalog _catalog;
    private readonly Dictionary<string, VideoFolderNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NavigationViewItem> _custom = [], _pins = [];
    private readonly HashSet<string> _existingCustom = new(StringComparer.OrdinalIgnoreCase);
    private string[] _customPaths = [];
    private int _scopeVersion;
    internal event Action<NavigationViewItem>? ItemCreated;

    internal LibrarySidebar(NavigationView navigation, NavigationViewItem computer, NavigationViewItemHeader customHeading,
        NavigationViewItemSeparator pinnedSeparator, NavigationViewItemHeader pinnedHeading,
        LibraryPreferencesStore store, LibraryVideoCatalog catalog)
    {
        _navigation = navigation; _computer = computer; _customHeading = customHeading;
        _pinnedSeparator = pinnedSeparator; _pinnedHeading = pinnedHeading; _store = store; _catalog = catalog;
        catalog.ScanStarted += () => _nodes.Clear();
        catalog.Progress += OnProgress;
        catalog.Completed += snapshot =>
        {
            _nodes.Clear();
            foreach (var entry in snapshot.Folders) _nodes.Add(entry.Key, entry.Value);
            _existingCustom.Clear(); _existingCustom.UnionWith(snapshot.ExistingRoots);
            Render(true);
        };
    }

    internal bool Configure()
    {
        RefreshPins();
        bool customChanged = !_customPaths.SequenceEqual(_store.State.LibraryFolders, StringComparer.OrdinalIgnoreCase);
        bool scopeChanged = _catalog.Configure(_store.LibraryRoots(), _store.State.ExcludedFolders, _store.DefaultFolders.Select(folder => folder.Path));
        if (!customChanged && !scopeChanged) return false;
        _customPaths = _store.State.LibraryFolders.ToArray();
        int version = ++_scopeVersion;
        if (scopeChanged)
        {
            _nodes.Clear(); _existingCustom.Clear();
            _computer.MenuItems.Clear();
        }
        Render(true);
        _ = ReadCustomLocationsAsync(version, _customPaths);
        return scopeChanged;
    }

    private async Task ReadCustomLocationsAsync(int version, string[] paths)
    {
        var existing = await Task.Run(() => paths.Where(Directory.Exists).ToArray());
        if (version != _scopeVersion) return;
        if (_catalog.Snapshot is { } snapshot)
        {
            _existingCustom.Clear(); _existingCustom.UnionWith(snapshot.ExistingRoots);
        }
        else { _existingCustom.Clear(); _existingCustom.UnionWith(existing); }
        Render(_catalog.Snapshot is not null);
    }

    private void OnProgress(LibraryScanProgress progress)
    {
        foreach (var file in progress.Files)
        {
            string? path = Path.GetDirectoryName(file);
            if (path is null) continue;
            Node(path).DirectVideoCount++;
            while (path is not null && _nodes.TryGetValue(path, out var node))
            {
                node.TotalVideoCount++;
                if (_catalog.DisplayRoots.Contains(path, StringComparer.OrdinalIgnoreCase)) break;
                path = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
            }
        }
        if (progress.Files.Length > 0) Render(false);
    }

    private VideoFolderNode Node(string path)
    {
        if (_nodes.TryGetValue(path, out var node)) return node;
        node = new(path); _nodes.Add(path, node);
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (parent is not null && !_catalog.DisplayRoots.Contains(path, StringComparer.OrdinalIgnoreCase)) Node(parent).Children[path] = node;
        return node;
    }

    private void Render(bool complete)
    {
        var defaults = _store.DefaultFolders.DistinctBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .Where(folder => _nodes.TryGetValue(folder.Path, out var node) && node.TotalVideoCount > 0).ToArray();
        var visiblePaths = defaults.Select(folder => folder.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (complete)
            foreach (var item in _computer.MenuItems.OfType<NavigationViewItem>().ToArray())
                if (item.Tag is SidebarFolder folder && !visiblePaths.Contains(folder.Node.Path)) _computer.MenuItems.Remove(item);
        foreach (var folder in defaults)
        {
            var node = _nodes[folder.Path];
            var item = _computer.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag is SidebarFolder old
                && old.Node.Path.Equals(node.Path, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = Create(node, FolderSection.Standard, true);
                int index = _store.DefaultFolders.TakeWhile(root => root != folder)
                    .Count(root => _computer.MenuItems.OfType<NavigationViewItem>().Any(existing => existing.Tag is SidebarFolder value
                        && value.Node.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase)));
                _computer.MenuItems.Insert(Math.Min(index, _computer.MenuItems.Count), item);
            }
            item.Content = folder.Name; item.Icon = new FontIcon { Glyph = folder.Glyph };
            Update(item, node, FolderSection.Standard, true, complete);
        }
        _computer.HasUnrealizedChildren = _catalog.Snapshot is null;

        var customPaths = _customPaths.Where(path => _existingCustom.Contains(path)
            && !_catalog.Excluded.Any(excluded => LibraryVideoCatalog.IsWithin(path, excluded))).ToArray();
        foreach (var item in _custom.ToArray())
            if (item.Tag is SidebarFolder folder && !customPaths.Contains(folder.Node.Path, StringComparer.OrdinalIgnoreCase))
            { _custom.Remove(item); _navigation.MenuItems.Remove(item); }
        foreach (var path in customPaths)
        {
            var node = _nodes.GetValueOrDefault(path) ?? new VideoFolderNode(path);
            var item = _custom.FirstOrDefault(item => item.Tag is SidebarFolder folder
                && folder.Node.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = Create(node, FolderSection.Custom, true); _custom.Add(item);
                _navigation.MenuItems.Insert(_navigation.MenuItems.IndexOf(_pinnedSeparator), item);
            }
            Update(item, node, FolderSection.Custom, true, complete);
        }
        _customHeading.Visibility = Visibility.Visible;
    }

    internal void Expand(NavigationViewItem item)
    {
        if (item.Tag is SidebarFolder folder) MergeChildren(item, folder.Node, folder.Section, true);
    }
    internal NavigationViewItem? FindFolder(string path)
    {
        foreach (var item in _navigation.MenuItems.OfType<NavigationViewItem>())
            if (Find(item) is { } found) return found;
        return null;
        NavigationViewItem? Find(NavigationViewItem item)
        {
            if (item.Tag is SidebarFolder folder)
            {
                if (folder.Node.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return item;
                if (!LibraryVideoCatalog.IsWithin(path, folder.Node.Path)) return null;
                Expand(item);
            }
            foreach (var child in item.MenuItems.OfType<NavigationViewItem>())
                if (Find(child) is { } found) { item.IsExpanded = true; return found; }
            return null;
        }
    }
    private void Update(NavigationViewItem item, VideoFolderNode node, FolderSection section, bool root, bool complete)
    {
        item.Tag = new SidebarFolder(node, section, root);
        item.HasUnrealizedChildren = node.Children.Values.Any(child => child.TotalVideoCount > 0);
        if (item.IsExpanded || item.MenuItems.Count > 0) MergeChildren(item, node, section, complete);
    }
    private void MergeChildren(NavigationViewItem parent, VideoFolderNode node, FolderSection section, bool complete)
    {
        var children = node.Children.Values.Where(child => child.TotalVideoCount > 0).ToArray();
        if (complete)
            foreach (var old in parent.MenuItems.OfType<NavigationViewItem>().ToArray())
                if (old.Tag is SidebarFolder folder && !children.Any(child => child.Path.Equals(folder.Node.Path, StringComparison.OrdinalIgnoreCase)))
                    parent.MenuItems.Remove(old);
        foreach (var child in children)
        {
            var item = parent.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag is SidebarFolder old
                && old.Node.Path.Equals(child.Path, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = Create(child, section, false);
                int index = children.TakeWhile(node => node != child).Count(node => parent.MenuItems.OfType<NavigationViewItem>()
                    .Any(existing => existing.Tag is SidebarFolder folder && folder.Node.Path.Equals(node.Path, StringComparison.OrdinalIgnoreCase)));
                parent.MenuItems.Insert(index, item);
            }
            Update(item, child, section, false, complete);
        }
    }
    private NavigationViewItem Create(VideoFolderNode node, FolderSection section, bool root)
    {
        var item = new NavigationViewItem { Content = node.Name, Tag = new SidebarFolder(node, section, root), Icon = new FontIcon { Glyph = "\uE8B7" } };
        ToolTipService.SetToolTip(item, node.Path);
        var menu = new MenuFlyout();
        var pin = new MenuFlyoutItem { Text = "Pin folder", Icon = new FontIcon { Glyph = "\uE718" } };
        pin.Click += (_, _) => _store.Pin(node.Path); menu.Items.Add(pin);
        var exclude = new MenuFlyoutItem { Text = "Exclude from library", Icon = new FontIcon { Glyph = "\uE738" } };
        exclude.Click += (_, _) => _store.ExcludeFolder(node.Path); menu.Items.Add(exclude);
        if (section == FolderSection.Custom && root)
        {
            var remove = new MenuFlyoutItem { Text = "Remove added folder", Icon = new FontIcon { Glyph = "\uE711" } };
            remove.Click += (_, _) => _store.RemoveLibraryFolder(node.Path); menu.Items.Add(remove);
        }
        item.ContextFlyout = menu; ItemCreated?.Invoke(item);
        return item;
    }

    private void RefreshPins()
    {
        foreach (var old in _pins.ToArray())
            if (!_store.State.PinnedFolders.Contains((string)old.Tag, StringComparer.OrdinalIgnoreCase))
            { _pins.Remove(old); _navigation.MenuItems.Remove(old); }
        foreach (var path in _store.State.PinnedFolders)
        {
            if (_pins.Any(item => string.Equals((string)item.Tag, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new NavigationViewItem { Content = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), Tag = path, Icon = new FontIcon { Glyph = "\uE718" } };
            ToolTipService.SetToolTip(item, path);
            var menu = new MenuFlyout(); var unpin = new MenuFlyoutItem { Text = "Unpin folder" };
            unpin.Click += (_, _) =>
            {
                if (ReferenceEquals(_navigation.SelectedItem, item)) _navigation.SelectedItem = _computer;
                _store.Unpin(path);
            };
            menu.Items.Add(unpin); item.ContextFlyout = menu;
            _pins.Add(item); _navigation.MenuItems.Add(item); ItemCreated?.Invoke(item);
        }
        _pinnedSeparator.Visibility = _pinnedHeading.Visibility = _pins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
