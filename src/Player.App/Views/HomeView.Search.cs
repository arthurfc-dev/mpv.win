using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Player.App.Views;

public sealed partial class HomeView
{
    private sealed record SearchLocation(string Query);
    private DispatcherQueueTimer? _searchTimer;

    private void InitializeLibrarySearch()
    {
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(250);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => SearchLibrary(MediaSearch.Text);
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || !_preferencesReady) return;
        _searchTimer?.Stop();
        _searchTimer?.Start();
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchTimer?.Stop();
        SearchLibrary(args.QueryText);
    }

    private void SearchLibrary(string text)
    {
        if (!_active || !_preferencesReady) return;
        string query = text;
        if (string.IsNullOrWhiteSpace(query))
        {
            if (_locationIndex >= 0 && _locations[_locationIndex].Tag is SearchLocation)
            {
                if (CanGoBack) GoBack();
                else Navigation.SelectedItem = VideosItem;
            }
            return;
        }
        Navigation.SelectedItem = null;
        SelectPage(new NavigationViewItem { Content = "Search results", Tag = new SearchLocation(query) });
    }

    private void SynchronizeSearchText(string query)
    {
        _searchTimer?.Stop();
        if (MediaSearch.Text != query) MediaSearch.Text = query;
    }
}
