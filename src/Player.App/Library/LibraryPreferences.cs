using System.Text.Json;

namespace Player.App.Library;

internal sealed class GalleryPreferences
{
    public string Sort { get; set; } = "Name";
    public bool Descending { get; set; }
    public string Layout { get; set; } = "Rectangle";
    public string Size { get; set; } = "Large";

    internal void Normalize()
    {
        // Migrate the former Cascade and three-size gallery preferences.
        Layout = Layout == "Square" ? "Square" : "Rectangle";
        Size = Size == "Small" ? "Small" : "Large";
    }
}

internal sealed class LibraryPreferences
{
    public List<RecentVideo> RecentVideos { get; set; } = [];
    public List<string> Favorites { get; set; } = [];
    public List<string> PinnedFolders { get; set; } = [];
    public List<string> LibraryFolders { get; set; } = [];
    public List<string> ExcludedFolders { get; set; } = [];
    public Dictionary<string, GalleryPreferences> Views { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// One UI-owned model; immutable JSON snapshots are written serially off-thread.
internal sealed class LibraryPreferencesStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv.win", "library.json");
    private readonly SemaphoreSlim _writes = new(1);
    private Task _pendingWrite = Task.CompletedTask;
    internal LibraryPreferences State { get; private set; } = new();
    internal string? Error { get; private set; }
    internal event Action? Changed;
    internal IReadOnlyList<WindowsMediaFolder> DefaultFolders { get; } = WindowsMediaFolders.Resolve();

    internal async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var state = JsonSerializer.Deserialize<LibraryPreferences>(await File.ReadAllTextAsync(_path));
            if (state is null) throw new JsonException("Empty library preferences.");
            state.Favorites = (state.Favorites ?? []).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            state.PinnedFolders = (state.PinnedFolders ?? []).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            state.LibraryFolders = CleanFolders(state.LibraryFolders);
            state.ExcludedFolders = CleanFolders(state.ExcludedFolders);
            state.RecentVideos = (state.RecentVideos ?? []).Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Path)
                    && Path.IsPathFullyQualified(item.Path))
                .OrderByDescending(item => item.LastPlayed).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Take(100).ToList();
            foreach (var item in state.RecentVideos)
                item.DurationSeconds = double.IsFinite(item.DurationSeconds) ? Math.Clamp(item.DurationSeconds, 0, 1_000_000_000) : 0;
            state.Views = new(state.Views ?? [], StringComparer.OrdinalIgnoreCase);
            State = state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Error = "Couldn't load saved library preferences. " + ex.Message; }
    }

    internal GalleryPreferences ForView(string key)
    {
        if (!State.Views.TryGetValue(key, out var value) || value is null) State.Views[key] = value = new();
        value.Normalize();
        return value;
    }
    internal bool IsFavorite(string path) => State.Favorites.Contains(path, StringComparer.OrdinalIgnoreCase);
    internal void RecordPlayed(string path, double duration)
    {
        if (!Path.IsPathFullyQualified(path)) return;
        State.RecentVideos.RemoveAll(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        State.RecentVideos.Insert(0, new RecentVideo { Path = path, LastPlayed = DateTimeOffset.UtcNow,
            DurationSeconds = double.IsFinite(duration) ? Math.Clamp(duration, 0, 1_000_000_000) : 0 });
        if (State.RecentVideos.Count > 100) State.RecentVideos.RemoveRange(100, State.RecentVideos.Count - 100);
        Save();
    }
    internal void ForgetRemovedPath(string path)
    {
        State.RecentVideos.RemoveAll(item => LibraryVideoCatalog.IsWithin(item.Path, path));
        State.Favorites.RemoveAll(item => LibraryVideoCatalog.IsWithin(item, path));
        State.PinnedFolders.RemoveAll(item => LibraryVideoCatalog.IsWithin(item, path));
        State.LibraryFolders.RemoveAll(item => LibraryVideoCatalog.IsWithin(item, path));
        Save();
    }
    internal void UpdateRecentDuration(string path, double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) return;
        var item = State.RecentVideos.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.DurationSeconds > 0) return;
        item.DurationSeconds = Math.Min(duration, 1_000_000_000); Save();
    }
    internal void ToggleFavorite(string path)
    {
        if (IsFavorite(path)) State.Favorites.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        else State.Favorites.Add(path);
        Save();
    }
    internal void Pin(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (State.PinnedFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        State.PinnedFolders.Add(path);
        Save();
    }
    internal void Unpin(string path)
    {
        State.PinnedFolders.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        Save();
    }
    private static List<string> CleanFolders(IEnumerable<string>? paths) => (paths ?? [])
        .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
        .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    internal IReadOnlyList<string> LibraryRoots()
    {
        return CleanFolders(DefaultFolders.OrderBy(folder => folder.Name == "Videos" ? 0 : 1).Select(folder => folder.Path)
            .Concat(State.LibraryFolders));
    }
    internal void AddLibraryFolder(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!State.LibraryFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) State.LibraryFolders.Add(path);
        State.ExcludedFolders.RemoveAll(excluded => string.Equals(excluded, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }
    internal void RemoveLibraryFolder(string path)
    {
        State.LibraryFolders.RemoveAll(folder => string.Equals(folder, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }
    internal void ExcludeFolder(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!State.ExcludedFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) State.ExcludedFolders.Add(path);
        Save();
    }
    internal void RestoreFolder(string path)
    {
        State.ExcludedFolders.RemoveAll(folder => string.Equals(folder, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }
    internal void Save()
    {
        var json = JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true });
        _pendingWrite = WriteAsync(json);
        Changed?.Invoke();
    }
    internal Task FlushAsync() => _pendingWrite;
    private async Task WriteAsync(string json)
    {
        await _writes.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Preserve an unreadable original before the user's first mutation.
                if (Error is not null && File.Exists(_path)) File.Copy(_path, _path + ".backup", true);
                await File.WriteAllTextAsync(_path + ".tmp", json);
                File.Move(_path + ".tmp", _path, true);
            });
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "Changes are available this session, but couldn't be saved. " + ex.Message; }
        finally { _writes.Release(); }
        Changed?.Invoke();
    }
}

internal sealed class RecentVideo
{
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset LastPlayed { get; set; }
    public double DurationSeconds { get; set; }
}
