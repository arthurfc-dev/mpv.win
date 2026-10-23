namespace Player.App.Library;

internal sealed class VideoFolderNode(string path)
{
    internal string Path { get; } = path;
    internal string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
    internal SortedDictionary<string, VideoFolderNode> Children { get; } = new(StringComparer.CurrentCultureIgnoreCase);
    internal int DirectVideoCount { get; set; }
    internal int TotalVideoCount { get; set; }
}
internal sealed record LibraryScanProgress(string[] Files, int Total, int Skipped);
internal sealed record LibraryVideoSnapshot(IReadOnlyList<string> Files, IReadOnlyList<VideoFolderNode> Roots, int Skipped,
    IReadOnlyDictionary<string, VideoFolderNode> Folders, IReadOnlySet<string> ExistingRoots, IReadOnlyList<string> DefaultFiles);

// Shared, session-only catalog. Views cancel their wait independently; the shell
// cancels disk work when browsing is hidden. No watcher or recurring scan.
internal sealed class LibraryVideoCatalog
{
    private CancellationTokenSource? _scan;
    private Task<LibraryVideoSnapshot>? _pending;
    private int _generation;
    internal LibraryVideoSnapshot? Snapshot { get; private set; }
    internal IReadOnlyList<string> Roots { get; private set; } = [];
    internal IReadOnlyList<string> Excluded { get; private set; } = [];
    internal IReadOnlyList<string> DefaultRoots { get; private set; } = [];
    internal IReadOnlyList<string> DisplayRoots => Roots.Where(root => !Excluded.Any(folder => IsWithin(root, folder)))
        .Where(root => !Roots.Any(other => !root.Equals(other, StringComparison.OrdinalIgnoreCase) && IsWithin(root, other))).ToArray();
    internal List<string> Discovered { get; } = [];
    internal event Action<LibraryScanProgress>? Progress;
    internal event Action<LibraryVideoSnapshot>? Completed;
    internal event Action? ScanStarted;

    internal static bool IsWithin(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal bool Configure(IEnumerable<string> roots, IEnumerable<string> excluded, IEnumerable<string> defaultRoots)
    {
        var nextRoots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var nextExcluded = excluded.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var nextDefaults = defaultRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (Roots.SequenceEqual(nextRoots, StringComparer.OrdinalIgnoreCase) && Excluded.SequenceEqual(nextExcluded, StringComparer.OrdinalIgnoreCase)
            && DefaultRoots.SequenceEqual(nextDefaults, StringComparer.OrdinalIgnoreCase)) return false;
        Cancel(); Snapshot = null; Discovered.Clear();
        Roots = nextRoots; Excluded = nextExcluded; DefaultRoots = nextDefaults;
        return true;
    }

    internal Task<LibraryVideoSnapshot> GetAsync(bool refresh, CancellationToken token)
    {
        if (refresh) { Cancel(); Snapshot = null; }
        if (Snapshot is not null) return Task.FromResult(Snapshot);
        if (_pending is null || _pending.IsCompleted || _scan!.IsCancellationRequested)
        {
            _scan?.Dispose(); _scan = new();
            Discovered.Clear();
            int generation = ++_generation;
            ScanStarted?.Invoke();
            var progress = new Progress<LibraryScanProgress>(value =>
            {
                if (generation != _generation || Snapshot is not null) return;
                Discovered.AddRange(value.Files);
                Progress?.Invoke(value);
            });
            _pending = RunAsync(generation, progress, _scan.Token);
        }
        return _pending.WaitAsync(token);
    }
    internal void Cancel() { ++_generation; _scan?.Cancel(); }
    internal void Invalidate() { Cancel(); Snapshot = null; Discovered.Clear(); }
    private async Task<LibraryVideoSnapshot> RunAsync(int generation, IProgress<LibraryScanProgress> progress, CancellationToken token)
    {
        var roots = Roots.ToArray(); var excluded = Excluded.ToArray(); var defaults = DefaultRoots.ToArray();
        var result = await Task.Run(() => Scan(roots, excluded, defaults, progress, token), token);
        token.ThrowIfCancellationRequested();
        if (generation == _generation)
        {
            Snapshot = result;
            Discovered.Clear(); Discovered.AddRange(result.Files);
            Completed?.Invoke(result);
        }
        return result;
    }
    private static LibraryVideoSnapshot Scan(string[] configuredRoots, string[] excluded, string[] defaults, IProgress<LibraryScanProgress> progress, CancellationToken token)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        int skipped = 0;
        // Scan only chosen locations. Overlapping roots contribute each file once.
        var scanRoots = configuredRoots.Where(root => !excluded.Any(folder => IsWithin(root, folder))).ToArray();
        var roots = scanRoots
            .Where(root => !configuredRoots.Any(other => !root.Equals(other, StringComparison.OrdinalIgnoreCase) && IsWithin(root, other)))
            .ToArray();
        var existingRoots = scanRoots.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        skipped += scanRoots.Length - existingRoots.Count;
        foreach (var root in scanRoots.Reverse()) if (existingRoots.Contains(root)) pending.Push(root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (excluded.Any(folder => IsWithin(directory, folder))) continue;
            if (!visited.Add(Path.TrimEndingDirectorySeparator(directory))) continue;
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) == 0) pending.Push(entry.FullName);
                    }
                    else if (VideoFolderCatalog.IsVideoFile(entry) && files.Add(entry.FullName)) batch.Add(entry.FullName);
                    if (batch.Count >= 128)
                    { progress.Report(new(batch.ToArray(), files.Count, skipped)); batch.Clear(); clock.Restart(); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; }
            if (clock.ElapsedMilliseconds >= 350)
            {
                progress.Report(new(batch.ToArray(), files.Count, skipped)); batch.Clear(); clock.Restart();
            }
        }
        if (batch.Count > 0) progress.Report(new(batch.ToArray(), files.Count, skipped));
        var nodes = new Dictionary<string, VideoFolderNode>(StringComparer.OrdinalIgnoreCase);
        VideoFolderNode Node(string path)
        {
            if (nodes.TryGetValue(path, out var existing)) return existing;
            var node = new VideoFolderNode(path); nodes[path] = node;
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
            if (parent is not null && !roots.Contains(path, StringComparer.OrdinalIgnoreCase)) Node(parent).Children[path] = node;
            return node;
        }
        foreach (var root in existingRoots) Node(root);
        foreach (var path in files)
        { token.ThrowIfCancellationRequested(); Node(Path.GetDirectoryName(path)!).DirectVideoCount++; }
        int Count(VideoFolderNode node) => node.TotalVideoCount = node.DirectVideoCount + node.Children.Values.Sum(Count);
        var visibleRoots = roots.Where(nodes.ContainsKey).Select(root => nodes[root]).ToArray();
        foreach (var root in visibleRoots) Count(root);
        var sorted = files.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new(sorted, visibleRoots, skipped, nodes, existingRoots,
            sorted.Where(path => defaults.Any(root => IsWithin(path, root))).ToArray());
    }
}
