using Microsoft.UI.Xaml.Controls;
using Player.App.Library;

namespace Player.App.Views;

public sealed partial class GalleryToolbar : UserControl
{
    private GalleryPreferences _preferences = new();
    private bool _compact;
    internal event Action? Changed;
    public GalleryToolbar() { InitializeComponent(); SetPreferences(_preferences); }
    internal void SetPreferences(GalleryPreferences preferences)
    {
        _preferences = preferences;
        preferences.Normalize();
        SortMenu.Items.Clear(); LayoutMenu.Items.Clear();
        AddGroup(SortMenu, "sort", [("Date taken", "Taken"), ("Date created", "Created"), ("Date modified", "Modified"), ("Name", "Name")],
            preferences.Sort, value => preferences.Sort = value);
        SortMenu.Items.Add(new MenuFlyoutSeparator());
        AddGroup(SortMenu, "direction", [("Ascending", "Ascending"), ("Descending", "Descending")],
            preferences.Descending ? "Descending" : "Ascending", value => preferences.Descending = value == "Descending");
        AddGroup(LayoutMenu, "layout", [("Square", "Square"), ("Rectangle", "Rectangle")], preferences.Layout, value => preferences.Layout = value);
        LayoutMenu.Items.Add(new MenuFlyoutSeparator());
        AddGroup(LayoutMenu, "size", [("Small", "Small"), ("Large", "Large")], preferences.Size, value => preferences.Size = value);
        UpdateAvailability();
    }
    internal void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        UpdateAvailability();
    }
    private void UpdateAvailability()
    {
        foreach (var item in LayoutMenu.Items.OfType<RadioMenuFlyoutItem>())
        {
            string value = (string)item.Tag;
            item.IsEnabled = !_compact || value is "Square" or "Small";
            item.IsChecked = value == (_compact ? "Square" : _preferences.Layout)
                || value == (_compact ? "Small" : _preferences.Size);
        }
    }
    private void AddGroup(MenuFlyout menu, string group, (string Label, string Value)[] choices, string selected, Action<string> assign)
    {
        // Unique groups prevent one toolbar changing another toolbar's radio state.
        string groupName = Guid.NewGuid() + group;
        foreach (var choice in choices)
        {
            var item = new RadioMenuFlyoutItem { Text = choice.Label, Tag = choice.Value, GroupName = groupName, IsChecked = choice.Value == selected };
            string? glyph = choice.Value switch
            {
                "Taken" => "\uE787", "Created" => "\uE8F4", "Modified" => "\uE70F", "Name" => "\uE8D2",
                "Ascending" => "\uE74A", "Descending" => "\uE74B",
                "Square" => "\uE739", "Rectangle" => "\uE714", "Small" => "\uF0E2", "Large" => "\uE8A9", _ => null
            };
            if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
            item.Click += (_, _) => { assign(choice.Value); Changed?.Invoke(); };
            menu.Items.Add(item);
        }
    }
}
