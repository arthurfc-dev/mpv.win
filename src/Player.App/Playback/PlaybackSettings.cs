using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Player.App.Playback;

internal enum PlaybackOption
{
    Speed = 1, PitchCorrection, SubtitleFont, SubtitleSize, SubtitleOutline,
    SubtitleBold, SubtitleItalic, SubtitleColor, SubtitleOutlineColor,
    SubtitlePosition, SubtitleAssOverride, ChapterThreshold, KeepOpen, KeepOpenPause,
    LoopFile, SavePosition, WatchLaterOptions, HighResolutionSeek, TrackAutoSelection,
    LoopPlaylist, AutoCreatePlaylist, StartupVolume, AudioLanguages, AudioFileAuto,
    AudioDevice, AudioExtensions, Mute, HardwareDecoding, VideoExtensions, DitherDepth,
    Deband, HdrComputePeak, ZeroCopy, VideoBackground, Scale, ChromaScale, Downscale,
    CorrectDownscaling, SigmoidUpscaling, SubtitleShadowOffset, SubtitleShadowColor
}

internal sealed record PlaybackSettings
{
    public int Version { get; init; } = 1;
    public bool ShowAdvancedSettings { get; init; }
    public double BackwardSeconds { get; init; } = 5;
    public double ForwardSeconds { get; init; } = 5;
    public bool ExactSeeking { get; init; } = true;
    public double Speed { get; init; } = 1;
    public bool PitchCorrection { get; init; } = true;
    public double ChapterThreshold { get; init; } = -1;
    public string SubtitleFont { get; init; } = "sans-serif";
    public double SubtitleSize { get; init; } = 38;
    public double SubtitleOutline { get; init; } = 1.65;
    public bool SubtitleBold { get; init; }
    public bool SubtitleItalic { get; init; }
    public string SubtitleColor { get; init; } = "#FFFFFF";
    public string SubtitleOutlineColor { get; init; } = "#000000";
    public double SubtitlePosition { get; init; } = 100;
    public bool OverrideAssStyle { get; init; }
    public bool SubtitleShadowEnabled { get; init; }
    public double SubtitleShadowOffset { get; init; } = 2;
    public string SubtitleShadowColor { get; init; } = "#000000";
    public double SubtitleShadowOpacity { get; init; } = 75;
    public string KeepOpen { get; init; } = "yes";
    public bool KeepOpenPause { get; init; } = true;
    public string IdleMode { get; init; } = "yes";
    public string LoopFile { get; init; } = "no";
    public bool SavePosition { get; init; }
    public string WatchLaterOptions { get; init; } = "start";
    public string HighResolutionSeek { get; init; } = "absolute";
    public bool TrackAutoSelection { get; init; } = true;
    public string LoopPlaylist { get; init; } = "no";
    public string AutoCreatePlaylist { get; init; } = "no";
    public string AppTheme { get; init; } = "system";
    public bool UseSystemColors { get; init; } = true;
    public string AccentColor { get; init; } = "#60CDFF";
    public string PlayButtonColor { get; init; } = "#60CDFF";
    public string SeekBarColor { get; init; } = "#60CDFF";
    public string VolumeBarColor { get; init; } = "#60CDFF";
    public double StartupVolume { get; init; } = 100;
    public bool StartupMuted { get; init; }
    public bool RememberVolume { get; init; } = true;
    public string AudioLanguages { get; init; } = "";
    public string AudioFileAuto { get; init; } = "no";
    public string AudioDevice { get; init; } = "auto";
    public bool RememberAudioDevice { get; init; } = true;
    public string AudioExtensions { get; init; } = "aac,ac3,aiff,ape,au,dts,eac3,flac,m4a,mka,mp3,oga,ogg,opus,thd,wav,wma,wv";
    public WindowSettings Window { get; init; } = new();
    public VideoSettings Video { get; init; } = new();

    internal static readonly string[] ResumeOptions = ["start", "speed", "volume", "aid", "sid", "sub-delay", "audio-delay", "sub-pos", "sub-visibility"];
    internal static bool ValidRepeat(string? value, bool playlist = false) => value is "no" or "inf"
        || playlist && value == "force" || int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count is >= 1 and <= 1000000;

