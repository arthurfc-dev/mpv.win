using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Player.App.Presentation;
using Microsoft.UI.System;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Player.App;

public sealed partial class MainWindow : UserControl
{
    private bool _closing;
    internal bool IsClosing => _closing;
    internal event Action? Closed;
    private bool _videoMode;
    private bool _chromeVisible = true;
    private bool _canReturnToBrowser;
    private readonly OverlayTransition _titleTransition;
    private readonly UISettings _uiSettings = new();
    private readonly ThemeSettings _themeSettings;
    private readonly VideoWindowSizing _videoSizing;
    private readonly NativePlayerHost _host;
    internal AppWindow AppWindow => _host.AppWindow;
    internal nint Handle => _host.Handle;
    internal bool HighContrast => _themeSettings?.HighContrast ?? false;
    internal bool AnimationsEnabled => _uiSettings.AnimationsEnabled;
    internal bool EffectsEnabled => _uiSettings.AdvancedEffectsEnabled;
    internal bool TitleChromeVisible => _chromeVisible && AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
    internal double TitleChromeHeight => AppTitleBar.ActualHeight > 0 ? AppTitleBar.ActualHeight : 32;
    internal bool TitleHasKeyboardFocus => HomeBackButton.FocusState == FocusState.Keyboard || ForwardButton.FocusState == FocusState.Keyboard;

