using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;

namespace Player.App.Views;

// Clearing only a section's visible items preserves its header and allows the
// native grouped GridView to recycle cards when that section is collapsed.
public sealed class GallerySection : ObservableCollection<VideoLibraryItem>, INotifyPropertyChanged
{
    public string Key { get; init; } = string.Empty;
    public string Heading { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public string Glyph { get; init; } = "\uE8B7";
    public bool HeaderVisible { get; init; } = true;
    public Visibility HeaderVisibility => HeaderVisible ? Visibility.Visible : Visibility.Collapsed;
    public bool Recent { get; init; }
    public bool Expanded { get; private set; } = true;
    public string Chevron => Expanded ? "\uE70E" : "\uE70D";
    public string ToggleLabel => $"{(Expanded ? "Collapse" : "Expand")} {Heading}";
    internal List<VideoLibraryItem> Entries { get; } = [];
    internal void SetExpanded(bool expanded)
    {
        Expanded = expanded; Items.Clear();
        if (expanded) foreach (var item in Entries) Items.Add(item);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new(nameof(Chevron)));
        OnPropertyChanged(new(nameof(ToggleLabel)));
    }
}
