using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Player.App.Playback;
using Player.App.Presentation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Player.App;

public sealed partial class MainPage : Page
{
    private MainWindow? _window;
    private NativeSession? _session;
    private Task? _initialization;
    private bool _engineReady;
    private bool _homeRequested;
    private bool _returnToBrowserOrigin;
    private string? _openedPath;
    private uint? _openedGeneration, _historyGeneration;
    private double _historyDuration;
    private PlaybackSnapshot _snapshot;
    private readonly object _updateLock = new();
    private PlaybackSnapshot _pending;
    private bool _updateQueued, _updating, _seeking, _closing;
    private string? _initialFile;
    private readonly OverlayTransition _controlsTransition;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idleTimer;
    private bool HighContrast => _window?.HighContrast ?? false;
    private bool _playbackVisible, _controlsVisible, _pointerOverControls;
    private bool _subtitleClearanceActive;
    private double _lastSubtitleClearance = double.NaN;
    private int _openFlyouts, _seekRevision;
    private uint _surfaceWidth, _surfaceHeight;
    private uint? _preparationQueued;
    private double _surfaceScaleX, _surfaceScaleY;
    private double _clientLayoutWidth, _clientLayoutHeight;
    private bool? _backdropContrast;
    private (double Bottom, double Title, bool ShowBottom, bool ShowTitle)? _backdropState;
    private sealed record TrackChoice(MediaTrackKind Kind, long Id, uint Generation);

