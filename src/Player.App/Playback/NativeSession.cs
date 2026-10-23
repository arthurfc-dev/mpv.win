using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace Player.App.Playback;

internal enum PlaybackState { Empty, Loading, Playing, Paused, Ended, Error }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeSettingResult
{
    public ulong Request;
    public int Status;
    public PlaybackOption Option;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Error;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PlaybackSnapshot
{
    public uint Size;
    public PlaybackState State;
    public double Position, Duration, Volume;
    public int Paused, HasVideo, VideoReady, Width, Height, Preparing;
    public long PresentedFrames, DroppedFrames;
    public double Fps, AvSync;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string Error;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Decoder;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Version;
    public uint TracksRevision, MediaGeneration;
    public int ChapterCount, Chapter, LoopFile;
    public uint PlaybackFinished;
    public int DisplayWidth, DisplayHeight, Rotation;
    public uint AudioStatus;
    public double SubtitlePosition;
    internal bool Muted => (AudioStatus & 1) != 0;
    internal uint AudioRevision => AudioStatus >> 1;
}

internal enum MediaTrackKind { Audio = 1, Subtitle = 2 }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeTrack
{
    public long Id;
    public MediaTrackKind Kind;
    public int Selected;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Language;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Codec;
}

internal sealed record MediaTrack(long Id, MediaTrackKind Kind, bool Selected, string Title, string Language, string Codec);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeChapter
{
    public double Time;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
}
internal sealed record MediaChapter(double Time, string Title);
internal sealed record ChapterCatalog(uint Revision, uint Generation, IReadOnlyList<MediaChapter> Chapters)
{
    internal static readonly ChapterCatalog Empty = new(0, 0, Array.Empty<MediaChapter>());
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeAudioDevice
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
}
internal sealed record AudioOutputDevice(string Name, string Description);
internal sealed record MediaWindowInfo(uint Revision, string Title, int Kind);
internal sealed record AudioDeviceCatalog(uint Revision, string Selected, IReadOnlyList<AudioOutputDevice> Devices)
{
    internal static readonly AudioDeviceCatalog Empty = new(0, "auto", Array.Empty<AudioOutputDevice>());
}
internal sealed record TrackCatalog(uint Revision, uint Generation, IReadOnlyList<MediaTrack> Tracks)
{
    internal static readonly TrackCatalog Empty = new(0, 0, Array.Empty<MediaTrack>());
}

internal sealed class NativeSession : IAsyncDisposable
{
    private readonly SessionHandle _handle;
    private readonly EventWaitHandle _changed = new(false, EventResetMode.AutoReset);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Task _events;
    private readonly StreamWriter? _diagnostics;
    private long _settingRequest;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _settingRequests = new();
    internal event Action<PlaybackSnapshot>? Changed;
    private TrackCatalog _catalog = TrackCatalog.Empty;
    private ChapterCatalog _chapters = ChapterCatalog.Empty;
    internal ChapterCatalog Chapters => Volatile.Read(ref _chapters);
    internal TrackCatalog Catalog => Volatile.Read(ref _catalog);
    private AudioDeviceCatalog _audioCatalog = AudioDeviceCatalog.Empty;
    internal AudioDeviceCatalog AudioDevices => Volatile.Read(ref _audioCatalog);
    private MediaWindowInfo _windowInfo = new(0, "", 0);
    internal MediaWindowInfo WindowInfo => Volatile.Read(ref _windowInfo);