    internal string? Validate()
    {
        if (Version != 1) return "These playback settings use an unsupported version.";
        if (Window is null) return "Window settings are missing.";
        if (Window.Validate() is { } windowError) return windowError;
        if (Video is null) return "Video settings are missing.";
        if (Video.Validate() is { } videoError) return videoError;
        if (!double.IsFinite(StartupVolume) || StartupVolume is < 0 or > 100) return "Startup volume must be between 0 and 100.";
        if (AudioFileAuto is not ("no" or "exact" or "fuzzy" or "all")) return "Choose a supported external audio matching mode.";
        if (AudioLanguages is null || AudioLanguages.Length > 127 || !Regex.IsMatch(AudioLanguages, "^([a-zA-Z0-9]+(-[a-zA-Z0-9]+)*(,[a-zA-Z0-9]+(-[a-zA-Z0-9]+)*)*)?$"))
            return "Audio languages must be comma-separated language tags, for example pt-BR,en,ja.";
        if (string.IsNullOrWhiteSpace(AudioDevice) || AudioDevice.Length > 127 || AudioDevice.Any(char.IsControl)) return "Choose a valid audio output device.";
        if (AudioExtensions is null || AudioExtensions.Length > 127 || !Regex.IsMatch(AudioExtensions, "^[a-zA-Z0-9]+(,[a-zA-Z0-9]+)*$"))
            return "Audio extensions must be comma-separated names without dots, up to 127 characters.";
        if (KeepOpen is not ("yes" or "no" or "always") || IdleMode is not ("yes" or "no" or "once")
            || HighResolutionSeek is not ("no" or "absolute" or "yes" or "default")
            || AutoCreatePlaylist is not ("no" or "filter" or "same")) return "Choose a supported playback mode.";
        if (!ValidRepeat(LoopFile) || !ValidRepeat(LoopPlaylist, true)) return "Repeat must be no, inf, or a whole number from 1 to 1000000 (playlist also accepts force).";
        if (WatchLaterOptions is null || WatchLaterOptions.Length > 127 || WatchLaterOptions.Split(',').Any(value => !ResumeOptions.Contains(value)))
            return "Choose supported resume options, separated by commas, including start to remember the position.";
        if (AppTheme is not ("system" or "light" or "dark")) return "Choose System, Light or Dark appearance.";
        foreach (string color in new[] { AccentColor, PlayButtonColor, SeekBarColor, VolumeBarColor })
            if (!Regex.IsMatch(color ?? "", "^#[0-9a-fA-F]{6}$")) return "Interface colors must use #RRGGBB.";
        foreach (var (name, value, min, max) in new[] {
            ("Backward skip", BackwardSeconds, 0.1, 600.0), ("Forward skip", ForwardSeconds, 0.1, 600.0),
            ("Playback speed", Speed, 0.25, 4.0), ("Chapter threshold", ChapterThreshold, -1.0, 60.0),
            ("Subtitle size", SubtitleSize, 1.0, 200.0), ("Subtitle outline", SubtitleOutline, 0.0, 10.0),
            ("Subtitle position", SubtitlePosition, 0.0, 100.0),
            ("Shadow distance", SubtitleShadowOffset, 0.5, 10.0), ("Shadow opacity", SubtitleShadowOpacity, 0.0, 100.0) })
            if (!double.IsFinite(value) || value < min || value > max) return $"{name} must be between {min} and {max}.";
        if (string.IsNullOrWhiteSpace(SubtitleFont) || SubtitleFont.Length > 127 || SubtitleFont.Any(char.IsControl))
            return "Enter a font family name of up to 127 characters.";
        if (!Regex.IsMatch(SubtitleColor ?? "", "^#[0-9a-fA-F]{6}$") || !Regex.IsMatch(SubtitleOutlineColor ?? "", "^#[0-9a-fA-F]{6}$")
            || !Regex.IsMatch(SubtitleShadowColor ?? "", "^#[0-9a-fA-F]{6}$"))
            return "Subtitle colors must use #RRGGBB, for example #FFFFFF.";
        return null;
    }

