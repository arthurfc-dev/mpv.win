#pragma once

#include <stdint.h>

#if defined(PLAYER_NATIVE_EXPORTS)
#define PLAYER_API __declspec(dllexport)
#else
#define PLAYER_API __declspec(dllimport)
#endif

#ifdef __cplusplus
#define PLAYER_NOEXCEPT noexcept
extern "C" {
#else
#define PLAYER_NOEXCEPT
#endif

typedef struct player_session player_session;

// Option IDs match PlaybackOption in the managed frontend. Only this allowlist
// can be changed; embedding/rendering options are not exposed.
typedef struct player_setting_result {
    uint64_t request;
    int32_t status;
    int32_t option;
    wchar_t error[256];
} player_setting_result;

enum player_state {
    PLAYER_EMPTY = 0, PLAYER_LOADING = 1, PLAYER_PLAYING = 2,
    PLAYER_PAUSED = 3, PLAYER_ENDED = 4, PLAYER_ERROR = 5
};

// Windows ABI, default alignment 8. Strings are fixed-size UTF-16 buffers.
// Callers set size before reading; the bridge rejects incompatible layouts.
typedef struct player_snapshot {
    uint32_t size;
    int32_t state;
    double position;
    double duration;
    double volume;
    int32_t paused;
    int32_t has_video;
    int32_t video_ready;
    int32_t video_width;
    int32_t video_height;
    int32_t preparing;
    int64_t presented_frames;
    int64_t dropped_frames;
    double fps;
    double av_sync;
    wchar_t error[512];
    wchar_t decoder[64];
    wchar_t version[128];
    uint32_t tracks_revision;
    uint32_t media_generation;
    int32_t chapter_count;
    int32_t chapter;
    int32_t loop_file;
    uint32_t reserved2; // ABI 10: successful EOF followed by playlist idle.
    int32_t display_width;
    int32_t display_height;
    int32_t rotation;
    uint32_t reserved3; // ABI 11: mute in bit 0, audio-device cache revision above it.
    double subtitle_position;
} player_snapshot;

// type: 1 = embedded audio, 2 = embedded subtitle. IDs belong to libmpv.
typedef struct player_track {
    int64_t id;
    int32_t type;
    int32_t selected;
    wchar_t title[256];
    wchar_t language[64];
    wchar_t codec[64];
} player_track;

typedef struct player_audio_device {
    wchar_t name[128];
    wchar_t description[256];
} player_audio_device;

typedef struct player_chapter {
    double time;
    wchar_t title[256];
} player_chapter;

// Cached chapter metadata, bounded to 4096 entries. Original mpv order/indexes.
// capacity=0 reads count/revision/generation without copying entries.
PLAYER_API int32_t __cdecl player_bridge_get_chapters(player_session* session, player_chapter* chapters,
    uint32_t capacity, uint32_t stride, uint32_t* revision, uint32_t* generation) PLAYER_NOEXCEPT;

PLAYER_API int32_t __cdecl player_bridge_get_audio_devices(player_session* session, player_audio_device* devices,
    uint32_t capacity, uint32_t stride, wchar_t* selected, uint32_t selected_capacity, uint32_t* revision) PLAYER_NOEXCEPT;
// capacity=0 reads revision/kind only. kind: 0=video, 1=audio, 2=still image.
PLAYER_API int32_t __cdecl player_bridge_get_window_info(player_session* session, wchar_t* title, uint32_t capacity,
    uint32_t* revision, int32_t* kind) PLAYER_NOEXCEPT;

PLAYER_API uint32_t __cdecl player_bridge_get_api_version(void) PLAYER_NOEXCEPT;
// 0 means success. Errors are explained in the provided UTF-16 buffer.
// Optional cache directory enables mpv's validated on-disk shader bytecode cache.
PLAYER_API int32_t __cdecl player_bridge_create(player_session** session, wchar_t* error, uint32_t capacity, const wchar_t* shader_cache, const wchar_t* watch_later, int32_t zero_copy) PLAYER_NOEXCEPT;
// Cached media path and generation, including automatic folder-playlist changes.
PLAYER_API int32_t __cdecl player_bridge_get_media_path(player_session* session, wchar_t* path, uint32_t capacity, uint32_t* generation) PLAYER_NOEXCEPT;
PLAYER_API void __cdecl player_bridge_destroy(player_session* session) PLAYER_NOEXCEPT;
// The auto-reset event is borrowed and remains valid until session destruction.
PLAYER_API void* __cdecl player_bridge_get_state_event(player_session* session) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_get_snapshot(player_session* session, player_snapshot* snapshot) PLAYER_NOEXCEPT;
// UI thread only: system compositor behind the WinUI child. Detach before destroy.
PLAYER_API int32_t __cdecl player_bridge_set_window_surface(player_session* session, void* window, int32_t attach) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_set_video_visible(player_session* session, int32_t visible) PLAYER_NOEXCEPT;
// UI thread only. Physical heights; flags: bottom=1, title=2, animate=4.
PLAYER_API int32_t __cdecl player_bridge_set_chrome_backdrops(player_session* session, double bottom_height, double title_height, uint32_t flags) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_resize(player_session* session, uint32_t width, uint32_t height, double scale_x, double scale_y) PLAYER_NOEXCEPT;
// These enqueue mpv commands; media I/O and decoding do not run on the caller.
PLAYER_API int32_t __cdecl player_bridge_open(player_session* session, const wchar_t* path) PLAYER_NOEXCEPT;
// Acknowledge the selected video's initial window fit before starting its clock.
// Stale generations are ignored. The render worker must still present the frame.
PLAYER_API int32_t __cdecl player_bridge_prepare_playback(player_session* session, uint32_t generation) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_set_paused(player_session* session, int32_t paused) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_seek(player_session* session, double seconds, int32_t exact) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_set_setting(player_session* session, int32_t option, const wchar_t* value, uint64_t request) PLAYER_NOEXCEPT;
// Pops a cached completion: 1=result, 0=empty. Does not call mpv.
PLAYER_API int32_t __cdecl player_bridge_take_setting_result(player_session* session, player_setting_result* result, uint32_t size) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_set_volume(player_session* session, double volume) PLAYER_NOEXCEPT;
// Returns total count, copying at most capacity entries. Retry if count > capacity.
// Only reads cached metadata; it does not call mpv or do media I/O.
PLAYER_API int32_t __cdecl player_bridge_get_tracks(player_session* session, player_track* tracks, uint32_t capacity, uint32_t stride, uint32_t* revision, uint32_t* generation) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_select_track(player_session* session, int32_t type, int64_t id, uint32_t generation) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_change_chapter(player_session* session, int32_t direction, uint32_t generation) PLAYER_NOEXCEPT;
PLAYER_API int32_t __cdecl player_bridge_set_loop(player_session* session, int32_t enabled) PLAYER_NOEXCEPT;
// Fraction of the subtitle canvas reserved for the visible bottom controls.
// Zero restores the user's baseline. Does not alter subtitle styles.
PLAYER_API int32_t __cdecl player_bridge_set_subtitle_clearance(player_session* session, double fraction, double baseline) PLAYER_NOEXCEPT;

#ifdef __cplusplus
}
#endif