    private NativeSession(SessionHandle handle)
    {
        _handle = handle;
        _changed.SafeWaitHandle = new SafeWaitHandle(NativeMethods.GetStateEvent(handle), ownsHandle: false);
        var diagnosticPath = Environment.GetEnvironmentVariable("PLAYER_DIAGNOSTICS_PATH");
        if (!string.IsNullOrWhiteSpace(diagnosticPath))
        {
            try { _diagnostics = new StreamWriter(diagnosticPath, append: false) { AutoFlush = true }; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _events = Task.Run(PumpEvents);
    }

    internal static Task<NativeSession> CreateAsync(bool zeroCopy = false) => Task.Run(() =>
    {
        if (NativeMethods.GetApiVersion() != 15 || Marshal.SizeOf<PlaybackSnapshot>() != 1544 || Marshal.SizeOf<NativeTrack>() != 784 || Marshal.SizeOf<NativeSettingResult>() != 528 || Marshal.SizeOf<NativeAudioDevice>() != 768 || Marshal.SizeOf<NativeChapter>() != 520)
            throw new InvalidOperationException("The playback bridge and application versions do not match.");
        var error = new StringBuilder(512);
        string? shaderCache = Environment.GetEnvironmentVariable("PLAYER_SHADER_CACHE_PATH");
        try
        {
            if (string.IsNullOrWhiteSpace(shaderCache))
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                shaderCache = string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Player", "Cache", "mpv-d3d11");
            }
            if (shaderCache is not null) Directory.CreateDirectory(shaderCache);
        }
        catch (IOException) { shaderCache = null; }
        catch (UnauthorizedAccessException) { shaderCache = null; }
        string watchLater = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv.win", "watch_later");
        var result = NativeMethods.Create(out var handle, error, (uint)error.Capacity, shaderCache, watchLater, zeroCopy ? 1 : 0);
        if (result < 0)
        {
            handle?.Dispose();
            throw new InvalidOperationException(error.ToString());
        }
        try { return new NativeSession(handle); }
        catch { handle.Dispose(); throw; }
    });

    private void PumpEvents()
    {
        WaitHandle[] handles = [_stop, _changed];
        while (WaitHandle.WaitAny(handles) == 1)
        {
            while (NativeMethods.TakeSettingResult(_handle, out var result, (uint)Marshal.SizeOf<NativeSettingResult>()) == 1)
                if (_settingRequests.TryRemove(result.Request, out var pending))
                {
                    if (result.Status >= 0) pending.TrySetResult();
                    else pending.TrySetException(new InvalidOperationException($"{result.Option}: {result.Error}"));
                }
            var snapshot = ReadSnapshot();
            if (NativeMethods.GetChapters(_handle, [], 0, 520, out uint chaptersRevision, out uint chaptersGeneration) >= 0
                && (chaptersRevision != Chapters.Revision || chaptersGeneration != Chapters.Generation))
                Volatile.Write(ref _chapters, ReadChapters());
            if (snapshot.TracksRevision != Catalog.Revision || snapshot.MediaGeneration != Catalog.Generation)
                Volatile.Write(ref _catalog, ReadTracks());
            if (snapshot.AudioRevision != AudioDevices.Revision) Volatile.Write(ref _audioCatalog, ReadAudioDevices());
            if (NativeMethods.GetWindowInfo(_handle, null, 0, out uint windowRevision, out _) == 0 && windowRevision != WindowInfo.Revision)
            {
                var title = new StringBuilder(2048);
                Check(NativeMethods.GetWindowInfo(_handle, title, 2048, out windowRevision, out int kind));
                Volatile.Write(ref _windowInfo, new(windowRevision, title.ToString(), kind));
            }
            if (_diagnostics is not null)
            {
                try
                {
                    _diagnostics.WriteLine(JsonSerializer.Serialize(new
                    {
                        Utc = DateTime.UtcNow, State = snapshot.State.ToString(),
                        snapshot.Position, snapshot.Duration, snapshot.Volume, snapshot.Paused,
                        snapshot.Width, snapshot.Height, snapshot.Fps, snapshot.Decoder,
                        snapshot.PresentedFrames, snapshot.DroppedFrames, snapshot.AvSync,
                        snapshot.VideoReady, snapshot.Preparing, snapshot.MediaGeneration, snapshot.Error, snapshot.Version,
                        snapshot.ChapterCount, snapshot.Chapter, snapshot.LoopFile,
                        snapshot.DisplayWidth, snapshot.DisplayHeight, snapshot.Rotation, snapshot.SubtitlePosition,
                        Tracks = Catalog.Tracks
                    }));
                }
                catch (IOException) { }
            }
            Changed?.Invoke(snapshot);
        }
    }

    internal PlaybackSnapshot ReadSnapshot()
    {
        var snapshot = new PlaybackSnapshot { Size = (uint)Marshal.SizeOf<PlaybackSnapshot>() };
        Check(NativeMethods.GetSnapshot(_handle, ref snapshot));
        return snapshot;
    }

    internal void Resize(uint width, uint height, double scaleX, double scaleY) =>
        Check(NativeMethods.Resize(_handle, width, height, scaleX, scaleY));
    internal void SetWindowSurface(nint window, bool attach) => Check(NativeMethods.SetWindowSurface(_handle, window, attach ? 1 : 0));
    internal void SetVideoVisible(bool visible) => Check(NativeMethods.SetVideoVisible(_handle, visible ? 1 : 0));
    internal void SetChromeBackdrops(double bottomHeight, double titleHeight, bool bottom, bool title, bool animate) =>
        Check(NativeMethods.SetChromeBackdrops(_handle, bottomHeight, titleHeight,
            (bottom ? 1u : 0) | (title ? 2u : 0) | (animate ? 4u : 0)));
    internal void Open(string path) => Check(NativeMethods.Open(_handle, path));
    internal string? MediaPath(uint expectedGeneration)
    {
        var path = new StringBuilder(32768);
        Check(NativeMethods.GetMediaPath(_handle, path, (uint)path.Capacity, out uint generation));
        return generation == expectedGeneration && path.Length > 0 ? path.ToString() : null;
    }
    internal void PreparePlayback(uint generation) => Check(NativeMethods.PreparePlayback(_handle, generation));
    internal void Pause(bool paused) => Check(NativeMethods.SetPaused(_handle, paused ? 1 : 0));
    internal void Seek(double seconds, bool exact = true) => Check(NativeMethods.Seek(_handle, seconds, exact ? 1 : 0));
    internal void SetVolume(double volume) => Check(NativeMethods.SetVolume(_handle, volume));
    internal void SelectTrack(MediaTrackKind kind, long id, uint generation) =>
        Check(NativeMethods.SelectTrack(_handle, kind, id, generation));
    internal void ChangeChapter(int direction, uint generation) =>
        Check(NativeMethods.ChangeChapter(_handle, direction, generation));
    internal void SetLoop(bool enabled) => Check(NativeMethods.SetLoop(_handle, enabled ? 1 : 0));
    internal void SetSubtitleClearance(double fraction, double baseline) => Check(NativeMethods.SetSubtitleClearance(_handle, fraction, baseline));

    internal async Task SetOptionAsync(PlaybackOption option, string value)
    {
        ulong request = (1ul << 63) | (ulong)Interlocked.Increment(ref _settingRequest);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingRequests[request] = completion;
        try
        {
            Check(NativeMethods.SetSetting(_handle, option, value, request));
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally { _settingRequests.TryRemove(request, out _); }
    }

    private ChapterCatalog ReadChapters()
    {
        NativeChapter[] buffer = [];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int count = NativeMethods.GetChapters(_handle, buffer, (uint)buffer.Length, 520, out uint revision, out uint generation);
            Check(count);
            if (count <= buffer.Length)
                return new(revision, generation, Array.AsReadOnly(buffer.Take(count).Select(c => new MediaChapter(c.Time, c.Title)).ToArray()));
            buffer = new NativeChapter[Math.Min(count, 4096)];
        }
        return ChapterCatalog.Empty;
    }

    private TrackCatalog ReadTracks()
    {
        NativeTrack[] buffer = [];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int count = NativeMethods.GetTracks(_handle, buffer, (uint)buffer.Length,
                (uint)Marshal.SizeOf<NativeTrack>(), out uint revision, out uint generation);
            Check(count);
            if (count <= buffer.Length)
            {
                var tracks = buffer.Take(count).Select(t =>
                    new MediaTrack(t.Id, t.Kind, t.Selected != 0, t.Title, t.Language, t.Codec)).ToArray();
                return new TrackCatalog(revision, generation, Array.AsReadOnly(tracks));
            }
            buffer = new NativeTrack[count];
        }
        // A rapidly replacing file can invalidate each count read. The state
        // event remains signaled and will give the next iteration a fresh list.
        return TrackCatalog.Empty;
    }

