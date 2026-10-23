using Microsoft.UI.Xaml.Controls;
using Player.App.Presentation;

namespace Player.App.Views;

public sealed partial class HomeView
{
    // Session-only browsing history. Playback history is owned by the library store.
    private sealed record Location(string Key, string Title, object? Tag);
    private readonly List<Location> _locations = [];
    private int _locationIndex = -1;
    private bool _restoringLocation;
    internal event Action? NavigationChanged;
    internal bool CanGoBack => _locationIndex > 0;
    internal bool CanGoForward => _locationIndex >= 0 && _locationIndex < _locations.Count - 1;

    private static string LocationKey(object? tag) => tag is SearchLocation search ? "search:" + search.Query
        : tag is SidebarFolder folder ? $"folder:{folder.Section}:{folder.Node.Path}" : tag as string ?? "home";

    private void RememberLocation(Location location)
    {
        if (_restoringLocation) return;
        if (_locationIndex >= 0 && string.Equals(_locations[_locationIndex].Key, location.Key, StringComparison.OrdinalIgnoreCase)) return;
        if (_locationIndex + 1 < _locations.Count)
            _locations.RemoveRange(_locationIndex + 1, _locations.Count - _locationIndex - 1);
        // Live edits update one search destination instead of adding a Back
        // entry for every pause in typing. Forward retains the final query.
        if (_locationIndex >= 0 && _locations[_locationIndex].Tag is SearchLocation && location.Tag is SearchLocation)
        {
            _locations[_locationIndex] = location;
            NavigationChanged?.Invoke();
            return;
        }
        _locations.Add(location);
        if (_locations.Count > 100) _locations.RemoveAt(0);
        _locationIndex = _locations.Count - 1;
        NavigationChanged?.Invoke();
    }

    internal void GoBack() { if (CanGoBack) RestoreLocation(_locationIndex - 1); }
    internal void GoForward() { if (CanGoForward) RestoreLocation(_locationIndex + 1); }

    private void RestoreLocation(int index)
    {
        var location = _locations[index];
        _restoringLocation = true;
        try
        {
            _locationIndex = index;
            NavigationViewItem? item;
            if (location.Key == "settings") item = Navigation.SettingsItem as NavigationViewItem;
            else if (location.Tag is SidebarFolder folder)
            {
                item = _sidebar.FindFolder(folder.Node.Path);
                if (item is not null && !string.Equals(LocationKey(item.Tag), location.Key, StringComparison.OrdinalIgnoreCase)) item = null;
            }
            else item = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(candidate =>
                string.Equals(LocationKey(candidate.Tag), location.Key, StringComparison.OrdinalIgnoreCase));

            // A removed/recreated tree item must not strand history. Browsing still
            // resolves the saved path through the current catalog and exclusions.
            if (item is null)
            {
                Navigation.SelectedItem = null;
                SelectPage(new NavigationViewItem { Content = location.Title, Tag = location.Tag });
            }
            else if (!ReferenceEquals(Navigation.SelectedItem, item)) Navigation.SelectedItem = item;
        }
        finally
        {
            _restoringLocation = false;
            NavigationChanged?.Invoke();
        }
    }
}
