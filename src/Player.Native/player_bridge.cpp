#include "player_bridge.h"
#include <Windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <wrl/client.h>
#include <DispatcherQueue.h>
#include <windows.ui.composition.interop.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Composition.Desktop.h>
#include <mpv/client.h>
#include <mpv/render_d3d11.h>
#include <algorithm>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstring>
#include <future>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <deque>
#include <unordered_map>
#include <cstdlib>
#include <vector>

using Microsoft::WRL::ComPtr;
extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace {
void check(HRESULT result) {
    if (FAILED(result)) {
        char message[80]{};
        sprintf_s(message, "Direct3D operation failed (0x%08X).", static_cast<unsigned>(result));
        throw std::runtime_error(message);
    }
}
std::string utf8(const wchar_t* text) {
    const int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, -1, nullptr, 0, nullptr, nullptr);
    if (!count) throw std::runtime_error("Invalid file name.");
    std::string result(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, -1, result.data(), count, nullptr, nullptr);
    result.pop_back();
    return result;
}
void wide(wchar_t* target, size_t count, const char* text) noexcept {
    if (!count) return;
    target[0] = 0;
    if (!text) return;
    // Bound the source rather than let a truncated conversion leave stale text.
    const int length = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
    if (length > 0 && static_cast<size_t>(length) <= count)
        MultiByteToWideChar(CP_UTF8, 0, text, -1, target, static_cast<int>(count));
    else
        wcscpy_s(target, count, L"Playback operation failed.");
}
struct MpvApi {
    HMODULE module{};
#define API(name) decltype(&mpv_##name) name{};
    API(create) API(initialize) API(terminate_destroy) API(set_option_string)
    API(command_async) API(set_property_async) API(observe_property) API(get_property) API(get_property_string)
    API(free) API(wait_event) API(wakeup) API(error_string)
    API(render_context_create) API(render_context_free) API(render_context_set_update_callback)
    API(render_context_update) API(render_context_render) API(render_context_report_swap)
#undef API
    MpvApi() {
        wchar_t path[32768]{};
        if (!GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), path, ARRAYSIZE(path)))
            throw std::runtime_error("Could not locate the playback bridge.");
        std::wstring dll(path);
        dll.resize(dll.find_last_of(L"\\/") + 1);
        dll += L"libmpv-2.dll";
        module = LoadLibraryExW(dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!module) throw std::runtime_error("Could not load libmpv-2.dll. Restore the playback dependencies and rebuild.");
        try {
#define LOAD(name) name = std::bit_cast<decltype(name)>(GetProcAddress(module, "mpv_" #name)); if (!name) throw std::runtime_error("The libmpv build is missing " #name ".");
            LOAD(create) LOAD(initialize) LOAD(terminate_destroy) LOAD(set_option_string)
            LOAD(command_async) LOAD(set_property_async) LOAD(observe_property) LOAD(get_property) LOAD(get_property_string)
            LOAD(free) LOAD(wait_event) LOAD(wakeup) LOAD(error_string)
            LOAD(render_context_create) LOAD(render_context_free) LOAD(render_context_set_update_callback)
            LOAD(render_context_update) LOAD(render_context_render) LOAD(render_context_report_swap)
#undef LOAD
        } catch (...) { FreeLibrary(module); module = nullptr; throw; }
    }
    ~MpvApi() { if (module) FreeLibrary(module); }
};
const mpv_node* field(const mpv_node& node, const char* key) noexcept {
    if (node.format != MPV_FORMAT_NODE_MAP || !node.u.list) return nullptr;
    for (int i = 0; i < node.u.list->num; ++i)
        if (node.u.list->keys[i] && strcmp(node.u.list->keys[i], key) == 0) return &node.u.list->values[i];
    return nullptr;
}
const char* text_field(const mpv_node& node, const char* key) noexcept {
    const auto* value = field(node, key);
    return value && value->format == MPV_FORMAT_STRING && value->u.string ? value->u.string : "";
}
bool flag_field(const mpv_node& node, const char* key) noexcept {
    const auto* value = field(node, key);
    return value && value->format == MPV_FORMAT_FLAG && value->u.flag;
}
void label(wchar_t* output, size_t capacity, const char* input) {
    int length = MultiByteToWideChar(CP_UTF8, 0, input, -1, nullptr, 0);
    if (length <= 0) { output[0] = 0; return; }
    std::wstring converted(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, input, -1, converted.data(), length);
    size_t count = std::min(capacity - 1, static_cast<size_t>(length - 1));
    if (count && converted[count - 1] >= 0xD800 && converted[count - 1] <= 0xDBFF) --count;
    wmemcpy(output, converted.data(), count);
    output[count] = 0;
}
std::vector<player_track> parse_tracks(const mpv_node& node) {
    std::vector<player_track> result;
    if (node.format != MPV_FORMAT_NODE_ARRAY || !node.u.list) return result;
    for (int i = 0; i < node.u.list->num; ++i) {
        const auto& entry = node.u.list->values[i];
        const char* type = text_field(entry, "type");
        const auto* id = field(entry, "id");
        if (!id || id->format != MPV_FORMAT_INT64) continue;
        player_track track{};
        track.type = strcmp(type, "audio") == 0 ? 1 : strcmp(type, "sub") == 0 ? 2 : 0;
        if (!track.type) continue;
        // External audio is now explicitly opt-in. External subtitles remain deferred.
        if (track.type == 2 && flag_field(entry, "external")) continue;
        track.id = id->u.int64;
        track.selected = flag_field(entry, "selected") ? 1 : 0;
        label(track.title, ARRAYSIZE(track.title), text_field(entry, "title"));
        label(track.language, ARRAYSIZE(track.language), text_field(entry, "lang"));
        label(track.codec, ARRAYSIZE(track.codec), text_field(entry, "codec"));
        result.push_back(track);
    }
    return result;
}
}