    internal MainWindow(string? initialFile)
    {
        InitializeComponent();
        _host = new NativePlayerHost(this);
        _host.Backdrop = new MicaBackdrop();
        _themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
        _titleTransition = new OverlayTransition(TitleChrome, slide: false, collapseWhenHidden: false,
            isHighContrast: () => HighContrast);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1120, 740));
        _videoSizing = new VideoWindowSizing(this);
        _videoSizing.ClientSizeChanged += OnClientSizeChanged;
        PlayerPage.Configure(this, initialFile);
        AppWindow.Closing += OnClosing;
        _host.CloseRequested += RequestClose;
        SizeChanged += OnWindowLayoutChanged;
        // This UserControl is the DesktopWindowXamlSource content root. Its
        // inner Grid does not receive keys when the island focuses the root.
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        PlayerPage.ActualThemeChanged += OnThemeChanged;
        _themeSettings.Changed += OnThemeSettingsChanged;
        _uiSettings.AdvancedEffectsEnabledChanged += OnEffectsChanged;
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
        UpdateCaptionColors();
    }
    internal void Activate() => _host.Activate();
    internal void OpenFromExplorer(string? path)
    {
        if (_closing) return;
        Activate();
        if (path is not null) PlayerPage.OpenFromExplorer(path);
    }
    private void OnWindowLayoutChanged(object sender, SizeChangedEventArgs e) =>
        _host.SetDragHeight(AppTitleBar.ActualHeight > 0 ? AppTitleBar.ActualHeight : 40, XamlRoot?.RasterizationScale ?? 1, 80);
    internal void SetPlaybackBackEnabled(bool enabled)
    {
        _canReturnToBrowser = enabled;
        RefreshNavigation();
    }
    internal void RefreshNavigation()
    {
        HomeBackButton.IsEnabled = _canReturnToBrowser || PlayerPage.CanBrowseBack;
        ForwardButton.IsEnabled = PlayerPage.CanBrowseForward;
        HomeBackButton.IsTabStop = HomeBackButton.IsEnabled && _chromeVisible;
        ForwardButton.IsTabStop = ForwardButton.IsEnabled && _chromeVisible;
    }
    private void OnNavigateBack(object sender, RoutedEventArgs args)
    {
        if (PlayerPage.CanBrowseBack) { PlayerPage.BrowseBack(); return; }
        if (!_canReturnToBrowser) return;
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ToggleFullscreen();
        PlayerPage.ReturnToBrowser();
    }
    private void OnNavigateForward(object sender, RoutedEventArgs args) => PlayerPage.BrowseForward();
    private void OnBackFocusChanged(object sender, RoutedEventArgs args) => PlayerPage.RevealChrome();
    internal void ToggleFullscreen()
    {
        bool fullscreen = AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        _titleTransition.Stop();
        TitleChrome.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        if (!fullscreen)
        {
            TitleChrome.Opacity = 1;
            _titleTransition.SetVisible(true, animate: false);
        }
        PlayerPage.SetFullscreen(fullscreen);
        if (!fullscreen) _videoSizing.QueueFit();
    }
    internal void UpdateVideoGeometry(int width, int height, int rotation, uint generation, int mediaKind = 0) =>
        _videoSizing.SetGeometry(width, height, rotation, generation, mediaKind);
    internal void UpdateAudioGeometry(uint generation) => _videoSizing.SetGeometry(16, 9, 0, generation, 1);
    internal bool TryGetClientSize(out int width, out int height) => _videoSizing.TryGetClientSize(out width, out height);
    private void OnClientSizeChanged(int width, int height) => PlayerPage.ResizeToClient(width, height);
    internal void SetBrowserLayout(bool browser) => _host.ImmediateVideoLayout = !browser;
    internal void SetVideoMode(bool video)
    {
        if (_videoMode == video) return;
        _videoMode = video;
        TitleBranding.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        _host.ImmediateVideoLayout = video;
        _host.Backdrop = video ? null : new MicaBackdrop();
        if (!video) _videoSizing.Clear();
        AppTitleBar.RequestedTheme = video ? ElementTheme.Dark : ElementTheme.Default;
        UpdateTitleBackdropFallback();
        UpdateCaptionColors();
    }
    internal void SetChromeVisible(bool visible, bool animate = true)
    {
        if (_closing) return;
        _chromeVisible = visible;
        TitleChrome.IsHitTestVisible = visible;
        HomeBackButton.IsTabStop = visible && HomeBackButton.IsEnabled;
        ForwardButton.IsTabStop = visible && ForwardButton.IsEnabled;
        if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
            _titleTransition.SetVisible(visible, animate);
        UpdateTitleBackdropFallback();
        PlayerPage.UpdateChromeBackdrops(animate);
    }
    private void UpdateTitleBackdropFallback()
    {
        // Normal shading belongs to the system compositor. Retain WinUI's
        // theme brushes for accessibility contrast colors.
        TitleScrim.Visibility = TitleBand.Visibility = _videoMode && HighContrast ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateCaptionColors()
    {
        var title = AppWindow.TitleBar;
        title.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        var foreground = HighContrast
            ? _uiSettings.GetColorValue(UIColorType.Foreground)
            : _videoMode || PlayerPage.ActualTheme == ElementTheme.Dark
                ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        title.ButtonForegroundColor = foreground;
        title.ButtonInactiveForegroundColor = foreground;
    }
    internal void ApplyAppTheme(string theme)
    {
        var requested = theme == "light" ? ElementTheme.Light : theme == "dark" ? ElementTheme.Dark : ElementTheme.Default;
        if (RequestedTheme != requested) RequestedTheme = requested;
        UpdateCaptionColors();
    }
    private void OnThemeChanged(FrameworkElement sender, object args) => UpdateCaptionColors();
    private void OnSystemColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closing) PlayerPage.RefreshAppearance();
    });
    private void OnThemeSettingsChanged(ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing)
            {
                UpdateCaptionColors();
                SetChromeVisible(true, animate: false);
                PlayerPage.OnContrastChanged();
                PlayerPage.RefreshAppearance();
                PlayerPage.UpdateStartupAppearance();
            }
        });
    private void OnEffectsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closing) PlayerPage.UpdateStartupAppearance();
    });
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        PlayerPage.TraceVolumeInput($"root-preview key={e.Key} handled={e.Handled} video={_videoMode} source={e.OriginalSource?.GetType().Name}");
        if (!_videoMode) return;
        if (PlayerPage.IsVolumePanelInput(e.OriginalSource as DependencyObject)) return;
        // Buttons can consume arrow keys for focus navigation before KeyDown
        // reaches the root. Playback volume owns Up/Down even on the seekbar.
        // Text entry and open selection controls retain their own navigation.
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or PasswordBox or RichEditBox or ComboBox or MenuFlyoutPresenter or MenuFlyoutItemBase) return;
        foreach (var modifier in new[] { VirtualKey.Menu, VirtualKey.Control, VirtualKey.LeftWindows, VirtualKey.RightWindows })
            if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(modifier)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) return;
        PlayerPage.AdjustVolume(e.Key == VirtualKey.Up ? 5 : -5);
        e.Handled = true;
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        PlayerPage.RevealChrome();
        if (e.Handled) return;
        bool alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (alt && e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            if (e.Key == VirtualKey.Left) OnNavigateBack(this, new RoutedEventArgs());
            else PlayerPage.BrowseForward();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Escape && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        { ToggleFullscreen(); e.Handled = true; return; }
        if (!_videoMode) return; // Preserve NavigationView's keyboard navigation.
        // Keep native editing, button activation, and slider keyboard behavior.
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or PasswordBox or RichEditBox) return;
            if (element is ButtonBase && e.Key == VirtualKey.Space) return;
            if (element is Slider && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down) return;
        }
        switch (e.Key)
        {
            case VirtualKey.Space: PlayerPage.TogglePlayback(); break;
            case VirtualKey.Left: PlayerPage.SeekStep(-1); break;
            case VirtualKey.Right: PlayerPage.SeekStep(1); break;
            case VirtualKey.F: ToggleFullscreen(); break;
            case VirtualKey.Escape:
                if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ToggleFullscreen();
                break;
            default: return;
        }
        e.Handled = true;
    }
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        RequestClose();
    }
    internal async void RequestClose()
    {
        if (_closing) return;
        _closing = true;
        _titleTransition.Stop();
        _videoSizing.CaptureRestored();
        _videoSizing.Dispose();
        _themeSettings.Changed -= OnThemeSettingsChanged;
        _uiSettings.AdvancedEffectsEnabledChanged -= OnEffectsChanged;
        _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;
        PlayerPage.ActualThemeChanged -= OnThemeChanged;
        try { await PlayerPage.ShutdownAsync(); }
        finally { try { _host.Destroy(); } finally { Closed?.Invoke(); } }
    }
}
