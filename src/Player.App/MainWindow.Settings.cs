using Microsoft.UI.Windowing;
using Player.App.Playback;

namespace Player.App;

public sealed partial class MainWindow
{
    private WindowSettings _windowSettings = new();
    private string? _lastTitleTemplate, _lastTitleMedia, _lastTitlePath;

    internal async Task InitializeWindowSettingsAsync(WindowSettings settings)
    {
        UpdateWindowSettings(settings);
        var memory = await WindowMemoryStore.LoadAsync();
        if (_closing) return;
        _videoSizing.RestoreInitialBounds(memory);
        if (settings.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        if (settings.Fullscreen && AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen) ToggleFullscreen();
    }

    internal void UpdateWindowSettings(WindowSettings settings)
    {
        _windowSettings = settings;
        _videoSizing.Configure(settings);
        PlayerPage.RefreshWindowTitle();
    }

    internal void UpdateWindowTitle(string mediaTitle, string? path)
    {
        if (_lastTitleTemplate == _windowSettings.Title && _lastTitleMedia == mediaTitle && _lastTitlePath == path) return;
        _lastTitleTemplate = _windowSettings.Title; _lastTitleMedia = mediaTitle; _lastTitlePath = path;
        string file = path is null ? "" : Path.GetFileName(path);
        string title = _windowSettings.Title.Replace("${filename}", file, StringComparison.Ordinal)
            .Replace("${media-title}", string.IsNullOrWhiteSpace(mediaTitle) ? file : mediaTitle, StringComparison.Ordinal)
            .Replace("${path}", path ?? "", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(title)) title = "mpv.win";
        if (title.Length > 2048) title = title[..(char.IsHighSurrogate(title[2046]) ? 2046 : 2047)] + "…";
        if (AppWindow.Title != title) AppWindow.Title = title;
    }

    internal async Task SaveWindowMemoryAsync()
    {
        if (_videoSizing.CaptureRestored() is not { } memory) return;
        try { await WindowMemoryStore.SaveAsync(memory with { PositionSaved = _windowSettings.RememberPosition,
            X = _windowSettings.RememberPosition ? memory.X : 0, Y = _windowSettings.RememberPosition ? memory.Y : 0 }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { App.WriteStartupFailure(ex, "Couldn't save window state on exit"); }
    }
}
