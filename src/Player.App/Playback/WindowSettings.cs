using System.Text.Json;
using System.Text.RegularExpressions;

namespace Player.App.Playback;

internal sealed record WindowSettings
{
    public string Title { get; init; } = "mpv.win";
    public bool Fullscreen { get; init; }
    public bool Maximized { get; init; }
    public int Screen { get; init; } = -1;
    public double AutoFit { get; init; }
    public double AutoFitSmaller { get; init; } = 10;
    public double AutoFitLarger { get; init; } = 90;
    public double AutoFitImage { get; init; } = 80;
    public double AutoFitAudio { get; init; } = 70;
    public double PositionX { get; init; } = 50;
    public double PositionY { get; init; } = 50;
    public string StartSize { get; init; } = "fit";
    public bool KeepAspect { get; init; } = true;
    public double MinimumAspect { get; init; }
    public double MinimumAudioAspect { get; init; }
    public bool RememberPosition { get; init; }
    public bool SnapEdges { get; init; }

    internal string? Validate()
    {
        if (Title is null || Title.Length > 256 || Title.Any(char.IsControl)) return "Window title must be at most 256 characters without line breaks.";
        foreach (Match token in Regex.Matches(Title, @"\$\{([^}]*)\}"))
            if (token.Groups[1].Value is not ("filename" or "media-title" or "path")) return "Window title supports ${filename}, ${media-title} and ${path}.";
        if (Screen is < -1 or > 32) return "Display must be -1 (current) or an index from 0 to 32.";
        foreach (double value in new[] { AutoFit, AutoFitSmaller, AutoFitLarger, AutoFitImage, AutoFitAudio, PositionX, PositionY })
            if (!double.IsFinite(value) || value is < 0 or > 100) return "Window percentages must be between 0 and 100.";
        if (AutoFitLarger <= 0 || AutoFitSmaller > AutoFitLarger) return "Maximum fitting height must be positive and at least the minimum fitting height.";
        if (StartSize is not ("fit" or "height-session" or "size-session" or "height-persistent" or "size-persistent")) return "Choose a supported initial-size policy.";
        foreach (double value in new[] { MinimumAspect, MinimumAudioAspect })
            if (!double.IsFinite(value) || value is < 0 or > 10) return "Minimum aspect ratios must be between 0 and 10 (0 disables the limit).";
        return null;
    }
}

internal sealed record WindowMemory(double Width, double Height, int X, int Y, bool PositionSaved = false);

internal static class WindowMemoryStore
{
    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv.win", "window-state.json");
    internal static async Task<WindowMemory?> LoadAsync()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var state = JsonSerializer.Deserialize<WindowMemory>(await File.ReadAllTextAsync(StatePath));
            return state is not null && double.IsFinite(state.Width) && double.IsFinite(state.Height)
                && state.Width is >= 100 and <= 16384 && state.Height is >= 100 and <= 16384 ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal static Task SaveAsync(WindowMemory state) => Task.Run(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        string temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, StatePath, overwrite: true);
    });
}