struct player_session {
    const std::wstring shader_cache_path;
    const std::wstring watch_later_path;
    MpvApi api;
    mpv_handle* core{};
    mpv_render_context* renderer{}; // Render thread only.
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain2> swap;
    // UI-thread lifetime. Video and chrome share the system-compositor tree.
    winrt::Windows::System::DispatcherQueueController composition_queue{nullptr};
    winrt::Windows::UI::Composition::Compositor system_compositor{nullptr};
    winrt::Windows::UI::Composition::Desktop::DesktopWindowTarget system_target{nullptr};
    winrt::Windows::UI::Composition::SpriteVisual bottom_backdrop{nullptr};
    winrt::Windows::UI::Composition::ContainerVisual title_backdrop{nullptr};
    winrt::Windows::UI::Composition::SpriteVisual title_band{nullptr};
    bool bottom_shown{}, title_shown{};
    HANDLE state_event{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    HANDLE render_event{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    std::thread event_thread;
    std::thread render_thread;
    std::atomic<bool> stopping{};
    std::atomic<uint64_t> request_id{1};
    std::atomic<uint64_t> open_request{};
    // Serializes autoplay with replacement and explicit pause requests. Never
    // acquired by rendering; ordinary mpv commands stay off the render worker.
    std::mutex playback_mutex;
    std::mutex state_mutex;
    bool layout_ready{}, resume_pending{}; // Protected by state_mutex.
    bool explicit_open_pending{}, ended_at_eof{}; // state_mutex
    std::wstring media_path; // state_mutex
    player_snapshot state{};
    std::vector<player_track> tracks;
    std::vector<player_chapter> chapters;
    uint32_t chapters_revision{}; // state_mutex
    std::vector<player_audio_device> audio_devices;
    std::wstring audio_device{L"auto"};
    uint32_t audio_revision{}; // state_mutex, also encoded in snapshot reserved3.
    std::wstring media_title;
    uint32_t window_revision{};
    int32_t media_kind{}; // state_mutex
    std::unordered_map<uint64_t, int32_t> pending_settings; // state_mutex
    std::deque<player_setting_result> setting_results; // state_mutex
    std::mutex size_mutex;
    UINT width{1}, height{1};
    double scale_x{1}, scale_y{1};
    bool resize_pending{true};

    explicit player_session(const wchar_t* shader_cache, const wchar_t* watch_later) : shader_cache_path(shader_cache ? shader_cache : L""), watch_later_path(watch_later ? watch_later : L"") {
        state.size = sizeof(state);
        state.volume = 100;
        state.chapter = -1;
        state.subtitle_position = 100;
        if (!state_event || !render_event) throw std::runtime_error("Could not create playback events.");
    }
    ~player_session() {
        stopping = true;
        if (core) api.wakeup(core);
        if (render_event) SetEvent(render_event);
        if (render_thread.joinable()) render_thread.join();
        if (event_thread.joinable()) event_thread.join();
        if (core) api.terminate_destroy(core);
        if (state_event) CloseHandle(state_event);
        if (render_event) CloseHandle(render_event);
    }
    void fail(const char* error) noexcept {
        std::lock_guard lock(state_mutex);
        state.state = PLAYER_ERROR;
        state.video_ready = 0;
        state.preparing = 0;
        resume_pending = false;
        wide(state.error, ARRAYSIZE(state.error), error);
        SetEvent(state_event);
    }
    void mpv_check(int result) {
        if (result < 0) throw std::runtime_error(api.error_string(result));
    }
    void initialize(bool zero_copy) {
        core = api.create();
        if (!core) throw std::runtime_error("libmpv could not create a playback instance.");
        const char* options[][2] = {
            {"vo", "libmpv"}, {"hwdec", "auto"}, {"config", "no"}, {"terminal", "no"},
            {"input-default-bindings", "no"}, {"input-vo-keyboard", "no"}, {"osc", "no"},
            {"osd-level", "0"}, {"keep-open", "yes"}, {"keep-open-pause", "yes"},
            {"idle", "yes"}, {"directory-filter-types", "video"}, {"directory-mode", "ignore"},
            {"playlist-inherit-options", "current"},
            {"sub-auto", "no"}, {"audio-file-auto", "no"},
            {"keepaspect", "yes"}, {"panscan", "0"}, {"video-zoom", "0"}
        };
        for (const auto& option : options) mpv_check(api.set_option_string(core, option[0], option[1]));
        mpv_check(api.set_option_string(core, "d3d11va-zero-copy", zero_copy ? "yes" : "no"));
        if (!watch_later_path.empty()) {
            const auto path = utf8(watch_later_path.c_str());
            mpv_check(api.set_option_string(core, "watch-later-dir", path.c_str()));
        }
        // config=no also removes mpv's default cache location. Supply our own
        // optional cache so later launches can reuse validated D3D11 bytecode.
        if (!shader_cache_path.empty()) {
            const auto cache = utf8(shader_cache_path.c_str());
            mpv_check(api.set_option_string(core, "gpu-shader-cache-dir", cache.c_str()));
        }
        // Opt-in engine diagnostics for startup/render scheduling investigations.
        wchar_t log_path[32768]{};
        const DWORD log_length = GetEnvironmentVariableW(L"PLAYER_MPV_LOG_PATH", log_path, ARRAYSIZE(log_path));
        if (log_length > 0 && log_length < ARRAYSIZE(log_path)) {
            const auto path = utf8(log_path);
            mpv_check(api.set_option_string(core, "log-file", path.c_str()));
            mpv_check(api.set_option_string(core, "msg-level", "all=debug"));
        }
        mpv_check(api.initialize(core));
        char* version = api.get_property_string(core, "mpv-version");
        wide(state.version, ARRAYSIZE(state.version), version);
        api.free(version);
        const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<IDXGIDevice1> dxgi_device;
        check(device.As(&dxgi_device));
        check(dxgi_device->SetMaximumFrameLatency(1));
        ComPtr<IDXGIAdapter> adapter;
        check(dxgi_device->GetAdapter(&adapter));
        ComPtr<IDXGIFactory2> factory;
        check(adapter->GetParent(IID_PPV_ARGS(&factory)));
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = 1; desc.Height = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.Scaling = DXGI_SCALING_STRETCH;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        ComPtr<IDXGISwapChain1> created;
        check(factory->CreateSwapChainForComposition(device.Get(), &desc, nullptr, &created));
        check(created.As(&swap));
        const struct { const char* name; mpv_format format; } properties[]{
            {"time-pos", MPV_FORMAT_DOUBLE}, {"duration", MPV_FORMAT_DOUBLE},
            {"pause", MPV_FORMAT_FLAG}, {"eof-reached", MPV_FORMAT_FLAG},
            {"volume", MPV_FORMAT_DOUBLE}, {"hwdec-current", MPV_FORMAT_STRING},
            {"frame-drop-count", MPV_FORMAT_INT64}, {"avsync", MPV_FORMAT_DOUBLE},
            {"track-list", MPV_FORMAT_NODE}, {"chapters", MPV_FORMAT_INT64},
            {"chapter", MPV_FORMAT_INT64}, {"loop-file", MPV_FORMAT_STRING},
            {"dwidth", MPV_FORMAT_INT64}, {"dheight", MPV_FORMAT_INT64},
            {"video-out-params/rotate", MPV_FORMAT_INT64}, {"sub-pos", MPV_FORMAT_DOUBLE}, {"idle-active", MPV_FORMAT_FLAG},
            {"mute", MPV_FORMAT_FLAG}, {"audio-device", MPV_FORMAT_STRING}, {"audio-device-list", MPV_FORMAT_NODE},
            {"media-title", MPV_FORMAT_STRING}, {"chapter-list", MPV_FORMAT_NODE}
        };
        uint64_t id = 1;
        for (const auto& property : properties)
            mpv_check(api.observe_property(core, id++, property.name, property.format));
        event_thread = std::thread([this] { events(); });
        std::promise<void> ready;
        auto completion = ready.get_future();
        render_thread = std::thread([this, ready = std::move(ready)]() mutable { render(std::move(ready)); });
        completion.get();
    }
    void events() noexcept {
        try { process_events(); }
        catch (const std::exception& ex) { fail(ex.what()); }
        catch (...) { fail("Playback event processing failed."); }
    }
    void process_events() {
        ULONGLONG last_progress = 0;
        while (!stopping) {
            {
                std::lock_guard playback_lock(playback_mutex);
                bool resume = false;
                {
                    std::lock_guard lock(state_mutex);
                    if (resume_pending && state.preparing && state.state != PLAYER_ERROR) {
                        resume_pending = false;
                        state.preparing = 0;
                        resume = true;
                    }
                }
                if (resume) {
                    const char* play[]{"set", "pause", "no", nullptr};
                    const int result = command(play);
                    if (result < 0) fail(api.error_string(result));
                    else SetEvent(state_event);
                }
            }
            const mpv_event* event = api.wait_event(core, -1);
            if (stopping) break;
            // Never hold a lock needed by rendering during ordinary mpv calls.
            int64_t video_width = 0, video_height = 0;
            int64_t display_width = 0, display_height = 0, rotation = 0;
            double fps = 0;
            std::wstring current_path;
            if (event->event_id == MPV_EVENT_START_FILE || event->event_id == MPV_EVENT_FILE_LOADED) {
                char* path = api.get_property_string(core, "path");
                if (path) { wchar_t converted[32768]{}; wide(converted, ARRAYSIZE(converted), path); current_path = converted; }
                api.free(path);
            }
            if (event->event_id == MPV_EVENT_START_FILE) {
                const char* args[]{"set", "pause", "yes", nullptr};
                command(args);
            }
            if (event->event_id == MPV_EVENT_FILE_LOADED) {
                api.get_property(core, "width", MPV_FORMAT_INT64, &video_width);
                api.get_property(core, "height", MPV_FORMAT_INT64, &video_height);
                api.get_property(core, "container-fps", MPV_FORMAT_DOUBLE, &fps);
                api.get_property(core, "dwidth", MPV_FORMAT_INT64, &display_width);
                api.get_property(core, "dheight", MPV_FORMAT_INT64, &display_height);
                api.get_property(core, "video-out-params/rotate", MPV_FORMAT_INT64, &rotation);
            }
            bool notify = true;
            std::lock_guard lock(state_mutex);
            switch (event->event_id) {
            case MPV_EVENT_SET_PROPERTY_REPLY: {
                auto pending = pending_settings.find(event->reply_userdata);
                if (pending != pending_settings.end()) {
                    player_setting_result result{};
                    result.request = event->reply_userdata;
                    result.status = event->error;
                    result.option = pending->second;
                    if (event->error < 0) wide(result.error, ARRAYSIZE(result.error), api.error_string(event->error));
                    setting_results.push_back(result);
                    pending_settings.erase(pending);
                }
                break;
            }
            case MPV_EVENT_START_FILE:
                media_kind = 0; media_title.clear(); ++window_revision;
                if (!explicit_open_pending) ++state.media_generation;
                explicit_open_pending = false;
                ended_at_eof = false; state.reserved2 = 0;
                if (!current_path.empty()) media_path = current_path;
                state.preparing = 1; layout_ready = resume_pending = false;
                state.state = PLAYER_LOADING; state.video_ready = 0; state.error[0] = 0;
                state.position = 0; state.duration = 0; state.presented_frames = 0;
                state.has_video = 0; state.video_width = 0; state.video_height = 0;
                state.display_width = 0; state.display_height = 0; state.rotation = 0;
                tracks.clear(); ++state.tracks_revision;
                chapters.clear(); ++chapters_revision;
                state.chapter_count = 0; state.chapter = -1;
                break;
            case MPV_EVENT_FILE_LOADED: {
                if (!current_path.empty()) media_path = current_path;
                state.fps = fps;
                state.video_width = static_cast<int32_t>(video_width);
                state.video_height = static_cast<int32_t>(video_height);
                state.has_video = video_width > 0 && video_height > 0;
                if (!state.has_video) { media_kind = 1; ++window_revision; }
                state.display_width = static_cast<int32_t>(display_width);
                state.display_height = static_cast<int32_t>(display_height);
                state.rotation = static_cast<int32_t>(rotation);
                state.state = state.paused ? PLAYER_PAUSED : PLAYER_PLAYING;
                // Audio-only media has no video frame/window fit to wait for.
                if (state.preparing && !state.has_video) resume_pending = true;
                break;
            }
            case MPV_EVENT_END_FILE: {
                const auto* end = static_cast<mpv_event_end_file*>(event->data);
                ended_at_eof = end->reason == MPV_END_FILE_REASON_EOF;
                if (end->reason == MPV_END_FILE_REASON_ERROR) {
                    state.state = PLAYER_ERROR; state.video_ready = 0;
                    state.preparing = 0; resume_pending = false;
                    wide(state.error, ARRAYSIZE(state.error), api.error_string(end->error));
                }
                break;
            }
            case MPV_EVENT_COMMAND_REPLY:
                if (event->error < 0 && event->reply_userdata == open_request.load()) {
                    state.state = PLAYER_ERROR; state.video_ready = 0;
                    state.preparing = 0; resume_pending = false;
                    wide(state.error, ARRAYSIZE(state.error), api.error_string(event->error));
                }
                break;
            case MPV_EVENT_PROPERTY_CHANGE: {
                const auto* p = static_cast<mpv_event_property*>(event->data);
                if (!p->data) {
                    if (event->reply_userdata == 9) { tracks.clear(); ++state.tracks_revision; }
                    else if (event->reply_userdata == 10) state.chapter_count = 0;
                    else if (event->reply_userdata == 11) state.chapter = -1;
                    else if (event->reply_userdata == 22) { chapters.clear(); ++chapters_revision; }
                    else notify = false;
                    break;
                }
                switch (event->reply_userdata) {
                case 1:
                    state.position = *static_cast<double*>(p->data);
                    notify = GetTickCount64() - last_progress >= 100;
                    if (notify) last_progress = GetTickCount64();
                    break;
                case 2: state.duration = *static_cast<double*>(p->data); break;
                case 3:
                    state.paused = *static_cast<int*>(p->data);
                    if (state.state == PLAYER_PLAYING || state.state == PLAYER_PAUSED)
                        state.state = state.paused ? PLAYER_PAUSED : PLAYER_PLAYING;
                    break;
                case 4:
                    if (*static_cast<int*>(p->data)) state.state = PLAYER_ENDED;
                    else if (state.state == PLAYER_ENDED) state.state = state.paused ? PLAYER_PAUSED : PLAYER_PLAYING;
                    break;
                case 5: state.volume = *static_cast<double*>(p->data); break;
                case 6: wide(state.decoder, ARRAYSIZE(state.decoder), *static_cast<char**>(p->data)); break;
                case 7: state.dropped_frames = *static_cast<int64_t*>(p->data); notify = false; break;
                case 8: state.av_sync = *static_cast<double*>(p->data); notify = false; break;
                case 9:
                    tracks = parse_tracks(*static_cast<mpv_node*>(p->data));
                    {
                        const auto& track_node = *static_cast<mpv_node*>(p->data);
                        int32_t detected_kind = 1;
                        if (track_node.format == MPV_FORMAT_NODE_ARRAY && track_node.u.list)
                            for (int i = 0; i < track_node.u.list->num; ++i) {
                                const auto& entry = track_node.u.list->values[i];
                                if (strcmp(text_field(entry, "type"), "video") == 0 && !flag_field(entry, "albumart")) {
                                    detected_kind = flag_field(entry, "image") ? 2 : 0; break;
                                }
                            }
                        if (detected_kind != media_kind) { media_kind = detected_kind; ++window_revision; }
                    }
                    ++state.tracks_revision;
                    break;
                case 10: state.chapter_count = static_cast<int32_t>(std::clamp<int64_t>(*static_cast<int64_t*>(p->data), 0, INT32_MAX)); break;
                case 11: state.chapter = static_cast<int32_t>(std::clamp<int64_t>(*static_cast<int64_t*>(p->data), -1, INT32_MAX)); break;
                case 12: {
                    const char* value = *static_cast<char**>(p->data);
                    state.loop_file = value && strcmp(value, "no") != 0 && strcmp(value, "0") != 0;
                    break;
                }
                case 13: state.display_width = static_cast<int32_t>(std::clamp<int64_t>(*static_cast<int64_t*>(p->data), 0, INT32_MAX)); break;
                case 14: state.display_height = static_cast<int32_t>(std::clamp<int64_t>(*static_cast<int64_t*>(p->data), 0, INT32_MAX)); break;
                case 15: state.rotation = static_cast<int32_t>(*static_cast<int64_t*>(p->data) % 360); break;
                case 16: state.subtitle_position = *static_cast<double*>(p->data); break;
                case 17:
                    if (*static_cast<int*>(p->data) && ended_at_eof) {
                        state.state = PLAYER_EMPTY; state.has_video = state.video_ready = state.preparing = 0;
                        resume_pending = false; state.reserved2 = 1;
                    }
                    break;
                case 18:
                    state.reserved3 = (state.reserved3 & ~1u) | (*static_cast<int*>(p->data) ? 1u : 0u);
                    break;
                case 19: {
                    wchar_t selected[128]{}; wide(selected, ARRAYSIZE(selected), *static_cast<char**>(p->data));
                    audio_device = selected;
                    state.reserved3 = (++audio_revision << 1) | (state.reserved3 & 1u);
                    break;
                }
                case 20: {
                    const auto& node = *static_cast<mpv_node*>(p->data);
                    audio_devices.clear();
                    if (node.format == MPV_FORMAT_NODE_ARRAY && node.u.list)
                        for (int i = 0; i < node.u.list->num && audio_devices.size() < 256; ++i) {
                            const auto& entry = node.u.list->values[i];
                            player_audio_device output_device{};
                            label(output_device.name, ARRAYSIZE(output_device.name), text_field(entry, "name"));
                            label(output_device.description, ARRAYSIZE(output_device.description), text_field(entry, "description"));
                            if (*output_device.name) audio_devices.push_back(output_device);
                        }
                    state.reserved3 = (++audio_revision << 1) | (state.reserved3 & 1u);
                    break;
                }
                case 21: {
                    wchar_t title[2048]{}; label(title, ARRAYSIZE(title), *static_cast<char**>(p->data));
                    media_title = title; ++window_revision; break;
                }
                case 22: {
                    const auto& node = *static_cast<mpv_node*>(p->data);
                    chapters.clear();
                    if (node.format == MPV_FORMAT_NODE_ARRAY && node.u.list)
                        for (int i = 0; i < node.u.list->num && i < 4096; ++i) {
                            const auto& entry = node.u.list->values[i];
                            const auto* time = field(entry, "time");
                            player_chapter chapter{};
                            // Retain indexes even when a malformed timestamp cannot be drawn.
                            chapter.time = time && time->format == MPV_FORMAT_DOUBLE && std::isfinite(time->u.double_)
                                ? time->u.double_ : -1;
                            label(chapter.title, ARRAYSIZE(chapter.title), text_field(entry, "title"));
                            chapters.push_back(chapter);
                        }
                    ++chapters_revision;
                    break;
                }
                default: notify = false; break;
                }
                break;
            }
            default: notify = false; break;
            }
            if (notify) SetEvent(state_event);
        }
    }
    static void update(void* opaque) noexcept {
        SetEvent(static_cast<player_session*>(opaque)->render_event);
    }
    void render(std::promise<void> ready) noexcept {
        bool initialized = false;
        try {
            mpv_d3d11_init_params d3d{device.Get()};
            // Use libmpv's default render scheduling mode.
            mpv_render_param init[]{
                {MPV_RENDER_PARAM_API_TYPE, const_cast<char*>(MPV_RENDER_API_TYPE_D3D11)},
                {MPV_RENDER_PARAM_D3D11_INIT_PARAMS, &d3d}, {MPV_RENDER_PARAM_INVALID, nullptr}
            };
            mpv_check(api.render_context_create(&renderer, core, init));
            api.render_context_set_update_callback(renderer, update, this);
            ready.set_value(); initialized = true;
            SetEvent(render_event);
            while (!stopping) {
                WaitForSingleObject(render_event, INFINITE);
                if (stopping) break;
                bool resized = false;
                uint32_t next_width = 0, next_height = 0;
                double next_scale_x = 1, next_scale_y = 1;
                {
                    std::lock_guard lock(size_mutex);
                    if (resize_pending) {
                        next_width = width; next_height = height;
                        next_scale_x = scale_x; next_scale_y = scale_y;
                        resize_pending = false; resized = true;
                    }
                }
                // Never hold the request mutex while DXGI waits for the GPU.
                // WM_SIZE must be able to publish newer dimensions immediately.
                if (resized) {
                    context->ClearState();
                    check(swap->ResizeBuffers(0, next_width, next_height, DXGI_FORMAT_UNKNOWN, 0));
                    DXGI_MATRIX_3X2_F matrix{};
                    matrix._11 = static_cast<float>(1.0 / next_scale_x);
                    matrix._22 = static_cast<float>(1.0 / next_scale_y);
                    check(swap->SetMatrixTransform(&matrix));
                }
                const auto flags = api.render_context_update(renderer);
                uint32_t generation;
                bool prepare_frame;
                {
                    std::lock_guard lock(state_mutex);
                    generation = state.media_generation;
                    prepare_frame = state.preparing && layout_ready && state.has_video && state.video_ready;
                }
                if (!(flags & MPV_RENDER_UPDATE_FRAME) && !resized && !prepare_frame) continue;
                ComPtr<ID3D11Texture2D> texture;
                check(swap->GetBuffer(0, IID_PPV_ARGS(&texture)));
                mpv_d3d11_fbo fbo{texture.Get()};
                int block_for_target = resized ? 0 : 1;
                mpv_render_param params[]{{MPV_RENDER_PARAM_D3D11_FBO, &fbo},
                    {MPV_RENDER_PARAM_BLOCK_FOR_TARGET_TIME, &block_for_target}, {MPV_RENDER_PARAM_INVALID, nullptr}};
                mpv_check(api.render_context_render(renderer, params));
                check(swap->Present(1, 0));
                api.render_context_report_swap(renderer);
                {
                    std::lock_guard lock(state_mutex);
                    if (generation == state.media_generation && state.has_video &&
                        (state.video_ready || (flags & MPV_RENDER_UPDATE_FRAME)) &&
                        state.state != PLAYER_ERROR && state.state != PLAYER_LOADING) {
                        ++state.presented_frames;
                        if (!state.video_ready) { state.video_ready = 1; SetEvent(state_event); }
                        if (state.preparing && layout_ready) {
                            std::lock_guard size_lock(size_mutex);
                            if (!resize_pending) {
                                resume_pending = true;
                                // Explicitly render-thread-safe according to client.h.
                                api.wakeup(core);
                            }
                        }
                    }
                }
            }
        } catch (...) {
            if (!initialized) ready.set_exception(std::current_exception());
            else {
                try { throw; }
                catch (const std::exception& ex) { fail(ex.what()); }
                catch (...) { fail("Video rendering stopped unexpectedly."); }
            }
        }
        if (renderer) {
            api.render_context_set_update_callback(renderer, nullptr, nullptr);
            api.render_context_free(renderer); renderer = nullptr;
        }
    }
    int command(const char** args, uint64_t id = 0) noexcept {
        return api.command_async(core, id ? id : request_id.fetch_add(1), args);
    }
};

static_assert(sizeof(player_snapshot) == 1544);
static_assert(sizeof(player_track) == 784);
static_assert(sizeof(player_setting_result) == 528);
static_assert(sizeof(player_audio_device) == 768);
static_assert(sizeof(player_chapter) == 520);
uint32_t __cdecl player_bridge_get_api_version() noexcept { return 15; }
int32_t __cdecl player_bridge_create(player_session** output, wchar_t* error, uint32_t capacity, const wchar_t* shader_cache, const wchar_t* watch_later, int32_t zero_copy) noexcept {
    if (!output || !error || !capacity || (zero_copy != 0 && zero_copy != 1)) return -1;
    *output = nullptr; error[0] = 0;
    try {
        auto session = std::make_unique<player_session>(shader_cache, watch_later);
        session->initialize(zero_copy != 0);
        *output = session.release(); return 0;
    } catch (const std::exception& ex) { wide(error, capacity, ex.what()); }
    catch (...) { wide(error, capacity, "Could not initialize playback."); }
    return -1;
}
void __cdecl player_bridge_destroy(player_session* session) noexcept { delete session; }
int32_t __cdecl player_bridge_get_window_info(player_session* session, wchar_t* title, uint32_t capacity,
    uint32_t* revision, int32_t* kind) noexcept {
    if (!session || !revision || !kind || (capacity && !title)) return -1;
    std::lock_guard lock(session->state_mutex);
    *revision = session->window_revision; *kind = session->media_kind;
    if (capacity) wcsncpy_s(title, capacity, session->media_title.c_str(), _TRUNCATE);
    return 0;
}
int32_t __cdecl player_bridge_get_audio_devices(player_session* session, player_audio_device* devices,
    uint32_t capacity, uint32_t stride, wchar_t* selected, uint32_t selected_capacity, uint32_t* revision) noexcept {
    if (!session || !selected || selected_capacity < 128 || !revision || stride != sizeof(player_audio_device) || (capacity && !devices)) return -1;
    std::lock_guard lock(session->state_mutex);
    *revision = session->audio_revision;
    wcscpy_s(selected, selected_capacity, session->audio_device.c_str());
    const auto count = std::min(static_cast<size_t>(capacity), session->audio_devices.size());
    if (count) std::copy_n(session->audio_devices.data(), count, devices);
    return static_cast<int32_t>(session->audio_devices.size());
}
int32_t __cdecl player_bridge_get_media_path(player_session* session, wchar_t* path, uint32_t capacity, uint32_t* generation) noexcept {
    if (!session || !path || !generation || capacity < 1) return -1;
    std::lock_guard lock(session->state_mutex);
    if (session->media_path.size() >= capacity) return -1;
    *generation = session->state.media_generation;
    wcscpy_s(path, capacity, session->media_path.c_str()); return 0;
}
void* __cdecl player_bridge_get_state_event(player_session* session) noexcept { return session ? session->state_event : nullptr; }
int32_t __cdecl player_bridge_get_snapshot(player_session* session, player_snapshot* output) noexcept {
    if (!session || !output || output->size != sizeof(player_snapshot)) return -1;
    std::lock_guard lock(session->state_mutex); *output = session->state; return 0;
}
int32_t __cdecl player_bridge_set_window_surface(player_session* session, void* window, int32_t attach) noexcept {
    if (!session || !window) return E_INVALIDARG;
    try {
        namespace composition = winrt::Windows::UI::Composition;
        if (!attach) {
            if (session->system_target) session->system_target.Close();
            session->system_target = nullptr;
            session->bottom_backdrop = nullptr;
            session->title_backdrop = nullptr;
            session->title_band = nullptr;
            session->system_compositor = nullptr;
            if (session->composition_queue) {
                session->composition_queue.ShutdownQueueAsync();
                session->composition_queue = nullptr;
            }
            return S_OK;
        }
        if (session->system_target) return E_UNEXPECTED;
        if (!winrt::Windows::System::DispatcherQueue::GetForCurrentThread()) {
            DispatcherQueueOptions options{sizeof(options), DQTYPE_THREAD_CURRENT, DQTAT_COM_STA};
            winrt::check_hresult(CreateDispatcherQueueController(options,
                reinterpret_cast<PDISPATCHERQUEUECONTROLLER*>(winrt::put_abi(session->composition_queue))));
        }
        session->system_compositor = composition::Compositor();
        auto desktop = session->system_compositor.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>();
        winrt::check_hresult(desktop->CreateDesktopWindowTarget(static_cast<HWND>(window), FALSE,
            reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(winrt::put_abi(session->system_target))));
        composition::ICompositionSurface surface{nullptr};
        auto interop = session->system_compositor.as<ABI::Windows::UI::Composition::ICompositorInterop>();
        winrt::check_hresult(interop->CreateCompositionSurfaceForSwapChain(session->swap.Get(),
            reinterpret_cast<ABI::Windows::UI::Composition::ICompositionSurface**>(winrt::put_abi(surface))));
        auto brush = session->system_compositor.CreateSurfaceBrush(surface);
        brush.Stretch(composition::CompositionStretch::Fill);
        auto visual = session->system_compositor.CreateSpriteVisual();
        visual.IsVisible(false);
        // DWM covers new client bounds immediately while the render worker
        // replaces its buffers. Normal sizing preserves the video's aspect.
        visual.RelativeSizeAdjustment({1, 1});
        visual.Brush(brush);
        auto root = session->system_compositor.CreateContainerVisual();
        root.RelativeSizeAdjustment({1, 1});
        root.IsVisible(false);
        visual.IsVisible(true);
        root.Children().InsertAtBottom(visual);

        auto bottom_brush = session->system_compositor.CreateLinearGradientBrush();
        bottom_brush.StartPoint({0, 0}); bottom_brush.EndPoint({0, 1});
        bottom_brush.ColorStops().Append(session->system_compositor.CreateColorGradientStop(0, {0, 0, 0, 0}));
        bottom_brush.ColorStops().Append(session->system_compositor.CreateColorGradientStop(0.45f, {166, 0, 0, 0}));
        bottom_brush.ColorStops().Append(session->system_compositor.CreateColorGradientStop(1, {217, 0, 0, 0}));
        session->bottom_backdrop = session->system_compositor.CreateSpriteVisual();
        session->bottom_backdrop.Brush(bottom_brush);
        session->bottom_backdrop.RelativeSizeAdjustment({1, 0});
        session->bottom_backdrop.RelativeOffsetAdjustment({0, 1, 0});
        session->bottom_backdrop.AnchorPoint({0, 1});
        session->bottom_backdrop.Opacity(0);
        root.Children().InsertAtTop(session->bottom_backdrop);

        session->title_backdrop = session->system_compositor.CreateContainerVisual();
        session->title_backdrop.RelativeSizeAdjustment({1, 0});
        session->title_backdrop.Opacity(0);
        auto title_brush = session->system_compositor.CreateLinearGradientBrush();
        title_brush.StartPoint({0, 0}); title_brush.EndPoint({0, 1});
        title_brush.ColorStops().Append(session->system_compositor.CreateColorGradientStop(0, {48, 0, 0, 0}));
        title_brush.ColorStops().Append(session->system_compositor.CreateColorGradientStop(1, {0, 0, 0, 0}));
        auto title_scrim = session->system_compositor.CreateSpriteVisual();
        title_scrim.RelativeSizeAdjustment({1, 1});
        title_scrim.Brush(title_brush);
        session->title_backdrop.Children().InsertAtBottom(title_scrim);
        session->title_band = session->system_compositor.CreateSpriteVisual();
        session->title_band.RelativeSizeAdjustment({1, 0});
        session->title_band.Brush(session->system_compositor.CreateColorBrush({102, 0, 0, 0}));
        session->title_backdrop.Children().InsertAtTop(session->title_band);
        root.Children().InsertAtTop(session->title_backdrop);
        session->bottom_shown = session->title_shown = false;
        session->system_target.Root(root);
        return S_OK;
    } catch (...) { return winrt::to_hresult(); }
}
int32_t __cdecl player_bridge_set_video_visible(player_session* session, int32_t visible) noexcept {
    if (!session || !session->system_target) return E_INVALIDARG;
    try {
        session->system_target.Root().IsVisible(visible != 0);
        if (!visible) {
            session->bottom_backdrop.StopAnimation(L"Opacity");
            session->title_backdrop.StopAnimation(L"Opacity");
            session->bottom_backdrop.Opacity(0);
            session->title_backdrop.Opacity(0);
            session->bottom_shown = session->title_shown = false;
        }
        return S_OK;
    } catch (...) { return winrt::to_hresult(); }
}
int32_t __cdecl player_bridge_set_chrome_backdrops(player_session* session, double bottom_height, double title_height, uint32_t flags) noexcept {
    if (!session || !session->bottom_backdrop || !std::isfinite(bottom_height) || !std::isfinite(title_height) ||
        bottom_height < 0 || bottom_height > 16384 || title_height < 0 || title_height > 4096 || (flags & ~7u)) return E_INVALIDARG;
    try {
        session->bottom_backdrop.Size({0, static_cast<float>(bottom_height)});
        session->title_backdrop.Size({0, static_cast<float>(title_height * 2.75)});
        session->title_band.Size({0, static_cast<float>(title_height)});
        auto fade = [&](auto visual, bool shown, bool& previous) {
            if (shown == previous && (flags & 4u)) return;
            previous = shown;
            if (!(flags & 4u)) {
                visual.StopAnimation(L"Opacity"); visual.Opacity(shown ? 1.f : 0.f);
                return;
            }
            auto animation = session->system_compositor.CreateScalarKeyFrameAnimation();
            animation.Duration(winrt::Windows::Foundation::TimeSpan{2500000});
            animation.InsertExpressionKeyFrame(0, L"this.StartingValue");
            auto easing = session->system_compositor.CreateCubicBezierEasingFunction({0.2f, 0}, {0, 1});
            animation.InsertKeyFrame(1, shown ? 1.f : 0.f, easing);
            visual.StartAnimation(L"Opacity", animation);
        };
        fade(session->bottom_backdrop, (flags & 1u) != 0, session->bottom_shown);
        fade(session->title_backdrop, (flags & 2u) != 0, session->title_shown);
        return S_OK;
    } catch (...) { return winrt::to_hresult(); }
}
int32_t __cdecl player_bridge_resize(player_session* session, uint32_t width, uint32_t height, double x, double y) noexcept {
    if (!session || !std::isfinite(x) || !std::isfinite(y) || x <= 0 || y <= 0) return -1;
    { std::lock_guard lock(session->size_mutex);
      session->width = std::clamp(width, 1u, 16384u); session->height = std::clamp(height, 1u, 16384u);
      session->scale_x = x; session->scale_y = y; session->resize_pending = true; }
    SetEvent(session->render_event); return 0;
}
int32_t __cdecl player_bridge_open(player_session* session, const wchar_t* path) noexcept {
    if (!session || !path || !*path) return -1;
    try {
        const auto file = utf8(path);
        std::lock_guard playback_lock(session->playback_mutex);
        const auto id = session->request_id.fetch_add(1);
        session->open_request = id;
        { std::lock_guard lock(session->state_mutex);
          session->state.state = PLAYER_LOADING; session->state.video_ready = 0; session->state.error[0] = 0;
          session->state.preparing = 1;
          session->explicit_open_pending = true; session->ended_at_eof = false;
          session->media_path = path; session->state.reserved2 = 0;
          session->layout_ready = session->resume_pending = false;
          ++session->state.media_generation; session->tracks.clear(); ++session->state.tracks_revision;
          session->chapters.clear(); ++session->chapters_revision;
          session->state.chapter_count = 0; session->state.chapter = -1; }
        SetEvent(session->state_event);
        // File-local pause keeps the playback clock stopped while this file's
        // decoder, first-frame shaders and fitted render target are prepared.
        const char* args[]{"loadfile", file.c_str(), "replace", "-1", "pause=yes", nullptr};
        int result = session->command(args, id);
        if (result < 0) session->fail(session->api.error_string(result));
        return result;
    } catch (const std::exception& ex) { session->fail(ex.what()); return -1; }
    catch (...) { return -1; }
}
int32_t __cdecl player_bridge_prepare_playback(player_session* session, uint32_t generation) noexcept {
    if (!session) return -1;
    {
        std::lock_guard lock(session->state_mutex);
        if (!session->state.preparing || session->state.media_generation != generation) return 0;
        session->layout_ready = true;
    }
    SetEvent(session->render_event);
    return 0;
}
int32_t __cdecl player_bridge_set_paused(player_session* session, int32_t paused) noexcept {
    if (!session) return -1;
    std::lock_guard playback_lock(session->playback_mutex);
    {
        std::lock_guard lock(session->state_mutex);
        session->state.preparing = 0;
        session->resume_pending = false;
    }
    const char* args[]{"set", "pause", paused ? "yes" : "no", nullptr};
    return session->command(args);
}
int32_t __cdecl player_bridge_seek(player_session* session, double seconds, int32_t exact) noexcept {
    if (!session || !std::isfinite(seconds)) return -1;
    char value[64]{};
    sprintf_s(value, "%.6f", std::max(0.0, seconds));
    const char* args[]{"seek", value, exact ? "absolute+exact" : "absolute+keyframes", nullptr};
    return session->command(args);
}
int32_t __cdecl player_bridge_set_volume(player_session* session, double volume) noexcept {
    if (!session || !std::isfinite(volume)) return -1;
    char value[64]{};
    sprintf_s(value, "%.3f", std::clamp(volume, 0.0, 100.0));
    const char* args[]{"set", "volume", value, nullptr};
    return session->command(args);
}
int32_t __cdecl player_bridge_get_chapters(player_session* session, player_chapter* output, uint32_t capacity,
    uint32_t stride, uint32_t* revision, uint32_t* generation) noexcept {
    if (!session || !revision || !generation || stride != sizeof(player_chapter) || (capacity && !output)) return -1;
    std::lock_guard lock(session->state_mutex);
    *revision = session->chapters_revision;
    *generation = session->state.media_generation;
    const size_t count = std::min(static_cast<size_t>(capacity), session->chapters.size());
    if (count) std::copy_n(session->chapters.data(), count, output);
    return static_cast<int32_t>(session->chapters.size());
}
int32_t __cdecl player_bridge_get_tracks(player_session* session, player_track* output, uint32_t capacity,
    uint32_t stride, uint32_t* revision, uint32_t* generation) noexcept {
    if (!session || !revision || !generation || stride != sizeof(player_track) || (capacity && !output)) return -1;
    std::lock_guard lock(session->state_mutex);
    *revision = session->state.tracks_revision;
    *generation = session->state.media_generation;
    const size_t count = std::min(static_cast<size_t>(capacity), session->tracks.size());
    if (count) std::copy_n(session->tracks.data(), count, output);
    return static_cast<int32_t>(session->tracks.size());
}
int32_t __cdecl player_bridge_select_track(player_session* session, int32_t type, int64_t id, uint32_t generation) noexcept {
    if (!session || (type != 1 && type != 2) || id < 0) return -1;
    {
        std::lock_guard lock(session->state_mutex);
        if (session->state.media_generation != generation ||
            (session->state.state != PLAYER_PLAYING && session->state.state != PLAYER_PAUSED && session->state.state != PLAYER_ENDED)) return -1;
        if (!(type == 2 && id == 0) && std::none_of(session->tracks.begin(), session->tracks.end(),
            [type, id](const player_track& t) { return t.type == type && t.id == id; })) return -1;
    }
    char value[32]{};
    sprintf_s(value, "%lld", static_cast<long long>(id));
    const char* args[]{"set", type == 1 ? "aid" : "sid", id == 0 ? "no" : value, nullptr};
    return session->command(args);
}
int32_t __cdecl player_bridge_change_chapter(player_session* session, int32_t direction, uint32_t generation) noexcept {
    if (!session || (direction != -1 && direction != 1)) return -1;
    int32_t target;
    {
        std::lock_guard lock(session->state_mutex);
        if (session->state.media_generation != generation || session->state.chapter_count <= 0 ||
            (session->state.state != PLAYER_PLAYING && session->state.state != PLAYER_PAUSED && session->state.state != PLAYER_ENDED)) return -1;
        const int64_t requested = static_cast<int64_t>(session->state.chapter) + direction;
        if (requested < 0 || requested >= session->state.chapter_count) return -1;
        target = static_cast<int32_t>(requested);
    }
    char value[32]{};
    sprintf_s(value, "%d", target);
    // mpv applies chapter-seek-threshold only to a backward add/cycle, not set.
    const char* args[]{direction < 0 ? "add" : "set", "chapter", direction < 0 ? "-1" : value, nullptr};
    return session->command(args);
}
int32_t __cdecl player_bridge_set_loop(player_session* session, int32_t enabled) noexcept {
    if (!session) return -1;
    const char* args[]{"set", "loop-file", enabled ? "inf" : "no", nullptr};
    return session->command(args);
}
int32_t __cdecl player_bridge_set_subtitle_clearance(player_session* session, double fraction, double baseline) noexcept {
    if (!session || !std::isfinite(fraction) || fraction < 0 || fraction > 0.8 || !std::isfinite(baseline) || baseline < 0 || baseline > 100) return -1;
    char position[32]{};
    sprintf_s(position, "%.4f", std::min(baseline, 100.0 * (1.0 - fraction)));
    const char* args[]{"set", "sub-pos", position, nullptr};
    return session->command(args);
}

int32_t __cdecl player_bridge_set_setting(player_session* session, int32_t option, const wchar_t* value, uint64_t request) noexcept {
    if (!session || !session->core || !value || !(request & (1ull << 63)) || wcsnlen_s(value, 128) >= 128) return MPV_ERROR_INVALID_PARAMETER;
    static const char* names[]{"", "speed", "audio-pitch-correction", "sub-font", "sub-font-size", "sub-outline-size",
        "sub-bold", "sub-italic", "sub-color", "sub-outline-color", "sub-pos", "sub-ass-override", "chapter-seek-threshold",
        "keep-open", "keep-open-pause", "loop-file", "save-position-on-quit", "watch-later-options", "hr-seek",
        "track-auto-selection", "loop-playlist", "autocreate-playlist", "volume", "alang", "audio-file-auto", "audio-device", "audio-exts", "mute",
        "hwdec", "video-exts", "dither-depth", "deband", "hdr-compute-peak", "d3d11va-zero-copy", "background-color",
        "scale", "cscale", "dscale", "correct-downscaling", "sigmoid-upscaling", "sub-shadow-offset", "sub-back-color"};
    // Zero-copy (33) is only accepted by create: its interop opts are a snapshot.
    if (option < 1 || option > 41 || option == 33) return MPV_ERROR_INVALID_PARAMETER;
    try {
        auto encoded = utf8(value);
        if (encoded.empty() && option != 23 && option != 36 && option != 37) return MPV_ERROR_INVALID_PARAMETER;
        if (option == 28) {
            const std::string allowed = ",auto,no,auto-copy,d3d11va,d3d11va-copy,nvdec,nvdec-copy,dxva2-copy,";
            if (allowed.find("," + encoded + ",") == std::string::npos) return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 30) {
            if (encoded != "auto" && encoded != "no" && encoded != "8" && encoded != "10" && encoded != "12" && encoded != "16") return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 32) {
            if (encoded != "auto" && encoded != "yes" && encoded != "no") return MPV_ERROR_INVALID_PARAMETER;
        } else if (option >= 35 && option <= 37) {
            const std::string allowed = ",bilinear,bicubic_fast,oversample,nearest,hermite,bicubic,gaussian,spline16,spline36,spline64,lanczos,ewa_lanczos,ewa_lanczossharp,";
            if (!(encoded.empty() && option != 35) && allowed.find("," + encoded + ",") == std::string::npos) return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 2 || option == 6 || option == 7 || option == 14 || option == 16 || option == 19 || option == 27 || option == 31 || option == 38 || option == 39) {
            if (encoded != "yes" && encoded != "no") return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 23 || option == 26 || option == 29) {
            const auto valid = option == 23 ? "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789,-" : "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789,";
            if (encoded.find_first_not_of(valid) != std::string::npos) return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 24) {
            if (encoded != "no" && encoded != "exact" && encoded != "fuzzy" && encoded != "all") return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 25) {
            if (encoded.find_first_of("\r\n\t") != std::string::npos) return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 13) {
            if (encoded != "yes" && encoded != "no" && encoded != "always") return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 15 || option == 20) {
            if (encoded != "no" && encoded != "inf" && !(option == 20 && encoded == "force")) {
                if (encoded.find_first_not_of("0123456789") != std::string::npos) return MPV_ERROR_INVALID_PARAMETER;
                const auto count = std::strtoll(encoded.c_str(), nullptr, 10);
                if (count < 1 || count > 1000000) return MPV_ERROR_INVALID_PARAMETER;
            }
        } else if (option == 17) {
            const std::string allowed = ",start,speed,volume,aid,sid,sub-delay,audio-delay,sub-pos,sub-visibility,";
            size_t start = 0;
            do {
                auto end = encoded.find(',', start);
                const auto token = encoded.substr(start, end == std::string::npos ? end : end - start);
                if (token.empty() || allowed.find("," + token + ",") == std::string::npos) return MPV_ERROR_INVALID_PARAMETER;
                if (end == std::string::npos) break;
                start = end + 1;
            } while (start <= encoded.size());
        } else if (option == 18) {
            if (encoded != "no" && encoded != "absolute" && encoded != "yes" && encoded != "default") return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 21) {
            if (encoded != "no" && encoded != "filter" && encoded != "same") return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 11) {
            if (encoded != "scale" && encoded != "force") return MPV_ERROR_INVALID_PARAMETER;
        } else if (option == 8 || option == 9 || option == 34 || option == 41) {
            if (encoded.size() != (option == 41 ? 9u : 7u) || encoded[0] != '#' || encoded.find_first_not_of("0123456789abcdefABCDEF", 1) != std::string::npos)
                return MPV_ERROR_INVALID_PARAMETER;
        } else if (option != 3) {
            char* end{};
            double number = std::strtod(encoded.c_str(), &end);
            double low = 0, high = 100;
            if (option == 1) { low = .25; high = 4; }
            else if (option == 4) { low = 1; high = 200; }
            else if (option == 5 || option == 40) high = 10;
            else if (option == 12) { low = -1; high = 60; }
            if (!end || *end || !std::isfinite(number) || number < low || number > high) return MPV_ERROR_INVALID_PARAMETER;
        }
        {
            std::lock_guard lock(session->state_mutex);
            if (session->pending_settings.size() + session->setting_results.size() >= 64 || session->pending_settings.contains(request)) return MPV_ERROR_INVALID_PARAMETER;
            session->pending_settings.emplace(request, option);
        }
        const char* data = encoded.c_str();
        int result = session->api.set_property_async(session->core, request, names[option], MPV_FORMAT_STRING, &data);
        if (result < 0) {
            std::lock_guard lock(session->state_mutex);
            session->pending_settings.erase(request);
        }
        return result;
    } catch (...) { return MPV_ERROR_INVALID_PARAMETER; }
}

int32_t __cdecl player_bridge_take_setting_result(player_session* session, player_setting_result* result, uint32_t size) noexcept {
    if (!session || !result || size != sizeof(player_setting_result)) return MPV_ERROR_INVALID_PARAMETER;
    std::lock_guard lock(session->state_mutex);
    if (session->setting_results.empty()) return 0;
    *result = session->setting_results.front();
    session->setting_results.pop_front();
    return 1;
}
