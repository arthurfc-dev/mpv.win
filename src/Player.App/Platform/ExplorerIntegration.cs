using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Player.App.Library;

namespace Player.App.Platform;

internal static class ExplorerIntegration
{
    private const string Capabilities = @"Software\mpv.win\Capabilities";
    private const string Registration = @"Software\mpv.win\Explorer";
    private static string Executable => Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("mpv.win's executable path is unavailable."));
    private static string OpenCommand(string exe) => $"\"{exe}\" \"%1\"";
    private static string ProgId(string extension) => "mpv.win.Video" + extension.ToLowerInvariant();

    internal static void Register()
    {
        string exe = Executable;
        using var capabilities = Registry.CurrentUser.CreateSubKey(Capabilities);
        capabilities.SetValue("ApplicationName", "mpv.win");
        capabilities.SetValue("ApplicationDescription", "Play local videos with mpv.win.");
        capabilities.SetValue("ApplicationIcon", $"\"{exe}\",0");
        using var associations = capabilities.CreateSubKey("FileAssociations");
        foreach (string extension in VideoFolderCatalog.Extensions.Order(StringComparer.Ordinal))
        {
            string id = ProgId(extension);
            using var type = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + id);
            type.SetValue("", "mpv.win video");
            type.SetValue("FriendlyTypeName", "mpv.win video");
            using (var application = type.CreateSubKey("Application"))
            {
                application.SetValue("ApplicationName", "mpv.win");
                application.SetValue("ApplicationDescription", "mpv.win media player");
                application.SetValue("ApplicationIcon", $"\"{exe}\",0");
            }
            using (var icon = type.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var verb = type.CreateSubKey(@"shell\open"))
            {
                verb.SetValue("FriendlyAppName", "mpv.win");
                verb.SetValue("MultiSelectModel", "Single");
                using var command = verb.CreateSubKey("command");
                command.SetValue("", OpenCommand(exe));
            }
            using var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
            openWith.SetValue(id, Array.Empty<byte>(), RegistryValueKind.None);
            associations.SetValue(extension, id);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) registered.SetValue("mpv.win", Capabilities);
        using (var registration = Registry.CurrentUser.CreateSubKey(Registration))
        {
            registration.SetValue("Executable", exe);
            registration.SetValue("Extensions", VideoFolderCatalog.Extensions.Order(StringComparer.Ordinal).ToArray(), RegistryValueKind.MultiString);
        }
        NotifyShell();
    }

    internal static void Unregister()
    {
        using var registration = Registry.CurrentUser.OpenSubKey(Registration);
        if (registration is null || !string.Equals(registration.GetValue("Executable") as string, Executable, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This copy of mpv.win does not own the Explorer registration. Remove it using the registered copy.");
        string[] extensions = registration.GetValue("Extensions") as string[] ?? [];
        foreach (string extension in extensions)
        {
            // Registry data is bounded to our known file types before deletion.
            if (!VideoFolderCatalog.Extensions.Contains(extension)) continue;
            string id = ProgId(extension);
            string root = @"Software\Classes\" + id;
            using (var command = Registry.CurrentUser.OpenSubKey(root + @"\shell\open\command"))
                if (!string.Equals(command?.GetValue("") as string, OpenCommand(Executable), StringComparison.OrdinalIgnoreCase)) continue;
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            using var openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", writable: true);
            openWith?.DeleteValue(id, throwOnMissingValue: false);
        }
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            if (registered?.GetValue("mpv.win") as string == Capabilities) registered.DeleteValue("mpv.win", false);
        Registry.CurrentUser.DeleteSubKeyTree(Capabilities, false);
        Registry.CurrentUser.DeleteSubKeyTree(Registration, false);
        NotifyShell();
    }

    internal static void OpenDefaultApps()
    {
        // The per-app Settings link requires our RegisteredApplications entry.
        // Register this copy as a candidate; Windows still owns the user's defaults.
        Register();
        string uri = "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString("mpv.win");
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint first, nint second);
}
