using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Player.App.Playback;
using Windows.Graphics;

namespace Player.App.Presentation;

internal sealed partial class VideoWindowSizing
{
    private WindowSettings _settings = new();
    private WindowMemory? _restoredMemory, _mediaMemory;
    private double _sourceAspect;
    private int _mediaKind;
    private double _largestWindowAspect = 10;
    private double MinimumAspect => Math.Min(_largestWindowAspect, _mediaKind == 1 ? _settings.MinimumAudioAspect : _settings.MinimumAspect);

    internal void Configure(WindowSettings settings)
    {
        _settings = settings;
        UpdateAspectLimit();
        if (_aspect > 0) _aspect = Math.Max(_sourceAspect, MinimumAspect);
    }
    private DisplayArea TargetDisplay()
    {
        var displays = DisplayArea.FindAll();
        return _settings.Screen >= 0 && _settings.Screen < displays.Count ? displays[_settings.Screen]
            : DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Nearest);
    }
    private static RectInt32 ScreenWorkArea(DisplayArea display)
    {
        // WorkArea is display-relative; AppWindow.Move and WM_MOVING use screen coordinates.
        var work = display.WorkArea;
        work.X += display.OuterBounds.X; work.Y += display.OuterBounds.Y;
        return work;
    }
    private void UpdateAspectLimit()
    {
        var work = ScreenWorkArea(TargetDisplay());
        GetBorders(out int borderWidth, out _);
        double dpi = Math.Max(1, GetDpiForWindow(_handle)) / 96.0;
        _largestWindowAspect = Math.Max(.1, (work.Width - borderWidth) / (270 * dpi));
    }
    private void MoveOntoDisplay(DisplayArea display)
    {
        var app = _window.AppWindow;
        if (DisplayArea.GetFromWindowId(app.Id, DisplayAreaFallback.Nearest).DisplayId.Value == display.DisplayId.Value) return;
        var work = ScreenWorkArea(display);
        app.Move(new PointInt32(Math.Clamp(app.Position.X, work.X, work.X + Math.Max(0, work.Width - app.Size.Width)),
            Math.Clamp(app.Position.Y, work.Y, work.Y + Math.Max(0, work.Height - app.Size.Height))));
    }
    internal void RestoreInitialBounds(WindowMemory? memory)
    {
        var app = _window.AppWindow;
        if (_settings.StartSize.EndsWith("persistent", StringComparison.Ordinal)) _mediaMemory = memory;
        if (memory is { PositionSaved: true } && _settings.RememberPosition && _settings.Screen < 0)
        {
            var area = ScreenWorkArea(DisplayArea.GetFromRect(new RectInt32(memory.X, memory.Y, 1, 1), DisplayAreaFallback.Nearest));
            app.Move(new PointInt32(Math.Clamp(memory.X, area.X, area.X + Math.Max(0, area.Width - app.Size.Width)),
                Math.Clamp(memory.Y, area.Y, area.Y + Math.Max(0, area.Height - app.Size.Height))));
        }
        var display = TargetDisplay();
        MoveOntoDisplay(display);
        var work = ScreenWorkArea(display);
        GetBorders(out int borderWidth, out int borderHeight);
        double dpi = GetDpiForWindow(_handle) / 96.0;
        if (_mediaMemory is not null)
            app.Resize(new SizeInt32(Math.Clamp(_settings.StartSize.StartsWith("size-", StringComparison.Ordinal) ? (int)Math.Round(_mediaMemory.Width * dpi) + borderWidth : app.Size.Width, (int)(BrowserMinimumWidth * dpi) + borderWidth, Math.Max((int)(BrowserMinimumWidth * dpi) + borderWidth, work.Width)),
                Math.Clamp((int)Math.Round(_mediaMemory.Height * dpi) + borderHeight, (int)(BrowserMinimumHeight * dpi) + borderHeight, Math.Max((int)(BrowserMinimumHeight * dpi) + borderHeight, work.Height))));
        int x = memory is { PositionSaved: true } && _settings.RememberPosition ? memory.X : work.X + (int)Math.Round(Math.Max(0, work.Width - app.Size.Width) * _settings.PositionX / 100);
        int y = memory is { PositionSaved: true } && _settings.RememberPosition ? memory.Y : work.Y + (int)Math.Round(Math.Max(0, work.Height - app.Size.Height) * _settings.PositionY / 100);
        app.Move(new PointInt32(Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - app.Size.Width)), Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - app.Size.Height))));
        CaptureRestored();
    }
    private SizeInt32 FittedSize(RectInt32 work, double maxWidth, double maxHeight, double minWidth, double minHeight)
    {
        // Initial-fit percentages must not make ordinary playback controls smaller
        // than the established native minimum window size.
        maxWidth = Math.Max(minWidth, maxWidth); maxHeight = Math.Max(minHeight, maxHeight);
        double dpi = GetDpiForWindow(_handle) / 96.0;
        double percent = _mediaKind == 1 ? _settings.AutoFitAudio : _mediaKind == 2 ? _settings.AutoFitImage : _settings.AutoFit;
        double height = percent > 0 ? work.Height * percent / 100 : _mediaKind == 1 && TryGetClientSize(out _, out int currentHeight) ? currentHeight : _displayHeight;
        double width = height * _aspect;
        if (_settings.StartSize != "fit" && _mediaMemory is { } memory)
        {
            height = memory.Height * dpi;
            width = _settings.StartSize.StartsWith("size-", StringComparison.Ordinal) ? memory.Width * dpi : height * _aspect;
        }
        double grow = Math.Max(1, Math.Max(minWidth / width, Math.Max(minHeight, work.Height * _settings.AutoFitSmaller / 100) / height));
        width *= grow; height *= grow;
        double shrink = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        return new SizeInt32(Math.Max(1, (int)Math.Round(width * shrink)), Math.Max(1, (int)Math.Round(height * shrink)));
    }
    internal WindowMemory? CaptureRestored()
    {
        var app = _window.AppWindow;
        if (app.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored }
            && TryGetClientSize(out int width, out int height))
        {
            double dpi = GetDpiForWindow(_handle) / 96.0;
            _restoredMemory = new(width / dpi, height / dpi, app.Position.X, app.Position.Y);
            if (_generation is not null && !_pendingFit) _mediaMemory = _restoredMemory;
        }
        if (_restoredMemory is null) return _mediaMemory;
        return _mediaMemory is { } media ? media with { X = _restoredMemory.X, Y = _restoredMemory.Y } : _restoredMemory;
    }
    private void SnapMovingWindow(nint lParam)
    {
        if (_window.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
        var rect = Marshal.PtrToStructure<NativeRect>(lParam);
        var work = ScreenWorkArea(DisplayArea.GetFromRect(new RectInt32(rect.Left, rect.Top, rect.Width, rect.Height), DisplayAreaFallback.Nearest));
        int threshold = (int)Math.Round(12 * GetDpiForWindow(_handle) / 96.0);
        int x = rect.Left, y = rect.Top;
        if (Math.Abs(rect.Left - work.X) <= threshold) x = work.X;
        else if (Math.Abs(rect.Right - work.X - work.Width) <= threshold) x = work.X + work.Width - rect.Width;
        if (Math.Abs(rect.Top - work.Y) <= threshold) y = work.Y;
        else if (Math.Abs(rect.Bottom - work.Y - work.Height) <= threshold) y = work.Y + work.Height - rect.Height;
        int width = rect.Width, height = rect.Height;
        rect.Left = x; rect.Top = y; rect.Right = x + width; rect.Bottom = y + height;
        Marshal.StructureToPtr(rect, lParam, false);
    }
}
