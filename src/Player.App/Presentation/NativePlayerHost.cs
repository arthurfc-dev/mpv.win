using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Content;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Foundation;

namespace Player.App.Presentation;

/// <summary>
/// Native top-level window with a WinUI content island. Video is composed
/// behind this transparent child UI by the system compositor.
/// </summary>
internal sealed class NativePlayerHost
{
    private readonly WindowProcedure _procedure;
    private readonly DesktopWindowXamlSource _source;
    private bool _navigatingFocus;
    private bool _sizingContent;
    private bool _resizeAgain, _resizeQueued;
    private bool _destroyed;
    private bool _focusRestoreQueued;
    private int _contentWidth, _contentHeight;
    private double _contentScale;
    private XamlRoot? _xamlRoot;
    internal nint Handle { get; }
    internal AppWindow AppWindow { get; }
    internal event Action? CloseRequested;
    internal bool ImmediateVideoLayout { get; set; }

    internal NativePlayerHost(FrameworkElement content)
    {
        _procedure = WindowMessage;
        var module = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = module, ClassName = "PlayerNativeWindow", Cursor = LoadCursor(0, 32512)
        };
        if (RegisterClassEx(ref windowClass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Handle = CreateWindowEx(0x00200000, windowClass.ClassName, "mpv.win", 0x00CF0000,
            250, 80, 1120, 740, 0, 0, module, 0);
        if (Handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        AppWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(Handle));
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        _source = new DesktopWindowXamlSource();
        _source.Initialize(AppWindow.Id);
        // Parent-size notifications can leave the child UI/layout one resize
        // behind inside the OS sizing loop. Publish its bounds in WM_SIZE.
        _source.SiteBridge.ResizePolicy = ContentSizePolicy.None;
        content.Loaded += OnContentLoaded;
        _source.Content = content;
        // All focusable content lives in this island: wrap Tab/Shift+Tab
        // instead of leaving focus on the native host with no control selected.
        _source.TakeFocusRequested += (_, args) =>
        {
            if (args.Request.Reason is XamlSourceFocusNavigationReason.First or XamlSourceFocusNavigationReason.Last)
                NavigateFocus(args.Request.Reason);
        };
        ResizeContent(Handle);
        _source.SiteBridge.Show();
    }

    private void OnContentLoaded(object sender, RoutedEventArgs args)
    {
        var content = (FrameworkElement)_source.Content;
        content.Loaded -= OnContentLoaded;
        _xamlRoot = content.XamlRoot;
        _xamlRoot.Changed += OnXamlRootChanged;
        ResizeContent(Handle);
    }
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeContent(Handle);

    private void ResizeContent(nint window)
    {
        if (_sizingContent) { _resizeAgain = true; return; }
        if (_destroyed || _source?.Content is not FrameworkElement content ||
            !GetClientRect(window, out var client) || client.Right <= 0 || client.Bottom <= 0) return;
        double scale = content.XamlRoot?.RasterizationScale ?? GetDpiForWindow(window) / 96.0;
        if (_contentWidth == client.Right && _contentHeight == client.Bottom && _contentScale == scale) return;
        _sizingContent = true;
        try
        {
            _source.SiteBridge.MoveAndResize(new RectInt32(0, 0, client.Right, client.Bottom));
            double width = client.Right / scale, height = client.Bottom / scale;
            content.Width = width;
            content.Height = height;
            // Galleries use WinUI's scheduled layout pass. Forcing the entire
            // virtualized navigation/gallery tree through Measure + Arrange +
            // UpdateLayout on every WM_SIZE caused repeated synchronous work.
            // Preserve the previously validated immediate video-overlay path.
            if (ImmediateVideoLayout || _contentWidth == 0 || _contentHeight == 0)
            {
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
            }
            _contentWidth = client.Right;
            _contentHeight = client.Bottom;
            _contentScale = scale;
        }
        finally
        {
            _sizingContent = false;
            if (_resizeAgain && !_resizeQueued)
            {
                _resizeAgain = false; _resizeQueued = true;
                content.DispatcherQueue.TryEnqueue(() => { _resizeQueued = false; ResizeContent(Handle); });
            }
        }
    }

    internal void Activate()
    {
        ShowWindow(Handle, IsIconic(Handle) ? 9 : 5);
        SetForegroundWindow(Handle);
        ((FrameworkElement)_source.Content).DispatcherQueue.TryEnqueue(() => NavigateFocus(XamlSourceFocusNavigationReason.First));
    }
    private void NavigateFocus(XamlSourceFocusNavigationReason reason)
    {
        if (_navigatingFocus) return;
        _navigatingFocus = true;
        try { _source.NavigateFocus(new XamlSourceFocusNavigationRequest(reason)); }
        finally { _navigatingFocus = false; }
    }
    private void RestoreIslandFocus()
    {
        if (_destroyed || _source is null) return;
        // HasFocus describes the island's logical focus and can remain true
        // while the native host owns keyboard focus after a task switch.
        NavigateFocus(XamlSourceFocusNavigationReason.Restore);
        if (GetFocus() == Handle) NavigateFocus(XamlSourceFocusNavigationReason.First);
    }
    private void QueueFocusRestore()
    {
        if (_destroyed || _focusRestoreQueued || _source?.Content is not FrameworkElement content) return;
        _focusRestoreQueued = content.DispatcherQueue.TryEnqueue(() =>
        {
            _focusRestoreQueued = false;
            if (_destroyed || GetForegroundWindow() != Handle) return;
            // Do not disturb an already focused control or an active popup.
            nint focus = GetFocus();
            if (focus == 0 || focus == Handle) RestoreIslandFocus();
        });
    }
    internal void Destroy() => DestroyWindow(Handle);
    internal SystemBackdrop? Backdrop { get => _source.SystemBackdrop; set => _source.SystemBackdrop = value; }
    internal void SetDragHeight(double height, double scale, double interactiveWidth)
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) return;
        int left = Math.Max(0, (int)Math.Ceiling(interactiveWidth * scale));
        int width = Math.Max(0, AppWindow.ClientSize.Width - AppWindow.TitleBar.RightInset - left);
        // Leave title navigation buttons in the client hit-test area for clicks.
        AppWindow.TitleBar.SetDragRectangles(width > 0
            ? [new RectInt32(left, 0, width, Math.Max(1, (int)Math.Round(height * scale)))] : []);
    }

    private nint WindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0005 && wParam != 1) ResizeContent(window);
        if (message == 0x0014) return 1; // No GDI erase: composition owns the client.
        if (message == 0x0006 && (wParam & 0xffff) != 0 && (wParam >> 16) == 0)
            QueueFocusRestore(); // WM_ACTIVATE: after Windows completes activation.
        if (message == 0x0007 && _source is not null)
        {
            RestoreIslandFocus();
            return 0;
        }
        if (message == 0x0010 && CloseRequested is not null) { CloseRequested.Invoke(); return 0; }
        if (message == 0x0002)
        {
            _destroyed = true;
            if (_xamlRoot is not null) _xamlRoot.Changed -= OnXamlRootChanged;
            _source?.Dispose();
            Application.Current.Exit();
            return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size, Style;
        internal nint Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern nint LoadCursor(nint instance, nint id);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}
