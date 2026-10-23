using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Player.App.Presentation;

/// <summary>
/// Fits new media and constrains normal interactive resizing to its display
/// aspect. Maximizing, snapping, and fullscreen keep their native bounds.
/// </summary>
internal sealed partial class VideoWindowSizing : IDisposable
{
    private readonly MainWindow _window;
    private readonly IntPtr _handle;
    private readonly SubclassProc _procedure;
    private double _aspect;
    private int _displayWidth, _displayHeight;
    private uint? _generation;
    private bool _pendingFit, _fitQueued, _disposed;
    private static readonly nuint SubclassId = 1;
    private NativePoint _maxTrack;
    private const double BrowserMinimumWidth = 720, BrowserMinimumHeight = 480;
    internal event Action<int, int>? ClientSizeChanged;

    internal VideoWindowSizing(MainWindow window)
    {
        _window = window;
        _handle = window.Handle;
        _procedure = WindowMessage;
        if (!SetWindowSubclass(_handle, _procedure, SubclassId, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void SetGeometry(int width, int height, int rotation, uint generation, int mediaKind = 0)
    {
        if (_disposed || width <= 0 || height <= 0) return;
        if (Math.Abs(rotation % 180) == 90) (width, height) = (height, width);
        if (_generation == generation && width == _displayWidth && height == _displayHeight && _mediaKind == mediaKind) return;
        _generation = generation;
        _displayWidth = width;
        _displayHeight = height;
        _mediaKind = mediaKind;
        _sourceAspect = (double)width / height;
        UpdateAspectLimit();
        _aspect = Math.Max(_sourceAspect, MinimumAspect);
        _pendingFit = true;
        QueueFit();
    }

    internal void Clear()
    {
        _aspect = 0;
        _generation = null;
        _pendingFit = false;
        EnsureBrowserMinimum();
    }

    private void EnsureBrowserMinimum()
    {
        if (_disposed || _window.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }
            || !TryGetClientSize(out int width, out int height)) return;
        GetMinimumClientSize(out double minWidth, out double minHeight);
        if (width >= minWidth && height >= minHeight) return;
        GetBorders(out int borderWidth, out int borderHeight);
        _window.AppWindow.Resize(new SizeInt32(Math.Max(width, (int)Math.Ceiling(minWidth)) + borderWidth,
            Math.Max(height, (int)Math.Ceiling(minHeight)) + borderHeight));
    }

    internal void QueueFit()
    {
        if (_disposed || !_pendingFit || _fitQueued) return;
        _fitQueued = true;
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            _fitQueued = false;
            if (_disposed || !_pendingFit || _aspect <= 0 ||
                _window.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
            var app = _window.AppWindow;
            var display = TargetDisplay();
            MoveOntoDisplay(display);
            UpdateAspectLimit();
            _aspect = Math.Max(_sourceAspect, MinimumAspect);
            var work = ScreenWorkArea(display);
            GetBorders(out int borderWidth, out int borderHeight);
            double maxWidth = Math.Max(1, work.Width * _settings.AutoFitLarger / 100 - borderWidth);
            double maxHeight = Math.Max(1, work.Height * _settings.AutoFitLarger / 100 - borderHeight);
            GetMinimumClientSize(out double minWidth, out double minHeight);
            var size = FittedSize(work, maxWidth, maxHeight, minWidth, minHeight);
            _pendingFit = false;
            // ResizeClient adds the standard caption allowance on this custom
            // extended-title HWND (31 extra client pixels at 96 DPI). Use the
            // actual non-client extents, as interactive WM_SIZING already does.
            app.Resize(new SizeInt32(size.Width + borderWidth, size.Height + borderHeight));
            // Resizing keeps the current position when it fits on this monitor.
            int x = _settings.RememberPosition ? app.Position.X : work.X + (int)Math.Round(Math.Max(0, work.Width - app.Size.Width) * _settings.PositionX / 100);
            int y = _settings.RememberPosition ? app.Position.Y : work.Y + (int)Math.Round(Math.Max(0, work.Height - app.Size.Height) * _settings.PositionY / 100);
            x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - app.Size.Width));
            y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - app.Size.Height));
            if (x != app.Position.X || y != app.Position.Y) app.Move(new PointInt32(x, y));
            CaptureRestored();
        });
    }

    private nint WindowMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        const uint WmSize = 0x0005, WmSizing = 0x0214, WmGetMinMaxInfo = 0x0024, WmMoving = 0x0216, WmExitSizeMove = 0x0232;
        if (!_disposed && message == WmMoving && _settings.SnapEdges) SnapMovingWindow(lParam);
        if (!_disposed && message == WmExitSizeMove) CaptureRestored();
        if (!_disposed && message == WmSizing && _aspect > 0 && (_settings.KeepAspect || MinimumAspect > 0) && wParam is >= 1 and <= 8 &&
            _window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            var rect = Marshal.PtrToStructure<NativeRect>(lParam);
            GetBorders(out int borderWidth, out int borderHeight);
            GetMinimumClientSize(out double minWidth, out double minHeight);
            double maxWidth = Math.Max(minWidth, _maxTrack.X > 0 ? _maxTrack.X - borderWidth : 16384);
            double maxHeight = Math.Max(minHeight, _maxTrack.Y > 0 ? _maxTrack.Y - borderHeight : 16384);
            double width, height;
            if (!_settings.KeepAspect)
            {
                if ((double)(rect.Width - borderWidth) / Math.Max(1, rect.Height - borderHeight) >= MinimumAspect)
                    return DefSubclassProc(window, message, wParam, lParam);
                double ratio = MinimumAspect;
                int adjustedWidth = (int)Math.Round((rect.Height - borderHeight) * ratio) + borderWidth;
                if (wParam is 1 or 4 or 7) rect.Left = rect.Right - adjustedWidth;
                else rect.Right = rect.Left + adjustedWidth;
                Marshal.StructureToPtr(rect, lParam, false); return 1;
            }
            // Like mpv's handle_sizing: top/bottom edges use height; left/right
            // and all corners use width. Never switch axes based on the last
            // resized rectangle: that feeds corrections back into the drag.
            if (wParam is 3 or 6)
            {
                height = Math.Clamp(rect.Height - borderHeight, minHeight, Math.Max(minHeight, Math.Min(maxHeight, maxWidth / _aspect)));
                width = height * _aspect;
            }
            else
            {
                width = Math.Clamp(rect.Width - borderWidth, minWidth, Math.Max(minWidth, Math.Min(maxWidth, maxHeight * _aspect)));
                height = width / _aspect;
            }
            int outerWidth = (int)Math.Round(width) + borderWidth;
            int outerHeight = (int)Math.Round(height) + borderHeight;
            if (wParam is 1 or 4 or 7) rect.Left = rect.Right - outerWidth;
            else rect.Right = rect.Left + outerWidth;
            if (wParam is 3 or 4 or 5) rect.Top = rect.Bottom - outerHeight;
            else rect.Bottom = rect.Top + outerHeight;
            Marshal.StructureToPtr(rect, lParam, false);
            return 1;
        }
        nint result = DefSubclassProc(window, message, wParam, lParam);
        if (!_disposed && message == WmGetMinMaxInfo)
        {
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            _maxTrack = limits.MaxTrack;
            GetBorders(out int borderWidth, out int borderHeight);
            GetMinimumClientSize(out double minWidth, out double minHeight);
            limits.MinTrack.X = Math.Max(limits.MinTrack.X, (int)Math.Ceiling(minWidth) + borderWidth);
            limits.MinTrack.Y = Math.Max(limits.MinTrack.Y, (int)Math.Ceiling(minHeight) + borderHeight);
            Marshal.StructureToPtr(limits, lParam, false);
        }
        if (!_disposed && message == WmSize && wParam != 1)
        {
            if (TryGetClientSize(out int width, out int height)) ClientSizeChanged?.Invoke(width, height);
            if (wParam == 0) QueueFit();
        }
        return result;
    }

    internal bool TryGetClientSize(out int width, out int height)
    {
        bool valid = GetClientRect(_handle, out var client);
        width = client.Width; height = client.Height;
        return valid && width > 0 && height > 0;
    }
    private void GetBorders(out int width, out int height)
    {
        GetWindowRect(_handle, out var outer);
        GetClientRect(_handle, out var client);
        width = Math.Max(0, outer.Width - client.Width);
        height = Math.Max(0, outer.Height - client.Height);
    }
    private void GetMinimumClientSize(out double width, out double height)
    {
        double scale = Math.Max(1, GetDpiForWindow(_handle)) / 96.0;
        // Browser chrome and cards need more space than the edge-to-edge player.
        // WM_GETMINMAXINFO applies this before Windows accepts a smaller drag.
        width = (_aspect > 0 ? 480 : BrowserMinimumWidth) * scale;
        height = (_aspect > 0 ? 270 : BrowserMinimumHeight) * scale;
        if (_aspect > 0 && _settings.KeepAspect)
        {
            width = Math.Max(width, height * _aspect);
            height = width / _aspect;
        }
        else if (_aspect > 0 && MinimumAspect > 0) width = Math.Max(width, height * MinimumAspect);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveWindowSubclass(_handle, _procedure, SubclassId);
        GC.KeepAlive(_procedure);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { internal NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
