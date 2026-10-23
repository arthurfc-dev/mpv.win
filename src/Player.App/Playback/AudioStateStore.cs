using System.Text.Json;

namespace Player.App.Playback;

internal sealed record RememberedAudioState(double? Volume, bool? Muted, string? Device);

internal static class AudioStateStore
{
    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv.win", "audio-state.json");

    internal static async Task<RememberedAudioState> LoadAsync()
    {
        try
        {
            if (File.Exists(StatePath) && JsonSerializer.Deserialize<RememberedAudioState>(await File.ReadAllTextAsync(StatePath)) is { } state)
            {
                if (state.Volume is { } volume && (!double.IsFinite(volume) || volume is < 0 or > 100)) return new(null, null, null);
                if (state.Device is { } device && (string.IsNullOrWhiteSpace(device) || device.Length > 127 || device.Any(char.IsControl))) return new(null, null, null);
                return state;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(null, null, null);
    }

    internal static Task SaveAsync(RememberedAudioState state) => Task.Run(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        string temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, StatePath, overwrite: true);
    });
}