    private AudioDeviceCatalog ReadAudioDevices()
    {
        NativeAudioDevice[] buffer = [];
        var selected = new StringBuilder(128);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int count = NativeMethods.GetAudioDevices(_handle, buffer, (uint)buffer.Length, 768, selected, 128, out uint revision);
            Check(count);
            if (count <= buffer.Length)
                return new(revision, selected.ToString(), Array.AsReadOnly(buffer.Take(count)
                    .Select(device => new AudioOutputDevice(device.Name, string.IsNullOrWhiteSpace(device.Description) ? device.Name : device.Description)).ToArray()));
            buffer = new NativeAudioDevice[Math.Min(count, 256)];
        }
        return AudioDeviceCatalog.Empty;
    }

    private static void Check(int result)
    {
        if (result < 0) throw new InvalidOperationException($"Playback operation failed ({result}).");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pending in _settingRequests.Values) pending.TrySetCanceled();
        _settingRequests.Clear();
        _stop.Set();
        await _events.ConfigureAwait(false);
        _changed.Dispose();
        _stop.Dispose();
        _diagnostics?.Dispose();
        await Task.Run(_handle.Dispose).ConfigureAwait(false);
    }
}

internal sealed class SessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SessionHandle() : base(ownsHandle: true) { }
    protected override bool ReleaseHandle() { NativeMethods.Destroy(handle); return true; }
}