    public MainPage()
    {
        try
        {
            InitializeComponent();
            SeekBar.ThumbToolTipValueConverter = new SeekTimeConverter();
            SeekBar.Loaded += OnChapterSeekBarLoaded;
            SeekBar.SizeChanged += (_, _) => RebuildChapterTrack();
            SeekBar.ValueChanged += (_, _) => UpdateChapterProgress();
            InitializeAppearanceBrushes();
            HomeShell.OpenFileRequested += RequestOpenFile;
            HomeShell.OpenVideoRequested += OpenFile;
            HomeShell.NavigationChanged += () => _window?.RefreshNavigation();
            _controlsTransition = new OverlayTransition(PlaybackControls, slide: true, collapseWhenHidden: true,
                isHighContrast: () => HighContrast);
            _controlsTransition.Completed += OnControlsTransitionCompleted;
            _controlsTransition.SetVisible(false, animate: false);
            _idleTimer = DispatcherQueue.CreateTimer();
            _idleTimer.Interval = TimeSpan.FromSeconds(3);
            _idleTimer.IsRepeating = false;
            _idleTimer.Tick += OnIdle;
            InitializeVolumeFeedback();
            AddHandler(PointerMovedEvent, new PointerEventHandler(OnPlayerPointerMoved), true);
            AddHandler(PointerPressedEvent, new PointerEventHandler(OnPlayerPointerPressed), true);
            SeekBar.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekPressed), true);
            SeekBar.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekReleased), true);
            SeekBar.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnSeekCanceled), true);
            SeekBar.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekCaptureLost), true);
        }
        catch (Exception ex)
        {
            App.WriteStartupFailure(ex, "Player page construction failed");
            throw;
        }
    }

    internal void Configure(MainWindow window, string? file)
    {
        _window = window; _initialFile = file; HomeShell.OwnerHandle = window.Handle;
        HomeShell.HighContrastRequested = () => window.HighContrast;
        UpdateStartupAppearance();
    }
    internal bool CanBrowseBack => HomeShell.Visibility == Visibility.Visible && HomeShell.CanGoBack;
    internal bool CanBrowseForward => HomeShell.Visibility == Visibility.Visible && HomeShell.CanGoForward;
    internal void BrowseBack() { if (CanBrowseBack) HomeShell.GoBack(); }
    internal void BrowseForward() { if (CanBrowseForward) HomeShell.GoForward(); }
    internal void UpdateStartupAppearance() => StartupScreen.SetAppearance(HighContrast, _window?.EffectsEnabled ?? false);
    private void RequestOpenFile() => OnOpen(this, new RoutedEventArgs());
    internal void OpenFromExplorer(string path)
    {
        if (_closing) return;
        // A launch arriving during startup replaces the initial request. Keep
        // any existing browser origin when replacing media opened from the UI.
        if (!_engineReady) { _initialFile = path; return; }
        OpenFile(path, directLaunch: !_returnToBrowserOrigin);
    }
    private void OnLoaded(object sender, RoutedEventArgs e) => _initialization ??= InitializePlaybackAsync();

    private async Task InitializePlaybackAsync()
    {
        try
        {
            await _playbackSettings.LoadAsync();
            if (_window is not null) await _window.InitializeWindowSettingsAsync(_playbackSettings.State.Window);
            RefreshAppearance();
            _session = await NativeSession.CreateAsync(_playbackSettings.State.Video.ZeroCopy);
            if (_closing) return;
            await InitializePlaybackSettingsAsync();
            if (_closing) return;
            _session.SetWindowSurface(_window!.Handle, true);
            ResizeSurface();
            _engineReady = true;
            _session.Changed += OnStateChanged;
            OpenButton.IsEnabled = EmptyOpenButton.IsEnabled = VolumeBar.IsEnabled = true;
            HomeShell.SetReady(true);
            if (_initialFile is not null) OpenFile(_initialFile, directLaunch: true);
            Apply(_session.ReadSnapshot());
        }
        catch (Exception ex) { ShowError(ex.Message, "Playback unavailable"); }
    }

    private void OnStateChanged(PlaybackSnapshot state)
    {
        lock (_updateLock)
        {
            _pending = state;
            if (_updateQueued || _closing) return;
            _updateQueued = true;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            PlaybackSnapshot latest;
            lock (_updateLock) { latest = _pending; _updateQueued = false; }
            if (!_closing) Apply(latest);
        });
    }

    private void Apply(PlaybackSnapshot state)
    {
        var previousState = _snapshot.State;
        uint previousTracksRevision = _snapshot.TracksRevision, previousGeneration = _snapshot.MediaGeneration;
        _snapshot = state;
        UpdateAutomaticPlayback(state, previousState == PlaybackState.Loading);
        _updating = true;
        try
        {
            bool home = _engineReady && (_homeRequested || state.State == PlaybackState.Empty);
            RefreshWindowTitle();
            bool loaded = !home && state.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended;
            bool loading = !home && (state.State == PlaybackState.Loading || state.Preparing != 0 || (loaded && state.HasVideo != 0 && state.VideoReady == 0));
            if (!home && !loading && state.HasVideo != 0 && (state.State == PlaybackState.Ended || (state.State == PlaybackState.Playing && state.Paused == 0))
                && _openedPath is not null && _openedGeneration == state.MediaGeneration && _historyGeneration != state.MediaGeneration)
            {
                _historyGeneration = state.MediaGeneration;
                _historyDuration = state.Duration;
                HomeShell.RecordPlayed(_openedPath, state.Duration);
            }
            if (_openedPath is not null && _openedGeneration == state.MediaGeneration && _historyGeneration == state.MediaGeneration
                && _historyDuration <= 0 && state.Duration > 0)
            {
                _historyDuration = state.Duration;
                HomeShell.UpdateRecentDuration(_openedPath, state.Duration);
            }
            PlayButton.IsEnabled = loaded && !loading;
            string playAction = state.State == PlaybackState.Ended ? "Replay" : state.Paused != 0 ? "Play" : "Pause";
            PlayIcon.Glyph = state.State == PlaybackState.Ended ? "\uE72C" : state.Paused != 0 ? "\uE768" : "\uE769";
            AutomationProperties.SetName(PlayButton, playAction);
            ToolTipService.SetToolTip(PlayButton, $"{playAction} (Space)");
            SeekBar.IsEnabled = loaded && !loading && state.Duration > 0;
            SeekBackButton.IsEnabled = SeekForwardButton.IsEnabled = SeekBar.IsEnabled;
            PreviousChapterButton.IsEnabled = PreviousChapterMenuItem.IsEnabled = loaded && !loading && state.ChapterCount > 0 && state.Chapter > 0;
            NextChapterButton.IsEnabled = NextChapterMenuItem.IsEnabled = loaded && !loading && state.ChapterCount > 0 && state.Chapter < state.ChapterCount - 1;
            SubtitleButton.IsEnabled = AudioButton.IsEnabled = LoopButton.IsEnabled = loaded && !loading;
            LoopButton.IsChecked = state.LoopFile != 0;
            ToolTipService.SetToolTip(LoopButton, state.LoopFile != 0 ? "Loop video: On" : "Loop video: Off");
            if (!loaded || previousGeneration != state.MediaGeneration)
            {
                SubtitleMenu.Hide();
                AudioMenu.Hide();
            }
            if (previousTracksRevision != state.TracksRevision || previousGeneration != state.MediaGeneration || previousState != state.State)
                UpdateOpenTrackMenus();
            SeekBar.Maximum = Math.Max(1, state.Duration);
            if (!_seeking) SeekBar.Value = Math.Clamp(state.Position, 0, SeekBar.Maximum);
            UpdateChapters(loaded && !loading);
            VolumeBar.Value = Math.Clamp(state.Volume, 0, 100);
            VolumeLabel.Text = $"{Math.Round(state.Volume)}";
            UpdateMutePresentation(state.Muted, state.Volume);
            UpdateTime(_seeking ? SeekBar.Value : state.Position);
            StartupScreen.SetLoading(!_engineReady || loading);
            HomeShell.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
            HomeShell.SetActive(home);
            _window?.SetPlaybackBackEnabled(_engineReady && !home && !loading);
            StatusOverlay.Visibility = !home && state.State == PlaybackState.Error ? Visibility.Visible : Visibility.Collapsed;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            EmptyIconWell.Visibility = Visibility.Visible;
            StatusIcon.Glyph = state.State == PlaybackState.Error ? "\uE7BA" : "\uE714";
            StatusTitle.Text = state.State == PlaybackState.Error ? "Couldn't play this file"
                : loading ? "Opening video" : "Open a video to get started";
            StatusDetail.Text = state.State == PlaybackState.Error ? state.Error
                : loading ? "Your video will start shortly." : "Drop a video here, or choose a file to watch.";
            EmptyOpenButton.Visibility = OpenHint.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            VideoArea.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
            SetPlaybackPresentation(loaded && !loading, previousState != state.State);
            // Keep the established immediate overlay preparation during file
            // loading/fitting as well as visible playback. Only browsing uses
            // the lighter scheduled layout path.
            _window?.SetBrowserLayout(home);
            if (loaded && state.HasVideo != 0)
            {
                _window?.UpdateVideoGeometry(state.DisplayWidth, state.DisplayHeight, state.Rotation, state.MediaGeneration, _session?.WindowInfo.Kind ?? 0);
                if (state.Preparing != 0 && state.DisplayWidth > 0 && state.DisplayHeight > 0 && _preparationQueued != state.MediaGeneration)
                {
                    uint generation = state.MediaGeneration;
                    _preparationQueued = generation;
                    // SetGeometry queues the initial fit first. Publish its
                    // resulting client size before native autoplay is allowed.
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closing || _snapshot.MediaGeneration != generation) return;
                        ResizeSurface();
                        Run(() => _session?.PreparePlayback(generation));
                    });
                }
            }
            else if (loaded && state.HasVideo == 0) _window?.UpdateAudioGeometry(state.MediaGeneration);
            UpdateSubtitleClearance();
        }
        finally { _updating = false; }
    }

    private void ShowError(string message, string title = "Couldn't play this file")
    {
        StartupScreen.SetLoading(false);
        HomeShell.Visibility = Visibility.Collapsed;
        HomeShell.SetActive(false);
        _window?.SetPlaybackBackEnabled(_engineReady);
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        EmptyIconWell.Visibility = Visibility.Visible;
        StatusIcon.Glyph = "\uE7BA";
        StatusOverlay.Visibility = Visibility.Visible;
        StatusTitle.Text = title;
        StatusDetail.Text = message;
        EmptyOpenButton.Visibility = Visibility.Visible;
        OpenHint.Visibility = Visibility.Visible;
        VideoArea.Visibility = Visibility.Collapsed;
        SetPlaybackPresentation(false, true);
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void OpenFile(string path) => OpenFile(path, directLaunch: false);
    private void OpenFile(string path, bool directLaunch) => Run(() =>
    {
        if (_session is null || _closing) return;
        path = Path.GetFullPath(path);
        // The browser retains its location, filter and navigation history while
        // hidden. Replacing media in the player keeps that original return path.
        if (directLaunch) _returnToBrowserOrigin = false;
        else if (HomeShell.Visibility == Visibility.Visible) _returnToBrowserOrigin = true;
        _homeRequested = false;
        ++_seekRevision;
        _seeking = false;
        MediaTitle.Text = Path.GetFileNameWithoutExtension(path);
        ToolTipService.SetToolTip(MediaTitle, Path.GetFileName(path));
        _session.Open(path);
        _openedPath = path;
        _openedGeneration = _session.ReadSnapshot().MediaGeneration;
        Apply(_session.ReadSnapshot());
        SubtitleMenu.Hide();
        AudioMenu.Hide();
        Focus(FocusState.Programmatic);
    });
    internal void ReturnToBrowser() => Run(() =>
    {
        if (_session is null || _closing) return;
        if (_snapshot.State != PlaybackState.Empty) _session.Pause(true);
        _homeRequested = true;
        ++_seekRevision;
        _seeking = false;
        SubtitleMenu.Hide();
        AudioMenu.Hide();
        if (!_returnToBrowserOrigin) HomeShell.NavigateRecentHome();
        Apply(_session.ReadSnapshot());
        HomeShell.FocusCurrentLocation();
    });
    private void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OnOpen(this, new RoutedEventArgs());
    }
    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_window is null || _session is null) return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _window.Handle);
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !_closing) OpenFile(file.Path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool accepted = _engineReady && _session is not null && !_closing && e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = accepted
            ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (!accepted) return;
        // Copy is the non-destructive transfer contract, not the user action:
        // dropping only opens the original path. Hide the shell's Copy badge
        // and source thumbnail; retain the native DPI-aware caption surface.
        e.DragUIOverride.Caption = "Open with mpv.win";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsContentVisible = false;
        e.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!_engineReady || _session is null || _closing || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (!_closing && items.FirstOrDefault() is StorageFile file) OpenFile(file.Path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { deferral.Complete(); }
    }
    private void OnPlay(object sender, RoutedEventArgs e) => TogglePlayback();
    internal void TogglePlayback() => Run(() =>
    {
        RevealChrome();
        if (_session is null || !PlayButton.IsEnabled) return;
        if (_snapshot.State == PlaybackState.Ended) { _session.Seek(0); _session.Pause(false); }
        else _session.Pause(_snapshot.Paused == 0);
    });
    internal void SeekRelative(double seconds) => Run(() =>
    {
        RevealChrome();
        if (SeekBar.IsEnabled) _session?.Seek(Math.Clamp(_snapshot.Position + seconds, 0, _snapshot.Duration), _playbackSettings.State.ExactSeeking);
    });
    internal void AdjustVolume(double amount) => Run(() =>
    {
        TraceVolumeInput($"adjust amount={amount} visible={_playbackVisible} ready={_engineReady} closing={_closing} native={_snapshot.Volume} slider={VolumeBar.Value}");
        if (_closing || !_playbackVisible || !_engineReady || _session is null) return;
        RevealChrome();
        double volume = Math.Clamp(VolumeBar.Value + amount, 0, 100);
        // Playback commands must not depend on a slider in a closed flyout.
        // Suppress its handler while reflecting the command in the UI.
        bool wasUpdating = _updating;
        _updating = true;
        try { VolumeBar.Value = volume; }
        finally { _updating = wasUpdating; }
        _session.SetVolume(volume);
        TraceVolumeInput($"command volume={volume}");
        VolumeLabel.Text = $"{Math.Round(volume)}";
        UpdateMutePresentation(_snapshot.Muted, volume);
        ShowVolumeFeedback();
    });
    private void OnSeekBack(object sender, RoutedEventArgs e) => SeekStep(-1);
    private void OnSeekForward(object sender, RoutedEventArgs e) => SeekStep(1);
    private void OnPreviousChapter(object sender, RoutedEventArgs e) => ChangeChapter(-1);
    private void OnNextChapter(object sender, RoutedEventArgs e) => ChangeChapter(1);
    private void ChangeChapter(int direction) => Run(() =>
    {
        RevealChrome();
        if (direction < 0 ? PreviousChapterButton.IsEnabled : NextChapterButton.IsEnabled)
            _session?.ChangeChapter(direction, _snapshot.MediaGeneration);
    });
    private void OnLoop(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_session is null || !LoopButton.IsEnabled) return;
        bool enabled = LoopButton.IsChecked == true;
        _session.SetLoop(enabled);
        if (enabled && _snapshot.State == PlaybackState.Ended)
        {
            _session.Seek(0);
            _session.Pause(false);
        }
        RevealChrome();
    });
    private void OnSubtitleMenuOpening(object sender, object args) => BuildTrackMenu(SubtitleMenu, MediaTrackKind.Subtitle);
    private void OnAudioMenuOpening(object sender, object args) => BuildTrackMenu(AudioMenu, MediaTrackKind.Audio);
    private void BuildTrackMenu(MenuFlyout menu, MediaTrackKind kind)
    {
        menu.Items.Clear();
        menu.Items.Add(new MenuFlyoutItem { Text = kind == MediaTrackKind.Subtitle ? "Subtitles" : "Audio tracks", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        var catalog = _session?.Catalog ?? TrackCatalog.Empty;
        var tracks = catalog.Generation == _snapshot.MediaGeneration
            ? catalog.Tracks.Where(t => t.Kind == kind).ToArray() : [];
        if (kind == MediaTrackKind.Subtitle)
            AddTrackChoice(menu, new TrackChoice(kind, 0, _snapshot.MediaGeneration), "Off", !tracks.Any(t => t.Selected));
        foreach (var track in tracks)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(track.Title)) parts.Add(track.Title.ReplaceLineEndings(" ").Trim());
            if (!string.IsNullOrWhiteSpace(track.Language)) parts.Add(track.Language);
            if (!string.IsNullOrWhiteSpace(track.Codec)) parts.Add(track.Codec.ToUpperInvariant());
            parts.Add($"Track {track.Id}");
            AddTrackChoice(menu, new TrackChoice(kind, track.Id, catalog.Generation), string.Join(" · ", parts), track.Selected);
        }
        if (tracks.Length == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = kind == MediaTrackKind.Subtitle ? "No embedded subtitles" : "No audio tracks", IsEnabled = false });
        if (kind == MediaTrackKind.Audio) AddAudioOutputMenu(menu);
    }
    private void AddTrackChoice(MenuFlyout menu, TrackChoice choice, string label, bool selected)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = label, Tag = choice, IsChecked = selected, MaxWidth = 460,
            GroupName = choice.Kind == MediaTrackKind.Subtitle ? "SubtitleTracks" : "AudioTracks"
        };
        item.Click += OnTrackSelected;
        menu.Items.Add(item);
    }
    private void OnTrackSelected(object sender, RoutedEventArgs args)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: TrackChoice choice } ||
            choice.Generation != _snapshot.MediaGeneration || !_playbackVisible) return;
        Run(() => _session?.SelectTrack(choice.Kind, choice.Id, choice.Generation));
    }
    private void UpdateOpenTrackMenus()
    {
        var catalog = _session?.Catalog ?? TrackCatalog.Empty;
        foreach (var menu in new[] { SubtitleMenu, AudioMenu })
        {
            foreach (var item in menu.Items.OfType<RadioMenuFlyoutItem>())
            {
                if (item.Tag is not TrackChoice choice) continue;
                bool valid = choice.Generation == _snapshot.MediaGeneration && choice.Generation == catalog.Generation;
                bool exists = choice.Kind == MediaTrackKind.Subtitle && choice.Id == 0 ||
                    catalog.Tracks.Any(t => t.Kind == choice.Kind && t.Id == choice.Id);
                item.IsEnabled = valid && exists;
                item.IsChecked = valid && (choice.Id == 0
                    ? !catalog.Tracks.Any(t => t.Kind == choice.Kind && t.Selected)
                    : catalog.Tracks.Any(t => t.Kind == choice.Kind && t.Id == choice.Id && t.Selected));
            }
        }
    }
    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updating && _session is not null) Run(() => _session.SetVolume(e.NewValue));
    }
    private void OnSeekPressed(object sender, PointerRoutedEventArgs e)
    {
        ++_seekRevision;
        if (SeekBar.IsEnabled) { _seeking = true; RevealChrome(); }
    }
    private void OnSeekReleased(object sender, PointerRoutedEventArgs e) => CommitSeek();
    private void OnSeekCaptureLost(object sender, PointerRoutedEventArgs e) => CommitSeek();
    private void OnSeekCanceled(object sender, PointerRoutedEventArgs e)
    {
        ++_seekRevision;
        _seeking = false;
        _updating = true;
        try { SeekBar.Value = Math.Clamp(_snapshot.Position, 0, SeekBar.Maximum); }
        finally { _updating = false; }
        UpdateTime(_snapshot.Position);
        ArmIdleTimer();
    }
    private void CommitSeek()
    {
        if (!_seeking) return;
        _seeking = false;
        ++_seekRevision;
        Run(() => _session?.Seek(SeekBar.Value));
        ArmIdleTimer();
    }
    private void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _session is null) return;
        UpdateTime(e.NewValue);
        // The Slider can change Value before its handled PointerPressed bubbles
        // here. Defer keyboard/automation commits so a pointer drag is recognized
        // before deciding whether this change should seek immediately.
        if (!_seeking)
        {
            double target = e.NewValue;
            int revision = ++_seekRevision;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_seeking && !_closing && _seekRevision == revision) Run(() => _session?.Seek(target));
            });
        }
    }
    private void UpdateTime(double position)
    {
        ElapsedLabel.Text = FormatTime(position);
        DurationLabel.Text = FormatTime(_snapshot.Duration);
    }
    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
    private sealed class SeekTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            FormatTime(value is double seconds ? seconds : 0);
        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
    private void OnFullscreen(object sender, RoutedEventArgs e) => _window?.ToggleFullscreen();
    internal void SetFullscreen(bool fullscreen)
    {
        FullscreenIcon.Glyph = fullscreen ? "\uE73F" : "\uE740";
        string action = fullscreen ? "Exit fullscreen" : "Enter fullscreen";
        FullscreenMenuItem.Text = action;
        AutomationProperties.SetName(FullscreenButton, action);
        ToolTipService.SetToolTip(FullscreenButton, fullscreen ? "Exit fullscreen (Esc)" : "Fullscreen (F)");
        RevealChrome();
    }
    private void SetPlaybackPresentation(bool visible, bool stateChanged)
    {
        bool changed = _playbackVisible != visible;
        _playbackVisible = visible;
        if (changed && _session is not null && !_closing)
        {
            _session.SetVideoVisible(visible);
            if (!visible) _backdropState = null;
        }
        _window?.SetVideoMode(visible);
        if (!visible)
        {
            if (VolumeFlyout.IsOpen) VolumeFlyout.Hide();
            _idleTimer.Stop();
            _controlsVisible = false;
            PlaybackControls.IsHitTestVisible = false;
            _controlsTransition.SetVisible(false, animate: false);
            _window?.SetChromeVisible(true, animate: false);
        }
        else if (changed || stateChanged) RevealChrome();
    }
    internal void RevealChrome()
    {
        if (_closing) return;
        if (_playbackVisible)
        {
            _controlsVisible = true;
            _subtitleClearanceActive = true;
            PlaybackControls.IsHitTestVisible = true;
            _controlsTransition.SetVisible(true, animate: !HighContrast);
            UpdateSubtitleClearance();
        }
        _window?.SetChromeVisible(true);
        ArmIdleTimer();
    }
    private bool HasKeyboardFocusInControls()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused is not Control { FocusState: FocusState.Keyboard }) return false;
        for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, PlaybackControls)) return true;
        return false;
    }
    private bool CanHideControls() => _playbackVisible && _snapshot.State == PlaybackState.Playing
        && _snapshot.Paused == 0 && _snapshot.HasVideo != 0 && !_seeking && !_pointerOverControls
        && _openFlyouts == 0 && !HasKeyboardFocusInControls() && _window?.TitleHasKeyboardFocus != true && !HighContrast;
    private void ArmIdleTimer()
    {
        _idleTimer.Stop();
        if (!_closing && _controlsVisible && CanHideControls()) _idleTimer.Start();
    }
    private void OnIdle(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!CanHideControls()) return;
        _controlsVisible = false;
        PlaybackControls.IsHitTestVisible = false;
        _controlsTransition.SetVisible(false);
        _window?.SetChromeVisible(false);
    }
    private void OnPlayerPointerMoved(object sender, PointerRoutedEventArgs e) => RevealChrome();
    private void OnControlsTransitionCompleted(bool visible)
    {
        if (visible) { QueuePendingVolumeFeedback(); return; }
        _subtitleClearanceActive = false;
        UpdateSubtitleClearance();
    }
    private void OnPlaybackControlsSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSubtitleClearance();
    internal void UpdateChromeBackdrops(bool animate = true)
    {
        bool contrast = HighContrast;
        if (_backdropContrast != contrast)
        {
            PlaybackControls.Background = contrast
                ? (Brush)Application.Current.Resources["PlayerOverlayBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            _backdropContrast = contrast;
        }
        if (_session is null || _closing || !_playbackVisible) return;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        double height = PlaybackControls.ActualHeight > 0 ? PlaybackControls.ActualHeight
            : _clientLayoutWidth < 960 ? 194 : 146;
        // Keep video unobscured behind the playback title controls. The bottom
        // control backdrop continues to follow the system-compositor path.
        var state = (Bottom: height * scale, Title: 0d,
            ShowBottom: _controlsVisible && !contrast, ShowTitle: false);
        bool fade = animate && !contrast && (_window?.AnimationsEnabled ?? true);
        if (fade && _backdropState == state) return;
        Run(() =>
        {
            _session.SetChromeBackdrops(state.Bottom, state.Title, state.ShowBottom, state.ShowTitle, fade);
            _backdropState = state;
        });
    }
    private void UpdateSubtitleClearance()
    {
        UpdateChromeBackdrops();
        if (_session is null || _closing) return;
        double fraction = 0;
        double layoutWidth = _clientLayoutWidth > 0 ? _clientLayoutWidth : PlayerRoot.ActualWidth;
        double layoutHeight = _clientLayoutHeight > 0 ? _clientLayoutHeight : PlayerRoot.ActualHeight;
        if (_subtitleClearanceActive && _playbackVisible && layoutHeight > 0)
        {
            double canvasHeight = layoutHeight;
            double width = _snapshot.DisplayWidth, height = _snapshot.DisplayHeight;
            if (Math.Abs(_snapshot.Rotation % 180) == 90) (width, height) = (height, width);
            if (width > 0 && height > 0)
                canvasHeight = Math.Min(canvasHeight, layoutWidth * height / width);
            double controlsHeight = PlaybackControls.ActualHeight > 0 ? PlaybackControls.ActualHeight
                : layoutWidth < 960 ? 194 : 146;
            fraction = Math.Clamp((controlsHeight + 8) / Math.Max(1, canvasHeight), 0, 0.8);
        }
        if (double.IsFinite(_lastSubtitleClearance) && Math.Abs(fraction - _lastSubtitleClearance) < 0.0001) return;
        Run(() =>
        {
            _session.SetSubtitleClearance(fraction, _playbackSettings.State.SubtitlePosition);
            _lastSubtitleClearance = fraction;
        });
    }
    private void OnPlayerPointerPressed(object sender, PointerRoutedEventArgs e) => RevealChrome();
    private void OnControlsEntered(object sender, PointerRoutedEventArgs e) { _pointerOverControls = true; RevealChrome(); }
    private void OnControlsExited(object sender, PointerRoutedEventArgs e) { _pointerOverControls = false; ArmIdleTimer(); }
    private void OnControlsFocusChanged(object sender, RoutedEventArgs e)
    {
        // Focus state settles after the routed event completes.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (HasKeyboardFocusInControls()) RevealChrome();
            else ArmIdleTimer();
        });
    }
    private void OnFlyoutOpened(object sender, object args) { ++_openFlyouts; RevealChrome(); }
    private void OnFlyoutClosed(object sender, object args) { _openFlyouts = Math.Max(0, _openFlyouts - 1); ArmIdleTimer(); }
    internal void OnContrastChanged() { if (!_closing) { RebuildChapterTrack(); RevealChrome(); } }
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 960;
        Grid.SetColumn(TransportButtons, compact ? 0 : 1);
        Grid.SetColumnSpan(TransportButtons, compact ? 3 : 1);
        Grid.SetRow(AuxiliaryControls, compact ? 1 : 0);
        Grid.SetColumn(AuxiliaryControls, compact ? 0 : 2);
        Grid.SetColumnSpan(AuxiliaryControls, compact ? 3 : 1);
        AuxiliaryControls.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        TransportRow.RowSpacing = compact ? 8 : 0;
        PreviousChapterButton.Visibility = NextChapterButton.Visibility = e.NewSize.Width < 520 ? Visibility.Collapsed : Visibility.Visible;
        ResizeSurface();
        UpdateSubtitleClearance();
    }
    private void ResizeSurface()
    {
        if (_window is not null && _window.TryGetClientSize(out int width, out int height))
            ResizeToClient(width, height);
    }
    internal void ResizeToClient(int clientWidth, int clientHeight)
    {
        if (_session is null || _closing) return;
        double scaleX = XamlRoot?.RasterizationScale ?? 1, scaleY = scaleX;
        _clientLayoutWidth = clientWidth / scaleX;
        _clientLayoutHeight = clientHeight / scaleY;
        // The browser needs XAML layout only. Do not allocate/resize a hidden
        // playback swap chain for every gallery-window size notification.
        if (_engineReady && (_homeRequested || _snapshot.State == PlaybackState.Empty)) return;
        // The system-compositor video fills the native client. WM_SIZE supplies physical pixels
        // even inside Windows' modal sizing loop, where queued layout work can
        // lag behind pointer movement. No UI-thread wait for rendering.
        uint width = (uint)Math.Max(1, clientWidth);
        uint height = (uint)Math.Max(1, clientHeight);
        if (width == _surfaceWidth && height == _surfaceHeight && scaleX == _surfaceScaleX && scaleY == _surfaceScaleY) return;
        Run(() =>
        {
            // Unlike SwapChainPanel, this visual uses physical client units;
            // XAML's rasterization scale only affects controls/subtitle clearance.
            _session!.Resize(width, height, 1, 1);
            _surfaceWidth = width; _surfaceHeight = height;
            _surfaceScaleX = scaleX; _surfaceScaleY = scaleY;
        });
        UpdateSubtitleClearance();
    }
    internal async Task ShutdownAsync()
    {
        _closing = true;
        HomeShell.OpenFileRequested -= RequestOpenFile;
        HomeShell.OpenVideoRequested -= OpenFile;
        HomeShell.SetActive(false);
        StartupScreen.SetLoading(false);
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdle;
        StopVolumeFeedback();
        _controlsTransition.Completed -= OnControlsTransitionCompleted;
        _controlsTransition.Stop();
        await HomeShell.FlushPreferencesAsync();
        if (_initialization is not null) await _initialization;
        await _settingsApplyTask;
        await SaveRememberedAudioAsync();
        if (_window is not null) await _window.SaveWindowMemoryAsync();
        if (_session is not null)
        {
            _session.Changed -= OnStateChanged;
            try { _session.SetWindowSurface(_window!.Handle, false); }
            finally { await _session.DisposeAsync(); _session = null; }
        }
    }
}
