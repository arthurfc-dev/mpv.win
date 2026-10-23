namespace Player.App.Library;

internal sealed record VideoFolderResult(string Folder, IReadOnlyList<string> Files, int SkippedFolders);

internal static class VideoFolderCatalog
{
    internal static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg",
        ".mpe", ".m2v", ".ts", ".mts", ".m2ts", ".mxf", ".vob", ".ogv", ".ogm",
        ".3gp", ".3g2", ".flv", ".f4v", ".asf", ".divx", ".rm", ".rmvb", ".qt",
        ".y4m", ".hevc", ".h265", ".h264", ".av1", ".ivf"
    };

    internal static bool IsVideoFile(FileSystemInfo entry)
    {
        if (!Extensions.Contains(entry.Extension)) return false;
        if (!entry.Extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)) return true;
        // Selected folders may also contain TypeScript source. Verify MPEG
        // transport sync bytes rather than treating every .ts file as a video.
        try
        {
            using var stream = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[577];
            int count = stream.Read(header);
            for (int offset = 0; offset < Math.Min(192, count); offset++)
                if (header[offset] == 0x47 &&
                    ((offset + 376 < count && header[offset + 188] == 0x47 && header[offset + 376] == 0x47) ||
                     (offset + 384 < count && header[offset + 192] == 0x47 && header[offset + 384] == 0x47))) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    // Resolve Windows' configured location, including a redirected/localized
    // Videos folder. Enumeration and sorting run on a worker, never the UI.
    internal static Task<VideoFolderResult> ScanAsync(CancellationToken cancellation, string? selectedFolder = null, bool recursive = true,
        IReadOnlyList<string>? excluded = null) => Task.Run(() =>
    {
        string root = selectedFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(root)) throw new DirectoryNotFoundException("Windows has no Videos folder configured.");
        var pending = new Stack<string>();
        var files = new List<string>();
        int skipped = 0;
        pending.Push(root);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.TryPop(out string? folder))
        {
            cancellation.ThrowIfCancellationRequested();
            if (excluded?.Any(path => LibraryVideoCatalog.IsWithin(folder, path)) == true) continue;
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        // Do not follow directory junctions/symlinks outside the
                        // selected tree or into a cycle. File links remain listed.
                        if (recursive && (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) == 0) pending.Push(entry.FullName);
                    }
                    else if (IsVideoFile(entry)) files.Add(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)) throw;
                ++skipped;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        files.Sort((a, b) =>
        {
            int byName = StringComparer.CurrentCultureIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b));
            return byName != 0 ? byName : StringComparer.OrdinalIgnoreCase.Compare(a, b);
        });
        return new VideoFolderResult(root, files, skipped);
    }, cancellation);
}