    internal IEnumerable<(PlaybackOption Option, string Value)> Options()
    {
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        yield return (PlaybackOption.Speed, Number(Speed));
        yield return (PlaybackOption.PitchCorrection, PitchCorrection ? "yes" : "no");
        yield return (PlaybackOption.SubtitleFont, SubtitleFont);
        yield return (PlaybackOption.SubtitleSize, Number(SubtitleSize));
        yield return (PlaybackOption.SubtitleOutline, Number(SubtitleOutline));
        yield return (PlaybackOption.SubtitleBold, SubtitleBold ? "yes" : "no");
        yield return (PlaybackOption.SubtitleItalic, SubtitleItalic ? "yes" : "no");
        yield return (PlaybackOption.SubtitleColor, SubtitleColor);
        yield return (PlaybackOption.SubtitleOutlineColor, SubtitleOutlineColor);
        yield return (PlaybackOption.SubtitlePosition, Number(SubtitlePosition));
        yield return (PlaybackOption.SubtitleAssOverride, OverrideAssStyle ? "force" : "scale");
        yield return (PlaybackOption.SubtitleShadowOffset, Number(SubtitleShadowEnabled ? SubtitleShadowOffset : 0));
        int shadowAlpha = (int)Math.Round(SubtitleShadowOpacity * 255 / 100);
        yield return (PlaybackOption.SubtitleShadowColor, $"#{shadowAlpha:X2}{SubtitleShadowColor[1..]}");
        yield return (PlaybackOption.ChapterThreshold, Number(ChapterThreshold));
        yield return (PlaybackOption.KeepOpen, KeepOpen);
        yield return (PlaybackOption.KeepOpenPause, KeepOpenPause ? "yes" : "no");
        yield return (PlaybackOption.LoopFile, LoopFile);
        yield return (PlaybackOption.SavePosition, SavePosition ? "yes" : "no");
        yield return (PlaybackOption.WatchLaterOptions, WatchLaterOptions);
        yield return (PlaybackOption.HighResolutionSeek, HighResolutionSeek);
        yield return (PlaybackOption.TrackAutoSelection, TrackAutoSelection ? "yes" : "no");
        yield return (PlaybackOption.LoopPlaylist, LoopPlaylist);
        yield return (PlaybackOption.AutoCreatePlaylist, AutoCreatePlaylist);
        yield return (PlaybackOption.StartupVolume, Number(StartupVolume));
        yield return (PlaybackOption.AudioLanguages, AudioLanguages);
        yield return (PlaybackOption.AudioFileAuto, AudioFileAuto);
        yield return (PlaybackOption.AudioDevice, AudioDevice);
        yield return (PlaybackOption.AudioExtensions, AudioExtensions);
        yield return (PlaybackOption.Mute, StartupMuted ? "yes" : "no");
        foreach (var option in Video.Options()) yield return option;
    }
}

internal sealed class PlaybackSettingsStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv.win", "playback-settings.json");
    internal PlaybackSettings State { get; private set; } = new();
    internal string? Warning { get; private set; }
    internal void UseDefaults(string warning) { State = new(); Warning = warning; }
    internal void UseDefaultAudioDevice()
    {
        State = State with { AudioDevice = "auto" };
        Warning = (Warning is null ? "" : Warning + " ") + "The saved audio output is unavailable. Windows default is in use.";
    }
    internal void UseRememberedAudio(RememberedAudioState state)
    {
        State = State with {
            StartupVolume = State.RememberVolume ? state.Volume ?? State.StartupVolume : State.StartupVolume,
            StartupMuted = State.RememberVolume ? state.Muted ?? State.StartupMuted : State.StartupMuted,
            AudioDevice = State.RememberAudioDevice ? state.Device ?? State.AudioDevice : State.AudioDevice };
    }

    internal async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<PlaybackSettings>(await File.ReadAllTextAsync(_path))
                ?? throw new JsonException("The settings file was empty.");
            if (loaded.Validate() is { } error) { Warning = error + " Defaults are in use."; return; }
            State = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Warning = "Couldn't read playback settings. Defaults are in use. " + ex.Message; }
    }

    internal async Task SaveAsync(PlaybackSettings settings)
    {
        if (settings.Validate() is { } error) throw new ArgumentException(error);
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        });
        State = settings;
        Warning = null;
    }
}