internal static class NativeMethods
{
    private const string Library = "Player.Native.dll";
    [DllImport(Library, EntryPoint = "player_bridge_get_chapters", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetChapters(SessionHandle handle, [Out] NativeChapter[] chapters, uint capacity, uint stride,
        out uint revision, out uint generation);
    [DllImport(Library, EntryPoint = "player_bridge_get_api_version", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint GetApiVersion();
    [DllImport(Library, EntryPoint = "player_bridge_create", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int Create(out SessionHandle handle, StringBuilder error, uint capacity, string? shaderCache, string watchLater, int zeroCopy);
    [DllImport(Library, EntryPoint = "player_bridge_get_media_path", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int GetMediaPath(SessionHandle handle, StringBuilder path, uint capacity, out uint generation);
    [DllImport(Library, EntryPoint = "player_bridge_get_audio_devices", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int GetAudioDevices(SessionHandle handle, [Out] NativeAudioDevice[] devices, uint capacity, uint stride,
        StringBuilder selected, uint selectedCapacity, out uint revision);
    [DllImport(Library, EntryPoint = "player_bridge_get_window_info", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowInfo(SessionHandle handle, StringBuilder? title, uint capacity, out uint revision, out int kind);
    [DllImport(Library, EntryPoint = "player_bridge_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(IntPtr handle);
    [DllImport(Library, EntryPoint = "player_bridge_get_state_event", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr GetStateEvent(SessionHandle handle);
    [DllImport(Library, EntryPoint = "player_bridge_get_snapshot", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetSnapshot(SessionHandle handle, ref PlaybackSnapshot snapshot);
    [DllImport(Library, EntryPoint = "player_bridge_resize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Resize(SessionHandle handle, uint width, uint height, double scaleX, double scaleY);
    [DllImport(Library, EntryPoint = "player_bridge_open", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int Open(SessionHandle handle, string path);
    [DllImport(Library, EntryPoint = "player_bridge_prepare_playback", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int PreparePlayback(SessionHandle handle, uint generation);
    [DllImport(Library, EntryPoint = "player_bridge_set_paused", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetPaused(SessionHandle handle, int paused);
    [DllImport(Library, EntryPoint = "player_bridge_seek", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Seek(SessionHandle handle, double seconds, int exact);
    [DllImport(Library, EntryPoint = "player_bridge_set_setting", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int SetSetting(SessionHandle handle, PlaybackOption option, string value, ulong request);
    [DllImport(Library, EntryPoint = "player_bridge_take_setting_result", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int TakeSettingResult(SessionHandle handle, out NativeSettingResult result, uint size);
    [DllImport(Library, EntryPoint = "player_bridge_set_volume", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetVolume(SessionHandle handle, double volume);
    [DllImport(Library, EntryPoint = "player_bridge_get_tracks", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetTracks(SessionHandle handle, [Out] NativeTrack[] tracks, uint capacity, uint stride,
        out uint revision, out uint generation);
    [DllImport(Library, EntryPoint = "player_bridge_select_track", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SelectTrack(SessionHandle handle, MediaTrackKind kind, long id, uint generation);
    [DllImport(Library, EntryPoint = "player_bridge_change_chapter", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ChangeChapter(SessionHandle handle, int direction, uint generation);
    [DllImport(Library, EntryPoint = "player_bridge_set_loop", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetLoop(SessionHandle handle, int enabled);
    [DllImport(Library, EntryPoint = "player_bridge_set_subtitle_clearance", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetSubtitleClearance(SessionHandle handle, double fraction, double baseline);
    [DllImport(Library, EntryPoint = "player_bridge_set_window_surface", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetWindowSurface(SessionHandle handle, nint window, int attach);
    [DllImport(Library, EntryPoint = "player_bridge_set_video_visible", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetVideoVisible(SessionHandle handle, int visible);
    [DllImport(Library, EntryPoint = "player_bridge_set_chrome_backdrops", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetChromeBackdrops(SessionHandle handle, double bottomHeight, double titleHeight, uint flags);
}
