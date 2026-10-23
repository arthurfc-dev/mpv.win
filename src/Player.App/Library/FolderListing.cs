namespace Player.App.Library;

internal sealed record FolderListing(VideoFolderNode Root, IReadOnlyList<string> Files, int Skipped)
{
    internal static async Task<FolderListing> LoadAsync(string path, IReadOnlyList<string> excluded, CancellationToken token)
    {
        var result = await VideoFolderCatalog.ScanAsync(token, path, true, excluded);
        return await Task.Run(() => Create(path, result.Files, result.SkippedFolders, token), token);
    }

    internal static FolderListing Create(string path, IReadOnlyList<string> files, int skipped, CancellationToken token)
    {
        var nodes = new Dictionary<string, VideoFolderNode>(StringComparer.OrdinalIgnoreCase);
        VideoFolderNode Node(string location)
        {
            if (nodes.TryGetValue(location, out var existing)) return existing;
            var node = new VideoFolderNode(location); nodes.Add(location, node);
            if (!location.Equals(path, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(location) is { } parent)
                Node(parent).Children[location] = node;
            return node;
        }
        var root = Node(path);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (LibraryVideoCatalog.IsWithin(file, path)) Node(Path.GetDirectoryName(file)!).DirectVideoCount++;
        }
        int Count(VideoFolderNode node) => node.TotalVideoCount = node.DirectVideoCount + node.Children.Values.Sum(Count);
        Count(root);
        return new(root, files, skipped);
    }
}
