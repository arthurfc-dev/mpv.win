using System.Runtime.InteropServices;

namespace Player.App.Library;

internal sealed record WindowsMediaFolder(string Name, string Path, string Glyph);

internal static class WindowsMediaFolders
{
    // The six personal locations shown by Windows, excluding profile/AppData,
    // installed applications and drive roots. DONT_VERIFY never creates folders.
    internal static IReadOnlyList<WindowsMediaFolder> Resolve()
    {
        (string Name, string Id, string Glyph)[] definitions =
        [
            ("Desktop", "B4BFCC3A-DB2C-424C-B029-7FE99A87C641", "\uE7F4"),
            ("Documents", "FDD39AD0-238F-46AF-ADB4-6C85480369C7", "\uE8A5"),
            ("Downloads", "374DE290-123F-4565-9164-39C4925E467B", "\uE896"),
            ("Music", "4BD8D571-6D19-48D3-BE97-422220080E43", "\uE8D6"),
            ("Pictures", "33E28130-4E1E-4676-835A-98395C3BC3BB", "\uE8B9"),
            ("Videos", "18989B1D-99B5-455B-841C-AB7C74E4DDFC", "\uE714")
        ];
        var result = new List<WindowsMediaFolder>();
        foreach (var definition in definitions)
        {
            Guid id = new(definition.Id);
            nint memory = 0;
            try
            {
                if (SHGetKnownFolderPath(in id, 0x4000, 0, out memory) < 0) continue;
                string? path = Marshal.PtrToStringUni(memory);
                if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) continue;
                result.Add(new(definition.Name, System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)), definition.Glyph));
            }
            finally { if (memory != 0) Marshal.FreeCoTaskMem(memory); }
        }
        return result;
    }
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
