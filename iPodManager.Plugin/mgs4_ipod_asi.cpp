#define WIN32_LEAN_AND_MEAN
#include "background_playback.h"
#include "checked_patch_batch.h"
#include "catalog_expansion_references.h"
#include "deployment_manifest.h"
#include "programmer_resume.h"
#include "runtime_path.h"
#include <algorithm>
#include <atomic>
#include <array>
#include <bcrypt.h>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwctype>
#include <exception>
#include <limits>
#include <memory>
#include <thread>
#include <unordered_map>
#include <vector>
#include <windows.h>

namespace
{
wchar_t g_log_path[MAX_PATH] = {};
wchar_t g_game_dir[MAX_PATH] = {};
BYTE *g_executable_start = nullptr;
volatile LONG *g_selected_ipod_index = nullptr;
volatile LONG g_last_resolved_ipod_position = -1;
constexpr DWORD kStockTrackCount = 73;
constexpr DWORD kNativeCatalogCapacity = 1024;
constexpr SIZE_T kCatalogEntrySize = 32;
constexpr SIZE_T kRuntimeRecordSize = 0x238;
constexpr SIZE_T kUiRecordSize = 0x110;
constexpr DWORD kOriginalCatalogCapacity = 128;
BYTE *g_expanded_runtime_records = nullptr;
int32_t *g_expanded_playback_order = nullptr;
int32_t *g_expanded_ui_orders = nullptr;
BYTE *g_expanded_ui_records = nullptr;
BYTE *g_expanded_sorting_workspace = nullptr;
BYTE *g_expansion_allocation = nullptr;
BYTE *g_expansion_thunks = nullptr;
BYTE *g_background_playback_thunks = nullptr;
volatile LONG g_background_playback_active = 0;
bool g_background_playback_enabled = false;
HANDLE g_catalog_ready_event = nullptr;
enum class ActivationPhase
{
    PreActivation,
    ConstructionBlocked,
    PatchedButConstructionBlocked,
    ConstructionStarted,
    ConstructionComplete,
    RuntimeActive,
    FailedBeforeCommit,
    FailedAfterCommit,
    PatchStateUncertain
};
std::atomic<ActivationPhase> g_activation_phase{ActivationPhase::PreActivation};
volatile LONG g_catalog_gate_waiters = 0;
void Log(const char *message);
ipod::patching::Patch g_activation_patches[320]{};
SIZE_T g_activation_patch_count = 0;
void DefinePatch(ipod::patching::Patch &patch, BYTE *address, const void *expected, const void *replacement,
                 SIZE_T length);

struct CustomTrack
{
    std::string runtime_id;
    bool podcast{};
    DWORD catalog_index{};
    uint32_t control_id{}, descriptor_id{};
    std::string dbm_request_path, descriptor_event_path, fmod_event_path;
    std::string source_path;
    uint64_t duration_seconds{};
    std::wstring dbm_path, bank_path;
};
std::vector<std::unique_ptr<CustomTrack>> g_custom_runtime;
std::array<BYTE, ipod::deployment::kStockCount> g_stock_disabled{};
struct SharedProgrammerBank
{
    volatile LONG registration{}, publication{};
    void *native_resource{};
    uint32_t native_handle{};
};
SharedProgrammerBank g_programmer_bank;
thread_local SharedProgrammerBank *g_registration_programmer = nullptr;
const GUID kProgrammerEventGuid = {0x67a17324, 0x5038, 0x4de0, {0x9d, 0x66, 0xb9, 0xd2, 0x76, 0xab, 0x8a, 0xa5}};
void *g_programmer_trigger_owner = nullptr;
CustomTrack *g_programmer_trigger_track = nullptr;
volatile LONG g_programmer_stream_created = 0;
bool InstallProgrammerCreateHook(void *owner);
void TryRegisterProgrammerBank();
CustomTrack *FindCustomByCatalogIndex(LONG i)
{
    for (auto &x : g_custom_runtime)
        if (x->catalog_index == static_cast<DWORD>(i))
            return x.get();
    return nullptr;
}
CustomTrack *FindCustomByDescriptorIds(uint32_t d, uint32_t c)
{
    for (auto &x : g_custom_runtime)
        if (x->descriptor_id == d && x->control_id == c)
            return x.get();
    return nullptr;
}
CustomTrack *FindCustomByDbmPath(const char *p)
{
    if (!p)
        return nullptr;
    for (auto &x : g_custom_runtime)
        if (_stricmp(x->dbm_request_path.c_str(), p) == 0)
            return x.get();
    return nullptr;
}
CustomTrack *FindCustomByEventPath(const char *p)
{
    if (!p)
        return nullptr;
    for (auto &x : g_custom_runtime)
        if (_stricmp(x->fmod_event_path.c_str(), p) == 0)
            return x.get();
    return nullptr;
}
constexpr uint32_t kCallingToNightControlId = 1509, kCallingToNightDescriptorId = 1809;
using NativeDescriptorResolveFn = bool (*)(void *, const void *, void *);
using NativeStringAssignFn = void *(*)(void *, const char *, size_t);
using NativeGuidResolveFn = int (*)(void *, const char *, GUID *);
NativeDescriptorResolveFn g_real_native_descriptor_resolve = nullptr;
NativeGuidResolveFn g_real_native_guid_resolve = nullptr;
void *g_captured_studio_system = nullptr;
extern void *g_audio_manager;
bool HookCustomDescriptor(void *context, const void *input, void *output)
{
    uint32_t key[5]{};
    __try
    {
        if (input)
            memcpy(key, input, sizeof(key));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return g_real_native_descriptor_resolve(context, input, output);
    }
    auto *t = FindCustomByDescriptorIds(key[0], key[1]);
    if (!t)
        return g_real_native_descriptor_resolve(context, input, output);
    if (InterlockedCompareExchange(&g_programmer_bank.publication, 0, 0) != 2)
    {
        TryRegisterProgrammerBank();
        return false;
    }
    uint32_t donor[5]{};
    memcpy(donor, key, sizeof(donor));
    donor[0] = kCallingToNightDescriptorId;
    donor[1] = kCallingToNightControlId;
    if (!g_real_native_descriptor_resolve(context, donor, output) || !output)
        return false;
    __try
    {
        auto *b = static_cast<BYTE *>(output);
        *reinterpret_cast<uint32_t *>(b) = t->descriptor_id;
        *reinterpret_cast<uint32_t *>(b + 4) = t->control_id;
        reinterpret_cast<NativeStringAssignFn>(g_executable_start + 0x3ede0)(b + 0x18, t->descriptor_event_path.c_str(),
                                                                             t->descriptor_event_path.size());
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
    char m[512]{};
    std::snprintf(m, sizeof(m), "GENERIC descriptor catalog=%lu ids=%u/%u path=%s", t->catalog_index, t->descriptor_id,
                  t->control_id, t->descriptor_event_path.c_str());
    Log(m);
    return true;
}
int HookCustomEventHandle(void *sys, const char *path, GUID *out)
{
    if (sys && !InterlockedCompareExchangePointer(&g_captured_studio_system, sys, nullptr))
    {
        char message[128]{};
        std::snprintf(message, sizeof(message), "PROGRAMMER_AUDIO STUDIO system=%p", sys);
        Log(message);
    }
    auto *t = FindCustomByEventPath(path);
    if (!t)
        return g_real_native_guid_resolve(sys, path, out);
    if (!out || !g_programmer_bank.native_handle)
    {
        Log("ERROR GENERIC event path lacks published native handle");
        return 74;
    }
    uint32_t handle = g_programmer_bank.native_handle;
    if (InterlockedCompareExchange(&g_programmer_bank.publication, 0, 0) == 2 && g_programmer_bank.native_handle)
    {
        void *owner = static_cast<BYTE *>(static_cast<void *>(out)) - 0x60;
        InterlockedExchangePointer(&g_programmer_trigger_owner, owner);
        auto *previous_track = static_cast<CustomTrack *>(InterlockedExchangePointer(
            reinterpret_cast<void *volatile *>(&g_programmer_trigger_track), t));
        if (previous_track != t)
            InterlockedExchange(&g_programmer_stream_created, 0);
        if (InstallProgrammerCreateHook(owner))
        {
            handle = g_programmer_bank.native_handle;
        }
        else
        {
            InterlockedExchangePointer(&g_programmer_trigger_owner, nullptr);
            InterlockedExchangePointer(reinterpret_cast<void *volatile *>(&g_programmer_trigger_track), nullptr);
            Log("ERROR direct-stream callback installation failed; native event retained");
        }
    }
    *reinterpret_cast<uint32_t *>(out) = handle;
    return 0;
}

struct ProgrammerSoundProperties
{
    const char *name;
    void *sound;
    int subsoundIndex;
};
static_assert(sizeof(ProgrammerSoundProperties) == 24, "FMOD 2.02 x64 programmer sound ABI changed");

void *g_programmer_sound = nullptr;
void *g_programmer_sound_owner = nullptr;

struct FmodCreateSoundExInfo
{
    int cbsize;
    uint32_t length;
    uint32_t fileoffset;
    int numchannels;
    int defaultfrequency;
    int format;
    uint32_t decodebuffersize;
    int initialsubsound;
    int numsubsounds;
    int *inclusionlist;
    int inclusionlistnum;
    void *pcmreadcallback;
    void *pcmsetposcallback;
    void *nonblockcallback;
    const char *dlsname;
    const char *encryptionkey;
    int maxpolyphony;
    void *userdata;
    int suggestedsoundtype;
    void *fileuseropen;
    void *fileuserclose;
    void *fileuserread;
    void *fileuserseek;
    void *fileuserasyncread;
    void *fileuserasynccancel;
    void *fileuserdata;
    int filebuffersize;
    int channelorder;
    void *initialsoundgroup;
    uint32_t initialseekposition;
    uint32_t initialseekpostype;
    int ignoresetfilesystem;
    uint32_t audioqueuepolicy;
    uint32_t minmidigranularity;
    int nonblockthreadid;
    GUID *fsbguid;
};
static_assert(sizeof(FmodCreateSoundExInfo) == 224, "FMOD 2.02 x64 create-sound ABI changed");
static_assert(offsetof(FmodCreateSoundExInfo, initialseekposition) == 192,
              "FMOD 2.02 x64 initial seek ABI changed");

void HookProgrammerCreate(void *owner, void *parameters)
{
    if (owner == InterlockedCompareExchangePointer(&g_programmer_trigger_owner, nullptr, nullptr))
    {
        char name[128] = "<null>";
        void *sound = nullptr;
        int subsound = 0;
        if (parameters)
        {
            __try
            {
                auto *properties = static_cast<ProgrammerSoundProperties *>(parameters);
                sound = properties->sound;
                subsound = properties->subsoundIndex;
                if (properties->name)
                {
                    const size_t length = strnlen_s(properties->name, sizeof(name) - 1);
                    memcpy(name, properties->name, length);
                    name[length] = '\0';
                }
                if (strcmp(name, "MGS4_STREAM_TEST") == 0)
                {
                    auto *track = static_cast<CustomTrack *>(InterlockedCompareExchangePointer(
                        reinterpret_cast<void *volatile *>(&g_programmer_trigger_track), nullptr, nullptr));
                    const char *source = track ? track->source_path.c_str() : "";
                    using GetCoreSystemFn = int (*)(void *, void **);
                    using CreateStreamFn = int (*)(void *, const char *, uint32_t, void *, void **);
                    void *studio = InterlockedCompareExchangePointer(&g_captured_studio_system, nullptr, nullptr);
                    void *core = nullptr;
                    const int core_result = studio ? reinterpret_cast<GetCoreSystemFn>(
                                                         g_executable_start + 0x2bfe90)(studio, &core)
                                                   : 30;
                    void *created = nullptr;
                    LONG saved_position_ms = 0;
                    __try
                    {
                        saved_position_ms = *reinterpret_cast<volatile LONG *>(g_executable_start + 0x1da2ff0);
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER)
                    {
                        saved_position_ms = 0;
                    }
                    FmodCreateSoundExInfo exinfo{};
                    void *create_info = nullptr;
                    if (track && ipod::programmer::IsValidResumePosition(
                                     InterlockedCompareExchange(&g_programmer_stream_created, 0, 0) != 0,
                                     saved_position_ms, track->duration_seconds))
                    {
                        exinfo.cbsize = sizeof(exinfo);
                        exinfo.initialseekposition = static_cast<uint32_t>(saved_position_ms);
                        exinfo.initialseekpostype = ipod::programmer::kFmodTimeunitMilliseconds;
                        create_info = &exinfo;
                    }
                    const int result = core_result == 0 && core && source[0]
                                           ? reinterpret_cast<CreateStreamFn>(g_executable_start + 0x283990)(
                                                 core, source, 0, create_info, &created)
                                           : core_result;
                    if (result == 0 && created)
                    {
                        if (InterlockedCompareExchangePointer(&g_programmer_sound, nullptr, nullptr))
                        {
                            reinterpret_cast<int (*)(void *)>(g_executable_start + 0x2836c0)(created);
                            Log("ERROR direct stream CREATE arrived before prior DESTROY");
                            return;
                        }
                        InterlockedExchangePointer(&g_programmer_sound, created);
                        InterlockedExchangePointer(&g_programmer_sound_owner, owner);
                        InterlockedExchange(&g_programmer_stream_created, 1);
                        properties->sound = created;
                        properties->subsoundIndex = -1;
                        sound = created;
                        subsound = -1;
                    }
                    else
                    {
                        char message[640]{};
                        std::snprintf(message, sizeof(message),
                                      "ERROR direct stream failed result=%d source=%s", result, source);
                        Log(message);
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                strcpy_s(name, "<invalid>");
                sound = nullptr;
                subsound = 0;
            }
        }
    }
}

void HookProgrammerDestroy(void *owner, void *)
{
    if (owner != InterlockedCompareExchangePointer(&g_programmer_sound_owner, nullptr, nullptr))
        return;
    void *sound = InterlockedExchangePointer(&g_programmer_sound, nullptr);
    InterlockedExchangePointer(&g_programmer_sound_owner, nullptr);
    if (!sound)
        return;
    using ReleaseFn = int (*)(void *);
    const int result = reinterpret_cast<ReleaseFn>(g_executable_start + 0x2836c0)(sound);
    if (result != 0)
    {
        char message[128]{};
        std::snprintf(message, sizeof(message), "ERROR direct stream release failed result=%d", result);
        Log(message);
    }
}

int HookProgrammerDuration(void *description, int *duration)
{
    const int result = reinterpret_cast<int (*)(void *, int *)>(g_executable_start + 0x2c1660)(description, duration);
    auto *owner = static_cast<BYTE *>(InterlockedCompareExchangePointer(&g_programmer_trigger_owner, nullptr, nullptr));
    auto *track = static_cast<CustomTrack *>(InterlockedCompareExchangePointer(
        reinterpret_cast<void *volatile *>(&g_programmer_trigger_track), nullptr, nullptr));
    if (result == 0 && duration && owner && track && *reinterpret_cast<void **>(owner + 0x60) == description)
    {
        *duration = static_cast<int>(track->duration_seconds * 1000);
    }
    return result;
}

bool PrepareProgrammerDurationHook(ipod::patching::Patch &patch)
{
    constexpr DWORD site = 0x176c9c, target = 0x2c1660;
    BYTE *thunks = nullptr;
    const auto base = reinterpret_cast<uintptr_t>(g_executable_start);
    for (uintptr_t offset = 0x20000000; offset <= 0x70000000 && !thunks; offset += 0x10000)
        thunks = static_cast<BYTE *>(
            VirtualAlloc(reinterpret_cast<void *>(base + offset), 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!thunks)
        return false;
    {
        BYTE *thunk = thunks;
        thunk[0] = 0x48;
        thunk[1] = 0xb8;
        const auto hook = reinterpret_cast<uintptr_t>(HookProgrammerDuration);
        memcpy(thunk + 2, &hook, 8);
        thunk[10] = 0xff;
        thunk[11] = 0xe0;
        BYTE expected[5] = {0xe8}, replacement[5] = {0xe8};
        const intptr_t old_delta = (g_executable_start + target) - (g_executable_start + site + 5);
        const intptr_t new_delta = thunk - (g_executable_start + site + 5);
        if (new_delta < INT32_MIN || new_delta > INT32_MAX)
        {
            VirtualFree(thunks, 0, MEM_RELEASE);
            return false;
        }
        const int32_t old_relative = static_cast<int32_t>(old_delta);
        const int32_t new_relative = static_cast<int32_t>(new_delta);
        memcpy(expected + 1, &old_relative, 4);
        memcpy(replacement + 1, &new_relative, 4);
        DefinePatch(patch, g_executable_start + site, expected, replacement, 5);
    }
    DWORD old = 0;
    if (!VirtualProtect(thunks, 0x1000, PAGE_EXECUTE_READ, &old))
    {
        VirtualFree(thunks, 0, MEM_RELEASE);
        return false;
    }
    return true;
}

bool InstallProgrammerCreateHook(void *owner)
{
    if (!owner)
        return false;
    void **create_slot = nullptr;
    void **destroy_slot = nullptr;
    void *expected_create = g_executable_start + 0x176cc0;
    void *expected_destroy = g_executable_start + 0x176e30;
    __try
    {
        auto **vtable = *reinterpret_cast<void ***>(owner);
        create_slot = vtable + 0x78 / sizeof(void *);
        destroy_slot = vtable + 0x80 / sizeof(void *);
        if ((*create_slot != expected_create && *create_slot != reinterpret_cast<void *>(HookProgrammerCreate)) ||
            (*destroy_slot != expected_destroy && *destroy_slot != reinterpret_cast<void *>(HookProgrammerDestroy)))
            return false;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
    if (*create_slot == reinterpret_cast<void *>(HookProgrammerCreate) &&
        *destroy_slot == reinterpret_cast<void *>(HookProgrammerDestroy))
        return true;
    DWORD old = 0;
    if (!VirtualProtect(create_slot, 2 * sizeof(void *), PAGE_READWRITE, &old))
        return false;
    InterlockedExchangePointer(create_slot, reinterpret_cast<void *>(HookProgrammerCreate));
    InterlockedExchangePointer(destroy_slot, reinterpret_cast<void *>(HookProgrammerDestroy));
    DWORD ignored = 0;
    const BOOL restored = VirtualProtect(create_slot, 2 * sizeof(void *), old, &ignored);
    return restored != FALSE;
}
bool PrepareCustomIdentityHooks(ipod::patching::Patch &a, ipod::patching::Patch &b)
{
    BYTE *d = g_executable_start + 0x172e40, *c = g_executable_start + 0x176c36;
    constexpr BYTE ed[] = {0x48, 0x89, 0x5c, 0x24, 0x08, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41,
                           0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8d, 0x6c, 0x24, 0xd9},
                   ec[] = {0xe8, 0x05, 0x9b, 0x14, 0x00};
    if (memcmp(d, ed, sizeof(ed)) || memcmp(c, ec, sizeof(ec)))
        return false;
    BYTE *code = nullptr;
    auto base = reinterpret_cast<uintptr_t>(g_executable_start);
    for (uintptr_t x = 0x20000000; x <= 0x70000000 && !code; x += 0x10000)
        code = static_cast<BYTE *>(
            VirtualAlloc(reinterpret_cast<void *>(base + x), 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!code)
        return false;
    memcpy(code, ed, sizeof(ed));
    code[sizeof(ed)] = 0x48;
    code[sizeof(ed) + 1] = 0xb8;
    auto cont = reinterpret_cast<uintptr_t>(d + sizeof(ed));
    memcpy(code + sizeof(ed) + 2, &cont, 8);
    code[sizeof(ed) + 10] = 0xff;
    code[sizeof(ed) + 11] = 0xe0;
    BYTE *r = code + 64;
    r[0] = 0x48;
    r[1] = 0xb8;
    auto gh = reinterpret_cast<uintptr_t>(HookCustomEventHandle);
    memcpy(r + 2, &gh, 8);
    r[10] = 0xff;
    r[11] = 0xe0;
    DWORD old = 0;
    if (!VirtualProtect(code, 0x1000, PAGE_EXECUTE_READ, &old))
        return false;
    BYTE dr[sizeof(ed)] = {0x48, 0xb8};
    auto dh = reinterpret_cast<uintptr_t>(HookCustomDescriptor);
    memcpy(dr + 2, &dh, 8);
    dr[10] = 0xff;
    dr[11] = 0xe0;
    memset(dr + 12, 0x90, sizeof(dr) - 12);
    DefinePatch(a, d, ed, dr, sizeof(ed));
    BYTE cr[5] = {0xe8};
    auto delta = r - (c + 5);
    int32_t rel = static_cast<int32_t>(delta);
    memcpy(cr + 1, &rel, 4);
    DefinePatch(b, c, ec, cr, 5);
    g_real_native_descriptor_resolve = reinterpret_cast<NativeDescriptorResolveFn>(code);
    g_real_native_guid_resolve = reinterpret_cast<NativeGuidResolveFn>(g_executable_start + 0x2c0740);
    return true;
}

using ProviderFileInfoFn = bool (*)(const char *, uint64_t *);
using ProviderReadFn = int32_t (*)(const char *, void *, uint64_t, uint64_t, uint64_t);
using ProviderBackingFn = void *(*)(void *);
ProviderFileInfoFn g_real_provider_file_info = nullptr;
ProviderReadFn g_real_provider_read = nullptr;
ProviderBackingFn g_real_provider_backing = nullptr;

bool IsCustomLooseDbmPath(const char *path)
{
    return FindCustomByDbmPath(path) != nullptr;
}
bool GetCustomLooseDbmPath(const char *requested, wchar_t (&path)[MAX_PATH])
{
    auto *t = FindCustomByDbmPath(requested);
    if (!t || t->dbm_path.size() >= MAX_PATH)
        return false;
    return wcscpy_s(path, t->dbm_path.c_str()) == 0;
}
void *HookProviderBacking(void *pathname)
{
    const char *path = static_cast<const char *>(pathname);
    bool custom = false;
    __try
    {
        custom = IsCustomLooseDbmPath(path);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        custom = false;
    }
    return custom ? reinterpret_cast<void *>(1) : g_real_provider_backing(pathname);
}

bool HookProviderFileInfo(const char *path, uint64_t *size)
{
    if (!IsCustomLooseDbmPath(path))
        return g_real_provider_file_info(path, size);
    wchar_t loose[MAX_PATH]{};
    LARGE_INTEGER length{};
    bool success = false;
    if (size && GetCustomLooseDbmPath(path, loose))
    {
        HANDLE file =
            CreateFileW(loose, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE)
        {
            success = GetFileSizeEx(file, &length) && length.QuadPart >= 0;
            CloseHandle(file);
        }
    }
    if (success)
        *size = static_cast<uint64_t>(length.QuadPart);
    else
        Log("ERROR custom DBM FileInfo failed");
    return success;
}

int32_t HookProviderRead(const char *path, void *destination, uint64_t capacity, uint64_t offset, uint64_t length)
{
    if (!IsCustomLooseDbmPath(path))
        return g_real_provider_read(path, destination, capacity, offset, length);
    wchar_t loose[MAX_PATH]{};
    LARGE_INTEGER size{}, position{};
    DWORD read = 0;
    int32_t result = -1;
    if (destination && length <= capacity && length <= MAXDWORD && GetCustomLooseDbmPath(path, loose))
    {
        HANDLE file =
            CreateFileW(loose, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE)
        {
            if (GetFileSizeEx(file, &size) && size.QuadPart >= 0 && offset <= static_cast<uint64_t>(size.QuadPart) &&
                length <= static_cast<uint64_t>(size.QuadPart) - offset)
            {
                position.QuadPart = static_cast<LONGLONG>(offset);
                if (SetFilePointerEx(file, position, nullptr, FILE_BEGIN) &&
                    ReadFile(file, destination, static_cast<DWORD>(length), &read, nullptr) && read == length)
                    result = 0;
            }
            CloseHandle(file);
        }
    }
    if (result != 0)
        Log("ERROR custom DBM read failed");
    return result;
}

bool PrepareProviderHooks(ipod::patching::Patch *patches)
{
    // Startup/catalog ingestion plus the proven persistent iPod streamer path.
    // The latter performs two metadata attempts and one data read in 0x140124850.
    constexpr DWORD info_sites[] = {0x5a985, 0x5a9cb, 0x5ac35, 0x5ac7b, 0x1249f3, 0x124a2f};
    constexpr DWORD read_sites[] = {0x5aa08, 0x5acb8, 0x124b43};
    // These backing calls must join FileInfo and Read in the checked startup batch.
    constexpr DWORD backing_sites[] = {0x124ae8, 0x124b24};
    BYTE *code = nullptr;
    const uintptr_t base = reinterpret_cast<uintptr_t>(g_executable_start);
    for (uintptr_t delta = 0x20000000; delta <= 0x70000000 && !code; delta += 0x10000)
        code = static_cast<BYTE *>(
            VirtualAlloc(reinterpret_cast<void *>(base + delta), 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!code)
        return false;
    auto relay = [](BYTE *output, uintptr_t hook) {
        output[0] = 0x48;
        output[1] = 0xb8;
        memcpy(output + 2, &hook, 8);
        output[10] = 0xff;
        output[11] = 0xe0;
    };
    relay(code, reinterpret_cast<uintptr_t>(HookProviderFileInfo));
    relay(code + 32, reinterpret_cast<uintptr_t>(HookProviderRead));
    relay(code + 64, reinterpret_cast<uintptr_t>(HookProviderBacking));
    DWORD old = 0;
    if (!VirtualProtect(code, 0x1000, PAGE_EXECUTE_READ, &old))
    {
        VirtualFree(code, 0, MEM_RELEASE);
        return false;
    }
    SIZE_T index = 0;
    auto define_call = [&](DWORD rva, BYTE *target, uintptr_t original) {
        BYTE *site = g_executable_start + rva;
        BYTE expected[5] = {0xe8};
        const intptr_t old_delta = static_cast<intptr_t>(original) - reinterpret_cast<intptr_t>(site + 5);
        const int32_t old_rel = static_cast<int32_t>(old_delta);
        memcpy(expected + 1, &old_rel, 4);
        if (memcmp(site, expected, 5))
            return false;
        BYTE replacement[5] = {0xe8};
        const intptr_t new_delta = target - (site + 5);
        if (new_delta < INT32_MIN || new_delta > INT32_MAX)
            return false;
        const int32_t new_rel = static_cast<int32_t>(new_delta);
        memcpy(replacement + 1, &new_rel, 4);
        DefinePatch(patches[index++], site, expected, replacement, 5);
        return true;
    };
    for (DWORD site : info_sites)
        if (!define_call(site, code, reinterpret_cast<uintptr_t>(g_executable_start + 0x159d30)))
        {
            VirtualFree(code, 0, MEM_RELEASE);
            return false;
        }
    for (DWORD site : read_sites)
        if (!define_call(site, code + 32, reinterpret_cast<uintptr_t>(g_executable_start + 0x15a5c0)))
        {
            VirtualFree(code, 0, MEM_RELEASE);
            return false;
        }
    for (DWORD site : backing_sites)
        if (!define_call(site, code + 64, reinterpret_cast<uintptr_t>(g_executable_start + 0x159db0)))
        {
            VirtualFree(code, 0, MEM_RELEASE);
            return false;
        }
    g_real_provider_file_info = reinterpret_cast<ProviderFileInfoFn>(g_executable_start + 0x159d30);
    g_real_provider_read = reinterpret_cast<ProviderReadFn>(g_executable_start + 0x15a5c0);
    g_real_provider_backing = reinterpret_cast<ProviderBackingFn>(g_executable_start + 0x159db0);
    Log("IPOD_PROVIDER callsite-only pathname observation prepared");
    return true;
}

using IdentityHandoffFn = int (*)(int, char *, uint32_t *);
IdentityHandoffFn g_real_identity_handoff = nullptr;
BYTE *g_identity_handoff_trampoline = nullptr;
bool IsPlaybackEligible(int index)
{
    const int total = static_cast<int>(kStockTrackCount + g_custom_runtime.size());
    if (index < 0 || index >= total)
        return false;
    if (index < static_cast<int>(kStockTrackCount) && g_stock_disabled[index])
        return false;
    using StockPredicateFn = int (*)(int);
    return reinterpret_cast<StockPredicateFn>(g_executable_start + 0x7b8b0)(index) == 0;
}
int ResolvePlaybackTraversalIndex(int candidate, int &position)
{
    if (candidate < 0 || candidate >= static_cast<int>(kStockTrackCount) || !g_stock_disabled[candidate])
        return candidate;

    const int total = static_cast<int>(kStockTrackCount + g_custom_runtime.size());
    auto *order = g_expanded_playback_order;
    auto *current_position = reinterpret_cast<volatile LONG *>(g_executable_start + 0x1da2fec);
    position = static_cast<int>(InterlockedCompareExchange(current_position, -1, -1));
    if (total <= 0 || position < 0 || position >= total)
        return candidate;
    const int previous =
        static_cast<int>(InterlockedCompareExchange(&g_last_resolved_ipod_position, -1, -1));
    int direction = 1;
    if (previous >= 0 && previous < total)
    {
        const int forward = (position - previous + total) % total;
        const int backward = (previous - position + total) % total;
        direction = forward <= backward ? 1 : -1;
    }

    int scan_position = position;
    for (int visited = 0; visited < total; ++visited)
    {
        const int index = order[scan_position];
        if (IsPlaybackEligible(index))
        {
            position = scan_position;
            return index;
        }
        scan_position += direction;
        if (scan_position < 0)
            scan_position = total - 1;
        else if (scan_position == total)
            scan_position = 0;
    }
    return candidate;
}
void HookSelectedIpodIndex(int candidate)
{
    const int requested = candidate;
    int position = -1;
    candidate = ResolvePlaybackTraversalIndex(candidate, position);
    if (candidate != requested && position >= 0)
        InterlockedExchange(reinterpret_cast<volatile LONG *>(g_executable_start + 0x1da2fec), position);
    InterlockedExchange(g_selected_ipod_index, candidate);
    if (candidate != requested)
    {
        char line[160]{};
        std::snprintf(line, sizeof(line), "stock traversal skipped disabled index=%d replacement=%d", requested,
                      candidate);
        Log(line);
    }
}
int HookIdentityHandoff(int index, char *identity, uint32_t *duration)
{
    const int result = g_real_identity_handoff(index, identity, duration);
    if (result == 0)
    {
        InterlockedExchange(&g_last_resolved_ipod_position,
                            *reinterpret_cast<volatile LONG *>(g_executable_start + 0x1da2fec));
        if (auto *track = FindCustomByCatalogIndex(index))
        {
            TryRegisterProgrammerBank();
        }
    }
    return result;
}
bool PrepareIdentityHandoffHook(ipod::patching::Patch &patch)
{
    constexpr BYTE expected[] = {0x48, 0x63, 0xc1, 0x4c, 0x8d, 0x15, 0xd6, 0x4a, 0xd1,
                                 0x01, 0x4c, 0x69, 0xc8, 0x38, 0x02, 0x00, 0x00};
    BYTE *entry = g_executable_start + 0x7c700;
    if (memcmp(entry, expected, sizeof(expected)))
    {
        Log("ERROR identity handoff entry rejected");
        return false;
    }
    BYTE *t = reinterpret_cast<BYTE *>(VirtualAlloc(nullptr, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!t)
        return false;
    // movsxd rax,ecx; mov r10,<record table>; imul r9,rax,238h
    const BYTE prefix[] = {0x48, 0x63, 0xc1, 0x49, 0xba};
    memcpy(t, prefix, sizeof(prefix));
    const uintptr_t table = reinterpret_cast<uintptr_t>(g_expanded_runtime_records);
    memcpy(t + 5, &table, 8);
    const BYTE multiply[] = {0x4c, 0x69, 0xc8, 0x38, 0x02, 0x00, 0x00};
    memcpy(t + 13, multiply, sizeof(multiply));
    t[20] = 0x48;
    t[21] = 0xb8;
    const uintptr_t continuation = reinterpret_cast<uintptr_t>(entry + sizeof(expected));
    memcpy(t + 22, &continuation, 8);
    t[30] = 0xff;
    t[31] = 0xe0;
    DWORD old = 0;
    if (!VirtualProtect(t, 0x1000, PAGE_EXECUTE_READ, &old))
    {
        VirtualFree(t, 0, MEM_RELEASE);
        return false;
    }
    BYTE replacement[sizeof(expected)] = {0x48, 0xb8};
    const uintptr_t hook = reinterpret_cast<uintptr_t>(HookIdentityHandoff);
    memcpy(replacement + 2, &hook, 8);
    replacement[10] = 0xff;
    replacement[11] = 0xe0;
    memset(replacement + 12, 0x90, sizeof(replacement) - 12);
    DefinePatch(patch, entry, expected, replacement, sizeof(expected));
    g_identity_handoff_trampoline = t;
    g_real_identity_handoff = reinterpret_cast<IdentityHandoffFn>(t);
    return true;
}

using EventStringLookupFn = int (*)(void *, const char *, void *);
EventStringLookupFn g_real_event_string_lookup = nullptr;
BYTE *g_event_string_lookup_trampoline = nullptr;
extern void *g_audio_manager;
int HookEventStringLookup(void *table, const char *path, void *guid)
{
    if (path && guid)
        if (auto *t = FindCustomByEventPath(path))
        {
            if (InterlockedCompareExchange(&g_programmer_bank.publication, 0, 0) != 2)
            {
                TryRegisterProgrammerBank();
                return g_real_event_string_lookup(table, path, guid);
            }
            memcpy(guid, &kProgrammerEventGuid, 16);
            char message[320]{};
            std::snprintf(message, sizeof(message), "custom event string lookup catalog=%lu runtime=%s path=%s",
                          t->catalog_index, t->runtime_id.c_str(), path);
            Log(message);
            return 0;
        }
    return g_real_event_string_lookup(table, path, guid);
}
bool PrepareEventStringLookupHook(ipod::patching::Patch &patch)
{
    constexpr BYTE expected[] = {0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x6c, 0x24, 0x10,
                                 0x48, 0x89, 0x74, 0x24, 0x18, 0x48, 0x89, 0x7c, 0x24, 0x20};
    BYTE *entry = g_executable_start + 0x3535d0;
    if (memcmp(entry, expected, sizeof(expected)))
    {
        Log("ERROR private string lookup entry rejected");
        return false;
    }
    BYTE *t = reinterpret_cast<BYTE *>(VirtualAlloc(nullptr, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!t)
        return false;
    memcpy(t, expected, sizeof(expected));
    t[20] = 0x48;
    t[21] = 0xb8;
    const uintptr_t continuation = reinterpret_cast<uintptr_t>(entry + sizeof(expected));
    memcpy(t + 22, &continuation, 8);
    t[30] = 0xff;
    t[31] = 0xe0;
    DWORD old = 0;
    if (!VirtualProtect(t, 0x1000, PAGE_EXECUTE_READ, &old))
    {
        VirtualFree(t, 0, MEM_RELEASE);
        return false;
    }
    BYTE replacement[sizeof(expected)] = {0x48, 0xb8};
    const uintptr_t hook = reinterpret_cast<uintptr_t>(HookEventStringLookup);
    memcpy(replacement + 2, &hook, 8);
    replacement[10] = 0xff;
    replacement[11] = 0xe0;
    memset(replacement + 12, 0x90, sizeof(replacement) - 12);
    DefinePatch(patch, entry, expected, replacement, sizeof(expected));
    g_event_string_lookup_trampoline = t;
    g_real_event_string_lookup = reinterpret_cast<EventStringLookupFn>(t);
    return true;
}

using BankLoadQueueFn = int (*)(void *, const void *, bool);
BankLoadQueueFn g_real_bank_load_queue = nullptr;
BYTE *g_bank_load_queue_trampoline = nullptr;
void *g_audio_manager = nullptr;
bool PrepareAudioManagerCapture(ipod::patching::Patch &patch)
{
    constexpr BYTE expected[] = {0x48, 0x8b, 0x89, 0xc8, 0x0a, 0x00, 0x00, 0x45,
                                 0x33, 0xc0, 0x48, 0x89, 0x54, 0x24, 0x28};
    BYTE *entry = g_executable_start + 0x332753;
    if (memcmp(entry, expected, sizeof(expected)))
    {
        Log("ERROR private audio manager capture entry rejected");
        return false;
    }
    BYTE *t = reinterpret_cast<BYTE *>(VirtualAlloc(nullptr, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!t)
        return false;
    SIZE_T n = 0;
    t[n++] = 0x48;
    t[n++] = 0xb8;
    const uintptr_t capture = reinterpret_cast<uintptr_t>(&g_audio_manager);
    memcpy(t + n, &capture, 8);
    n += 8;
    t[n++] = 0x48;
    t[n++] = 0x89;
    t[n++] = 0x08;
    memcpy(t + n, expected, sizeof(expected));
    n += sizeof(expected);
    t[n++] = 0x48;
    t[n++] = 0xb8;
    const uintptr_t continuation = reinterpret_cast<uintptr_t>(entry + sizeof(expected));
    memcpy(t + n, &continuation, 8);
    n += 8;
    t[n++] = 0xff;
    t[n++] = 0xe0;
    DWORD old = 0;
    if (!VirtualProtect(t, 0x1000, PAGE_EXECUTE_READ, &old))
    {
        VirtualFree(t, 0, MEM_RELEASE);
        return false;
    }
    BYTE replacement[sizeof(expected)] = {0x48, 0xb8};
    const uintptr_t target = reinterpret_cast<uintptr_t>(t);
    memcpy(replacement + 2, &target, 8);
    replacement[10] = 0xff;
    replacement[11] = 0xe0;
    memset(replacement + 12, 0x90, sizeof(replacement) - 12);
    DefinePatch(patch, entry, expected, replacement, sizeof(expected));
    return true;
}
void TryRegisterProgrammerBank()
{
    if (!g_audio_manager) return;
    if (InterlockedCompareExchange(&g_programmer_bank.registration, 1, 0) != 0)
        return;
    wchar_t wide_path[MAX_PATH]{};
    if (swprintf_s(wide_path, L"%scommon\\bank\\default\\MGS4iPodStreaming.bank", g_game_dir) < 0)
        return;
    char path[MAX_PATH * 3]{};
    if (!WideCharToMultiByte(CP_UTF8, 0, wide_path, -1, path, sizeof(path), nullptr, nullptr))
    {
        Log("PROGRAMMER_PROBE registration failed: bank pathname conversion");
        return;
    }
    const DWORD attributes = GetFileAttributesW(wide_path);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY))
    {
        char message[320]{};
        std::snprintf(message, sizeof(message), "ERROR programmer BANK missing: %s",
                      path);
        Log(message);
        return;
    }
    using CreateFn = int (*)(void *, uint32_t *);
    using LookupFn = int (*)(uint32_t, void **);
    using RegisterFn = int (*)(void *, const void *, uint32_t, void *);
    uint32_t handle = 0;
    int result = reinterpret_cast<CreateFn>(g_executable_start + 0x3c1bb0)(g_audio_manager, &handle);
    void *state = nullptr;
    if (result == 0)
        result = reinterpret_cast<LookupFn>(g_executable_start + 0x340c30)(handle, &state);
    if (result != 0 || !state)
    {
        char message[512]{};
        std::snprintf(message, sizeof(message),
                      "ERROR programmer BANK state creation failed path=%s result=%d resource=%p state=%p",
                      path, result, nullptr, state);
        Log(message);
        return;
    }
    struct Input
    {
        uint32_t mode, reserved;
        const char *path;
        uint64_t zero[7];
    } input{};
    input.path = path;
    g_registration_programmer = &g_programmer_bank;
    result = reinterpret_cast<RegisterFn>(g_executable_start + 0x34c080)(g_audio_manager, &input, 0, state);
    g_registration_programmer = nullptr;
    if (result != 0 || !g_programmer_bank.native_resource)
    {
        Log(result != 0 ? "ERROR programmer BANK registration failed"
                        : "ERROR programmer BANK resource capture failed");
        return;
    }
    InterlockedExchange(&g_programmer_bank.registration, 2);
}
int HookBankLoadQueue(void *loader, const void *descriptor, bool immediate)
{
    if (g_registration_programmer)
    {
        if (descriptor)
        {
            const auto *pair = reinterpret_cast<const uintptr_t *>(descriptor);
            if (pair[0] == 1 && pair[1])
                g_registration_programmer->native_resource = reinterpret_cast<void *>(pair[1]);
        }
        return g_real_bank_load_queue(loader, descriptor, immediate);
    }
    const int native = g_real_bank_load_queue(loader, descriptor, immediate);
    TryRegisterProgrammerBank();
    return native;
}
bool PrepareBankLoadQueueHook(ipod::patching::Patch &patch)
{
    constexpr BYTE expected[] = {0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x74,
                                 0x24, 0x10, 0x57, 0x48, 0x83, 0xec, 0x30};
    BYTE *entry = g_executable_start + 0x3c4680;
    if (memcmp(entry, expected, sizeof(expected)))
    {
        Log("ERROR private load queue entry rejected");
        return false;
    }
    BYTE *t = reinterpret_cast<BYTE *>(VirtualAlloc(nullptr, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!t)
        return false;
    memcpy(t, expected, sizeof(expected));
    t[15] = 0x48;
    t[16] = 0xb8;
    const uintptr_t continuation = reinterpret_cast<uintptr_t>(entry + sizeof(expected));
    memcpy(t + 17, &continuation, 8);
    t[25] = 0xff;
    t[26] = 0xe0;
    DWORD old = 0;
    if (!VirtualProtect(t, 0x1000, PAGE_EXECUTE_READ, &old))
    {
        VirtualFree(t, 0, MEM_RELEASE);
        return false;
    }
    BYTE replacement[sizeof(expected)] = {0x48, 0xb8};
    const uintptr_t hook_entry = reinterpret_cast<uintptr_t>(HookBankLoadQueue);
    memcpy(replacement + 2, &hook_entry, 8);
    replacement[10] = 0xff;
    replacement[11] = 0xe0;
    memset(replacement + 12, 0x90, sizeof(replacement) - 12);
    DefinePatch(patch, entry, expected, replacement, sizeof(expected));
    g_bank_load_queue_trampoline = t;
    g_real_bank_load_queue = reinterpret_cast<BankLoadQueueFn>(t);
    return true;
}

using BankPublicationFn = int (*)(void *, void *);
BankPublicationFn g_real_bank_publication = nullptr;
BYTE *g_bank_publication_trampoline = nullptr;
int HookBankPublication(void *manager, void *completed)
{
    const int result = g_real_bank_publication(manager, completed);
    void *resource = completed ? *reinterpret_cast<void **>(completed) : nullptr;
    const void *expected_resource = g_programmer_bank.native_resource;
    const bool match = resource && resource == expected_resource;
    if (completed)
    {
        if (match &&
            InterlockedCompareExchange(&g_programmer_bank.publication, 1, 0) == 0)
        {
            using LookupFn = int (*)(void *, const void *, void **);
            void *event = nullptr;
            int lookup = result == 0
                             ? reinterpret_cast<LookupFn>(g_executable_start + 0x34bc40)(manager, &kProgrammerEventGuid,
                                                                                         &event)
                             : -1;
            void *state = nullptr;
            uint32_t handle = 0;
            if (lookup == 0 && event)
            {
                __try
                {
                    state = *reinterpret_cast<void **>(reinterpret_cast<BYTE *>(event) + 0xd0);
                    if (state)
                        handle = *reinterpret_cast<uint32_t *>(state);
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                    state = nullptr;
                    handle = 0;
                }
            }
            g_programmer_bank.native_handle = handle;
            if (result != 0 || lookup != 0 || !event || !state || !handle)
                Log(result != 0   ? "ERROR programmer BANK publication failed"
                    : lookup != 0 ? "ERROR programmer event lookup failed"
                    : !event      ? "ERROR programmer event is null"
                    : !state      ? "ERROR programmer event state is null"
                                  : "ERROR programmer native handle is zero");
            else
                InterlockedExchange(&g_programmer_bank.publication, 2);
        }

    }
    TryRegisterProgrammerBank();
    return result;
}
bool PrepareBankPublicationHook(ipod::patching::Patch &patch)
{
    constexpr BYTE expected[] = {0x40, 0x55, 0x53, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41,
                                 0x57, 0x48, 0x8d, 0xac, 0x24, 0x48, 0xf7, 0xff, 0xff};
    BYTE *entry = g_executable_start + 0x319690;
    if (memcmp(entry, expected, sizeof(expected)))
    {
        Log("ERROR private completion publish entry rejected");
        return false;
    }
    BYTE *t = reinterpret_cast<BYTE *>(VirtualAlloc(nullptr, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!t)
        return false;
    memcpy(t, expected, sizeof(expected));
    t[18] = 0x48;
    t[19] = 0xb8;
    const uintptr_t continuation = reinterpret_cast<uintptr_t>(entry + sizeof(expected));
    memcpy(t + 20, &continuation, 8);
    t[28] = 0xff;
    t[29] = 0xe0;
    DWORD old = 0;
    if (!VirtualProtect(t, 0x1000, PAGE_EXECUTE_READ, &old))
    {
        VirtualFree(t, 0, MEM_RELEASE);
        return false;
    }
    BYTE replacement[sizeof(expected)] = {0x48, 0xb8};
    const uintptr_t hook = reinterpret_cast<uintptr_t>(HookBankPublication);
    memcpy(replacement + 2, &hook, 8);
    replacement[10] = 0xff;
    replacement[11] = 0xe0;
    memset(replacement + 12, 0x90, sizeof(replacement) - 12);
    DefinePatch(patch, entry, expected, replacement, sizeof(expected));
    g_bank_publication_trampoline = t;
    g_real_bank_publication = reinterpret_cast<BankPublicationFn>(t);
    return true;
}
void DefinePatch(ipod::patching::Patch &patch, BYTE *address, const void *expected, const void *replacement,
                 SIZE_T length)
{
    patch = {};
    patch.address = address;
    patch.length = length;
    memcpy(patch.expected, expected, length);
    memcpy(patch.replacement, replacement, length);
}

bool LoadBackgroundPlaybackPreference()
{
    wchar_t path[MAX_PATH]{};
    swprintf_s(path, L"%siPod\\preferences.json", g_game_dir);
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 64 * 1024)
    {
        CloseHandle(file);
        return false;
    }
    std::vector<char> json(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    const bool read_ok = ReadFile(file, json.data(), static_cast<DWORD>(json.size()), &read, nullptr) &&
                         read == json.size();
    CloseHandle(file);
    if (!read_ok)
        return false;
    constexpr char key[] = "\"backgroundPlaybackEnabled\"";
    auto property = std::search(json.begin(), json.end(), std::begin(key), std::end(key) - 1);
    if (property == json.end())
        return false;
    auto cursor = property + sizeof(key) - 1;
    while (cursor != json.end() && isspace(static_cast<unsigned char>(*cursor)))
        ++cursor;
    if (cursor == json.end() || *cursor++ != ':')
        return false;
    while (cursor != json.end() && isspace(static_cast<unsigned char>(*cursor)))
        ++cursor;
    constexpr char value[] = "true";
    if (static_cast<size_t>(json.end() - cursor) < sizeof(value) - 1 ||
        !std::equal(std::begin(value), std::end(value) - 1, cursor))
        return false;
    cursor += sizeof(value) - 1;
    return cursor == json.end() || isspace(static_cast<unsigned char>(*cursor)) || *cursor == ',' || *cursor == '}';
}

bool CanRollbackActivation(ActivationPhase phase)
{
    return phase == ActivationPhase::PreActivation || phase == ActivationPhase::ConstructionBlocked ||
           phase == ActivationPhase::PatchedButConstructionBlocked || phase == ActivationPhase::FailedBeforeCommit;
}

bool RollbackActivation()
{
    if (!CanRollbackActivation(g_activation_phase.load()))
    {
        Log("ERROR structural rollback forbidden after constructor commit or uncertain patch recovery");
        return false;
    }
    if (!g_activation_patch_count)
        return true;
    ipod::patching::Patch rollback[320]{};
    for (SIZE_T i = 0; i < g_activation_patch_count; ++i)
    {
        const auto &source = g_activation_patches[g_activation_patch_count - 1 - i];
        DefinePatch(rollback[i], source.address, source.replacement, source.expected, source.length);
    }
    const auto result = ipod::patching::ApplyCheckedPatches(rollback, g_activation_patch_count);
    if (result == ipod::patching::Result::Applied)
    {
        g_activation_patch_count = 0;
        g_activation_phase.store(ActivationPhase::ConstructionBlocked);
        Log("native activation ROLLED BACK; feature set inactive");
        return true;
    }
    Log("CRITICAL native activation rollback could not be proven");
    g_activation_phase.store(ActivationPhase::PatchStateUncertain);
    return false;
}

void AbortPreConstructionActivation()
{
    const auto phase = g_activation_phase.load();
    if (!CanRollbackActivation(phase) && phase != ActivationPhase::PatchStateUncertain)
        return;
    if (phase == ActivationPhase::PatchStateUncertain || !RollbackActivation())
    {
        Log("CRITICAL constructor remains blocked: patch recovery uncertain; restart MGS4");
        return;
    }
    g_custom_runtime.clear();
    g_stock_disabled.fill(0);
    g_background_playback_enabled = false;
    g_activation_phase.store(ActivationPhase::FailedBeforeCommit);
    // Only the unchanged stock constructor may proceed after a proven pre-commit abort.
    if (g_catalog_ready_event && !SetEvent(g_catalog_ready_event))
        Log("CRITICAL could not release stock constructor after pre-commit abort");
}

bool CommitCatalogConstruction()
{
    auto expected = ActivationPhase::PatchedButConstructionBlocked;
    if (!g_activation_phase.compare_exchange_strong(expected, ActivationPhase::ConstructionStarted))
    {
        Log("ERROR constructor commit rejected: expansion not ready");
        return false;
    }
    // Commit BEFORE signalling: a waiting game thread can immediately retain expanded pointers.
    if (!SetEvent(g_catalog_ready_event))
    {
        g_activation_phase.store(ActivationPhase::FailedAfterCommit);
        Log("CRITICAL constructor signal failed; committed expansion retained; restart MGS4");
        return false;
    }
    Log("catalog construction COMMITTED; expansion patches and buffers retained for process lifetime");
    return true;
}

DWORD WINAPI WaitForCatalogDecision(HANDLE event, DWORD)
{
    InterlockedIncrement(&g_catalog_gate_waiters);
    const DWORD result = WaitForSingleObject(event, INFINITE);
    if (result != WAIT_OBJECT_0)
    {
        Log("CRITICAL constructor gate wait failed; construction blocked; restart MGS4");
        // A broken gate cannot safely continue into either storage layout.
        Sleep(INFINITE);
        return result;
    }
    InterlockedDecrement(&g_catalog_gate_waiters);
    return result;
}

bool HashFileSha256(const wchar_t *path, uint64_t expected_size, const std::array<uint8_t, 32> &expected_hash)
{
    static thread_local BCRYPT_ALG_HANDLE algorithm = nullptr;
    static thread_local DWORD object_size = 0;
    if (!algorithm)
    {
        DWORD got = 0;
        if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0 ||
            BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size),
                              sizeof(object_size), &got, 0) < 0)
            return false;
    }
    HANDLE file =
        CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return false;
    LARGE_INTEGER size{};
    bool ok = GetFileSizeEx(file, &size) && size.QuadPart >= 0 && static_cast<uint64_t>(size.QuadPart) == expected_size;
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::vector<BYTE> object;
    if (ok)
    {
        object.resize(object_size);
        ok = BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0, 0) >= 0;
    }
    BYTE buffer[65536]{};
    while (ok)
    {
        DWORD count = 0;
        if (!ReadFile(file, buffer, sizeof(buffer), &count, nullptr))
        {
            ok = false;
            break;
        }
        if (!count)
            break;
        ok = BCryptHashData(hash, buffer, count, 0) >= 0;
    }
    std::array<uint8_t, 32> actual{};
    if (ok)
        ok =
            BCryptFinishHash(hash, actual.data(), static_cast<ULONG>(actual.size()), 0) >= 0 && actual == expected_hash;
    if (hash)
        BCryptDestroyHash(hash);
    CloseHandle(file);
    return ok;
}

bool ValidateExecutableProfile()
{
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER *>(g_executable_start);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64 *>(g_executable_start + dos->e_lfanew);
    constexpr uint64_t expected_exe_size = 32684616;
    const std::array<uint8_t, 32> expected_exe_hash = {0x65, 0x6e, 0xde, 0x90, 0x0b, 0x03, 0x46, 0x7e, 0x4e, 0xd0, 0x5a,
                                                       0x00, 0xec, 0xa3, 0x5f, 0x0a, 0xc3, 0x06, 0xce, 0x57, 0xa8, 0xf2,
                                                       0x9a, 0x76, 0xa2, 0x0e, 0x5a, 0x54, 0x10, 0xd0, 0x29, 0x00};
    wchar_t exe_path[MAX_PATH]{};
    swprintf_s(exe_path, L"%smgs4.exe", g_game_dir);
    if (nt->OptionalHeader.SizeOfImage == 605806592 &&
        HashFileSha256(exe_path, expected_exe_size, expected_exe_hash))
        return true;
    Log("ERROR unsupported MGS4 executable profile; feature set inactive");
    return false;
}

bool InstallCatalogConstructorGate()
{
    BYTE *entry = g_executable_start + 0x7b940;
    const SIZE_T prefix = entry[0] == 0x40 ? 1 : 0;
    const BYTE instruction_shape[] = {0x55, 0x48, 0x83, 0xec, 0x50, 0x80, 0x3d};
    const SIZE_T displaced_length = 12 + prefix;
    if (memcmp(entry + prefix, instruction_shape, sizeof(instruction_shape)) || entry[displaced_length - 1] != 0)
    {
        Log("ERROR catalog constructor gate entry rejected");
        return false;
    }

    BYTE *code = reinterpret_cast<BYTE *>(VirtualAlloc(nullptr, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!code)
        return false;
    BYTE *trampoline = code;
    BYTE prologue[] = {0x40, 0x55, 0x48, 0x83, 0xec, 0x50, 0x49, 0xbb};
    const SIZE_T prologue_offset = prefix ? 0 : 1;
    const SIZE_T prologue_length = sizeof(prologue) - prologue_offset;
    memcpy(trampoline, prologue + prologue_offset, prologue_length);
    const SIZE_T displacement_offset = prefix + 7;
    const uintptr_t flag = reinterpret_cast<uintptr_t>(entry + displaced_length +
                                                        *reinterpret_cast<const int32_t *>(entry + displacement_offset));
    memcpy(trampoline + prologue_length, &flag, sizeof(flag));
    const BYTE compare[] = {0x41, 0x80, 0x3b, 0x00, 0xff, 0x25, 0x00, 0x00, 0x00, 0x00};
    memcpy(trampoline + prologue_length + sizeof(flag), compare, sizeof(compare));
    const uintptr_t continuation = reinterpret_cast<uintptr_t>(entry + displaced_length);
    memcpy(trampoline + prologue_length + sizeof(flag) + sizeof(compare), &continuation, sizeof(continuation));

    BYTE *thunk = code + 0x100;
    std::vector<BYTE> machine;
    auto bytes = [&](std::initializer_list<BYTE> value) { machine.insert(machine.end(), value); };
    auto pointer = [&](uintptr_t value) {
        const auto *p = reinterpret_cast<const BYTE *>(&value);
        machine.insert(machine.end(), p, p + sizeof(value));
    };
    // Preserve the exact native entry context while waiting. The final absolute
    // indirect jump leaves every register and the original stack untouched.
    bytes({0x9c, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53});
    bytes({0x48, 0x81, 0xec, 0x88, 0x00, 0x00, 0x00});
    bytes({0xf3, 0x0f, 0x7f, 0x44, 0x24, 0x20, 0xf3, 0x0f, 0x7f, 0x4c, 0x24, 0x30});
    bytes({0xf3, 0x0f, 0x7f, 0x54, 0x24, 0x40, 0xf3, 0x0f, 0x7f, 0x5c, 0x24, 0x50});
    bytes({0xf3, 0x0f, 0x7f, 0x64, 0x24, 0x60, 0xf3, 0x0f, 0x7f, 0x6c, 0x24, 0x70});
    bytes({0x48, 0xb9});
    pointer(reinterpret_cast<uintptr_t>(g_catalog_ready_event));
    bytes({0xba, 0xff, 0xff, 0xff, 0xff, 0x48, 0xb8});
    pointer(reinterpret_cast<uintptr_t>(WaitForCatalogDecision));
    bytes({0xff, 0xd0});
    bytes({0xf3, 0x0f, 0x6f, 0x44, 0x24, 0x20, 0xf3, 0x0f, 0x6f, 0x4c, 0x24, 0x30});
    bytes({0xf3, 0x0f, 0x6f, 0x54, 0x24, 0x40, 0xf3, 0x0f, 0x6f, 0x5c, 0x24, 0x50});
    bytes({0xf3, 0x0f, 0x6f, 0x64, 0x24, 0x60, 0xf3, 0x0f, 0x6f, 0x6c, 0x24, 0x70});
    bytes({0x48, 0x81, 0xc4, 0x88, 0x00, 0x00, 0x00});
    bytes({0x41, 0x5b, 0x41, 0x5a, 0x41, 0x59, 0x41, 0x58, 0x5a, 0x59, 0x58, 0x9d});
    bytes({0xff, 0x25, 0x00, 0x00, 0x00, 0x00});
    pointer(reinterpret_cast<uintptr_t>(trampoline));
    memcpy(thunk, machine.data(), machine.size());

    DWORD old = 0;
    if (!VirtualProtect(code, 0x1000, PAGE_EXECUTE_READ, &old))
    {
        VirtualFree(code, 0, MEM_RELEASE);
        return false;
    }
    BYTE replacement[13] = {0x48, 0xb8};
    const uintptr_t hook = reinterpret_cast<uintptr_t>(thunk);
    memcpy(replacement + 2, &hook, sizeof(hook));
    replacement[10] = 0xff;
    replacement[11] = 0xe0;
    memset(replacement + 12, 0x90, sizeof(replacement) - 12);
    ipod::patching::Patch patch{};
    DefinePatch(patch, entry, entry, replacement, displaced_length);
    const auto gate_result = ipod::patching::ApplyCheckedPatches(&patch, 1);
    if (gate_result != ipod::patching::Result::Applied)
    {
        if (gate_result != ipod::patching::Result::RecoveryFailed)
            VirtualFree(code, 0, MEM_RELEASE);
        else
            Log("CRITICAL gate patch recovery uncertain; trampoline retained, no expansion will be installed");
        Log("ERROR catalog constructor gate installation failed");
        return false;
    }
    Log("CATALOG_EXPANSION context-preserving constructor gate installed");
    return true;
}

bool LoadDeployment()
{
    wchar_t manifest_path[MAX_PATH]{};
    swprintf_s(manifest_path, L"%siPod\\deployment.bin", g_game_dir);
    HANDLE mf = CreateFileW(manifest_path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL,
                            nullptr);
    if (mf == INVALID_HANDLE_VALUE)
    {
        Log("ERROR deployment manifest absent; stock retained");
        return false;
    }
    LARGE_INTEGER ml{};
    if (!GetFileSizeEx(mf, &ml) || ml.QuadPart <= 0 || ml.QuadPart > 1024 * 1024)
    {
        CloseHandle(mf);
        Log("ERROR deployment manifest size rejected; stock retained");
        return false;
    }
    std::vector<uint8_t> mb(static_cast<size_t>(ml.QuadPart));
    DWORD mr = 0;
    bool mok = ReadFile(mf, mb.data(), static_cast<DWORD>(mb.size()), &mr, nullptr) && mr == mb.size();
    CloseHandle(mf);
    if (!mok)
    {
        Log("ERROR deployment manifest read failed; stock retained");
        return false;
    }
    ipod::deployment::current::Manifest vm;
    ipod::deployment::current::Error ve{};
    if (!ipod::deployment::current::Parse(mb.data(), mb.size(), vm, ve))
    {
        char why[160]{};
        std::snprintf(why, sizeof(why), "ERROR deployment validation rejected code=%u; stock retained",
                      static_cast<unsigned>(ve));
        Log(why);
        return false;
    }
    if (vm.entries.size() > kNativeCatalogCapacity - kStockTrackCount)
    {
        Log("ERROR deployment exceeds native 1024-record catalog storage; stock retained");
        return false;
    }
    {
        char parsed[192]{};
        std::snprintf(parsed, sizeof(parsed), "deployment parsed generation=%llu custom_tracks=%zu",
                      static_cast<unsigned long long>(vm.generation), vm.entries.size());
        Log(parsed);
    }
    std::unordered_set<std::string> dbm_requests, dbm_paths;
    for (const auto &e : vm.entries)
    {
        if (!dbm_requests.insert(e.dbm_request_path).second)
        {
            Log("ERROR duplicate DBM request path; stock retained");
            return false;
        }
        if (!dbm_paths.insert(e.dbm_path).second)
        {
            Log("ERROR duplicate deployed DBM path; stock retained");
            return false;
        }
    }
    g_custom_runtime.clear();
    g_stock_disabled.fill(0);
    for (size_t i = 0; i < vm.stock_enabled.size(); ++i)
        g_stock_disabled[i] = vm.stock_enabled[i] ? 0 : 1;
    auto read_all = [](const wchar_t *p) {
        std::vector<uint8_t> b;
        HANDLE f =
            CreateFileW(p, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f == INVALID_HANDLE_VALUE)
            return b;
        LARGE_INTEGER n{};
        if (GetFileSizeEx(f, &n) && n.QuadPart > 0 && n.QuadPart < INT32_MAX)
        {
            b.resize(static_cast<size_t>(n.QuadPart));
            DWORD r = 0;
            if (!ReadFile(f, b.data(), static_cast<DWORD>(b.size()), &r, nullptr) || r != b.size())
                b.clear();
        }
        CloseHandle(f);
        return b;
    };
    std::atomic_size_t next_dbm{};
    std::atomic_bool dbms_valid{true};
    auto validate_dbms = [&]() {
        wchar_t relative[MAX_PATH]{};
        wchar_t absolute[MAX_PATH]{};
        while (dbms_valid.load(std::memory_order_relaxed))
        {
            const size_t index = next_dbm.fetch_add(1, std::memory_order_relaxed);
            if (index >= vm.entries.size())
                break;
            const auto &entry = vm.entries[index];
            if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, entry.dbm_path.c_str(), -1, relative, MAX_PATH))
            {
                dbms_valid.store(false, std::memory_order_relaxed);
                break;
            }
            swprintf_s(absolute, L"%s%ls", g_game_dir, relative);
            if (!HashFileSha256(absolute, entry.dbm_size, entry.dbm_sha256) ||
                !ipod::deployment::current::ValidateDbm(entry, read_all(absolute)))
            {
                dbms_valid.store(false, std::memory_order_relaxed);
                break;
            }
        }
    };
    std::array<std::thread, 8> validation_threads;
    for (auto &thread : validation_threads)
        thread = std::thread(validate_dbms);
    for (auto &thread : validation_threads)
        thread.join();
    if (!dbms_valid.load(std::memory_order_relaxed))
    {
        Log("ERROR DBM artifact validation failed; stock retained");
        return false;
    }
    {
        char validated[128]{};
        std::snprintf(validated, sizeof(validated), "deployment DBM validation complete count=%zu", vm.entries.size());
        Log(validated);
    }
    std::unordered_map<std::wstring, std::pair<uint64_t, std::array<uint8_t, 32>>> verified_banks;
    for (size_t o = 0; o < vm.entries.size(); ++o)
    {
        const auto &e = vm.entries[o];
        auto rt = std::make_unique<CustomTrack>();
        rt->runtime_id = e.runtime_id;
        rt->duration_seconds = e.duration_seconds;
        rt->podcast = e.classification == ipod::deployment::Classification::Podcast;
        rt->catalog_index = kStockTrackCount + static_cast<DWORD>(o);
        rt->control_id = e.control_id;
        rt->descriptor_id = e.control_id + 300;
        rt->dbm_request_path = e.dbm_request_path;
        rt->descriptor_event_path = e.descriptor_event_path;
        rt->fmod_event_path = "event:" + e.descriptor_event_path;
        std::size_t full_source_path_length = 0;
        bool used_extended_source_path = false;
        if (!ipod::runtime::BuildPlaybackSourcePath(g_game_dir, e.source_path, rt->source_path,
                                                    &full_source_path_length, &used_extended_source_path))
            return false;
        if (used_extended_source_path)
        {
            char shortened[192]{};
            std::snprintf(shortened, sizeof(shortened),
                          "source path extended catalog=%lu full_chars=%zu",
                          static_cast<unsigned long>(rt->catalog_index), full_source_path_length);
            Log(shortened);
        }
        wchar_t rel[MAX_PATH]{};
        if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, e.dbm_path.c_str(), -1, rel, MAX_PATH))
            return false;
        swprintf_s(manifest_path, L"%s%ls", g_game_dir, rel);
        rt->dbm_path = manifest_path;
        wchar_t bn[MAX_PATH]{};
        if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, e.bank_name.c_str(), -1, bn, MAX_PATH))
            return false;
        swprintf_s(manifest_path, L"%scommon\\bank\\default\\%ls", g_game_dir, bn);
        rt->bank_path = manifest_path;
        const auto bank_validation = std::make_pair(e.bank_size, e.bank_sha256);
        const auto known_bank = verified_banks.find(rt->bank_path);
        if (known_bank != verified_banks.end())
        {
            if (known_bank->second != bank_validation)
                return false;
        }
        else
        {
            if (!HashFileSha256(rt->bank_path.c_str(), e.bank_size, e.bank_sha256))
                return false;
            verified_banks.emplace(rt->bank_path, bank_validation);
        }
        g_custom_runtime.push_back(std::move(rt));
    }
    char ok[192]{};
    std::snprintf(ok, sizeof(ok), "deployment loaded generation=%llu custom_tracks=%zu",
                  static_cast<unsigned long long>(vm.generation), g_custom_runtime.size());
    Log(ok);
    return true;
}

void Log(const char *message)
{
    SYSTEMTIME now{};
    GetLocalTime(&now);
    constexpr char format[] = "%04u-%02u-%02u %02u:%02u:%02u.%03u [pid=%lu] %s\r\n";
    const DWORD process_id = GetCurrentProcessId();
    const int length = std::snprintf(nullptr, 0, format, now.wYear, now.wMonth, now.wDay, now.wHour,
                                     now.wMinute, now.wSecond, now.wMilliseconds, process_id, message);
    if (length <= 0)
        return;
    try
    {
        std::vector<char> line(static_cast<size_t>(length) + 1);
        std::snprintf(line.data(), line.size(), format, now.wYear, now.wMonth, now.wDay, now.wHour,
                      now.wMinute, now.wSecond, now.wMilliseconds, process_id, message);
        HANDLE file = CreateFileW(g_log_path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                  OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;
        DWORD written = 0;
        WriteFile(file, line.data(), static_cast<DWORD>(length), &written, nullptr);
        CloseHandle(file);
    }
    catch (const std::bad_alloc &)
    {
        // Diagnostics must not make a native callback fail under memory pressure.
    }
}

bool ValidatePodcastPatchSet()
{
    bool has_podcast = false;
    for (const auto &track : g_custom_runtime)
        has_podcast |= track->podcast;
    if (!has_podcast)
        return true;

    const BYTE menu_expected[] = {0x48, 0x83, 0xfe, 0x03};
    const BYTE navigation_expected[] = {0xb8, 0x02, 0x00, 0x00, 0x00};
    BYTE *call = g_executable_start + 0xd07715;
    const BYTE songs_traversal_expected[] = {0x85, 0xc0, 0x75, 0x7c, 0x0f, 0xbf, 0x0d, 0xdd, 0x76, 0x06, 0x01};
    if (memcmp(g_executable_start + 0xd06323, menu_expected, sizeof(menu_expected)) != 0 ||
        memcmp(g_executable_start + 0xd028ba, navigation_expected, sizeof(navigation_expected)) != 0 ||
        call[0] != 0xe8 ||
        memcmp(g_executable_start + 0xd0771a, songs_traversal_expected, sizeof(songs_traversal_expected)) != 0)
    {
        Log("ERROR preflight rejected mismatched Podcast patch bytes; activation remains inactive");
        return false;
    }
    int32_t displacement = 0;
    memcpy(&displacement, call + 1, sizeof(displacement));
    if (call + 5 + displacement != g_executable_start + 0x7b8b0)
    {
        Log("ERROR preflight rejected mismatched Songs predicate target; activation remains inactive");
        return false;
    }
    return true;
}

struct FileHookSlots
{
    BYTE *wide{};
    BYTE *ansi{};
    uintptr_t wide_original{};
    uintptr_t ansi_original{};
};

BYTE *PreparePodcastThunks()
{
    BYTE *thunk = nullptr;
    const uintptr_t module_base = reinterpret_cast<uintptr_t>(g_executable_start);
    for (uintptr_t delta = 0x20000000; delta <= 0x70000000 && !thunk; delta += 0x10000)
        thunk = reinterpret_cast<BYTE *>(VirtualAlloc(reinterpret_cast<void *>(module_base + delta), 0x1000,
                                                      MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!thunk)
        return nullptr;
    const BYTE prefix[] = {0xf6, 0x47, 0x08, 0x04, 0x75, 0x0c, 0x48, 0xb8};
    memcpy(thunk, prefix, sizeof(prefix));
    const uintptr_t predicate_address = reinterpret_cast<uintptr_t>(g_executable_start + 0x7b8b0);
    memcpy(thunk + 8, &predicate_address, 8);
    const BYTE suffix[] = {0xff, 0xe0, 0xb8, 0x01, 0x00, 0x00, 0x00, 0xc3};
    memcpy(thunk + 16, suffix, sizeof(suffix));

    BYTE *traversal = thunk + 64;
    const BYTE traversal_prefix[] = {0x85, 0xc0, 0x74, 0x12, 0xf6, 0x47, 0x08, 0x04, 0x75, 0x25, 0x48, 0xb8};
    memcpy(traversal, traversal_prefix, sizeof(traversal_prefix));
    const uintptr_t unavailable = reinterpret_cast<uintptr_t>(g_executable_start + 0xd0779a);
    memcpy(traversal + 12, &unavailable, 8);
    traversal[20] = 0xff;
    traversal[21] = 0xe0;
    traversal[22] = 0x48;
    traversal[23] = 0xb8;
    const uintptr_t count = reinterpret_cast<uintptr_t>(g_executable_start + 0x1d6ee02);
    memcpy(traversal + 24, &count, 8);
    const BYTE eligible_suffix[] = {0x0f, 0xbf, 0x08, 0x48, 0xb8};
    memcpy(traversal + 32, eligible_suffix, sizeof(eligible_suffix));
    const uintptr_t eligible = reinterpret_cast<uintptr_t>(g_executable_start + 0xd07725);
    memcpy(traversal + 37, &eligible, 8);
    traversal[45] = 0xff;
    traversal[46] = 0xe0;
    const BYTE skip_prefix[] = {0x41, 0xff, 0xc6, 0x48, 0x81, 0xc7, 0x10, 0x01, 0x00, 0x00, 0x48, 0xb8};
    memcpy(traversal + 47, skip_prefix, sizeof(skip_prefix));
    const uintptr_t skip = reinterpret_cast<uintptr_t>(g_executable_start + 0xd077b3);
    memcpy(traversal + 59, &skip, 8);
    traversal[67] = 0xff;
    traversal[68] = 0xe0;
    auto write_stock_thunk = [&](BYTE *output, uintptr_t fallback) {
        const BYTE prefix[] = {0x83, 0xf9, 0x49, 0x73, 0x16, 0x48, 0xb8};
        memcpy(output, prefix, sizeof(prefix));
        const uintptr_t mask = reinterpret_cast<uintptr_t>(g_stock_disabled.data());
        memcpy(output + 7, &mask, 8);
        const BYTE middle[] = {0x80, 0x3c, 0x08, 0x00, 0x74, 0x06, 0xb8, 0xff, 0xff, 0xff, 0xff, 0xc3, 0x48, 0xb8};
        memcpy(output + 15, middle, sizeof(middle));
        memcpy(output + 29, &fallback, 8);
        output[37] = 0xff;
        output[38] = 0xe0;
    };
    constexpr SIZE_T stock_thunk_offset = 256, stock_songs_thunk_offset = 320;
    static_assert(64 + 69 <= stock_thunk_offset && stock_thunk_offset + 39 <= stock_songs_thunk_offset &&
                      stock_songs_thunk_offset + 39 <= 0x1000,
                  "generated thunk regions must not overlap");
    write_stock_thunk(thunk + stock_thunk_offset, predicate_address);
    write_stock_thunk(thunk + stock_songs_thunk_offset, reinterpret_cast<uintptr_t>(thunk));
    BYTE *selection = thunk + 384;
    selection[0] = 0x48;
    selection[1] = 0xb8;
    const uintptr_t selection_hook = reinterpret_cast<uintptr_t>(&HookSelectedIpodIndex);
    memcpy(selection + 2, &selection_hook, 8);
    selection[10] = 0xff;
    selection[11] = 0xe0;
    DWORD old = 0;
    if (!VirtualProtect(thunk, 0x1000, PAGE_EXECUTE_READ, &old))
    {
        VirtualFree(thunk, 0, MEM_RELEASE);
        return nullptr;
    }
    return thunk;
}

bool AllocateExpansionStorage()
{
    constexpr SIZE_T runtime_size = kNativeCatalogCapacity * kRuntimeRecordSize;
    // The constructor keeps an end pointer at record[capacity] + 0x224.
    // Reserve one trailing record so that pointer remains within committed storage.
    constexpr SIZE_T page = 0x1000;
    constexpr SIZE_T runtime_span = (runtime_size + kRuntimeRecordSize + page - 1) & ~(page - 1);
    constexpr SIZE_T playback_size = kNativeCatalogCapacity * sizeof(int32_t);
    constexpr SIZE_T ui_orders_size = 4 * kNativeCatalogCapacity * sizeof(int32_t);
    constexpr SIZE_T ui_records_size = kNativeCatalogCapacity * kUiRecordSize;
    constexpr SIZE_T sorting_size = kNativeCatalogCapacity * kUiRecordSize;
    constexpr SIZE_T playback_alignment_bias = 8; // native base 0x1DA2DC8 is 8 mod 16
    constexpr SIZE_T ui_orders_alignment_bias = 12; // native base 0x1D6EF5C is 12 mod 16
    constexpr SIZE_T playback_span = 0x2000;
    constexpr SIZE_T ui_orders_span = 0x5000;
    constexpr SIZE_T playback_offset = runtime_span + page;
    constexpr SIZE_T ui_orders_offset = playback_offset + playback_span + page;
    constexpr SIZE_T ui_records_offset = ui_orders_offset + ui_orders_span + page;
    constexpr SIZE_T sorting_offset = ui_records_offset + ui_records_size + page;
    constexpr SIZE_T thunk_offset = sorting_offset + sorting_size + page;
    constexpr SIZE_T allocation_size = thunk_offset + page;
    const uintptr_t module = reinterpret_cast<uintptr_t>(g_executable_start);
    for (uintptr_t delta = 0x31000000; delta <= 0x70000000 && !g_expansion_allocation; delta += 0x10000)
        g_expansion_allocation = reinterpret_cast<BYTE *>(VirtualAlloc(
            reinterpret_cast<void *>(module + delta), allocation_size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!g_expansion_allocation)
        return false;
    g_expanded_runtime_records = g_expansion_allocation;
    g_expanded_playback_order =
        reinterpret_cast<int32_t *>(g_expansion_allocation + playback_offset + playback_alignment_bias);
    g_expanded_ui_orders =
        reinterpret_cast<int32_t *>(g_expansion_allocation + ui_orders_offset + ui_orders_alignment_bias);
    g_expanded_ui_records = g_expansion_allocation + ui_records_offset;
    g_expanded_sorting_workspace = g_expansion_allocation + sorting_offset;
    g_expansion_thunks = g_expansion_allocation + thunk_offset;
    DWORD ignored = 0;
    if (!VirtualProtect(g_expansion_allocation + runtime_span, page, PAGE_NOACCESS, &ignored) ||
        !VirtualProtect(g_expansion_allocation + playback_offset + playback_span, page, PAGE_NOACCESS, &ignored) ||
        !VirtualProtect(g_expansion_allocation + ui_orders_offset + ui_orders_span, page, PAGE_NOACCESS, &ignored) ||
        !VirtualProtect(g_expansion_allocation + ui_records_offset + ui_records_size, page, PAGE_NOACCESS, &ignored) ||
        !VirtualProtect(g_expansion_allocation + sorting_offset + sorting_size, page, PAGE_NOACCESS, &ignored))
        return false;
    char line[320]{};
    std::snprintf(line, sizeof(line),
                  "CATALOG_EXPANSION ALLOC records=%p/%zu order=%p/%zu ui_orders=%p/%zu ui_records=%p/%zu sort=%p/%zu",
                  g_expanded_runtime_records, runtime_size, g_expanded_playback_order, playback_size,
                  g_expanded_ui_orders, ui_orders_size, g_expanded_ui_records, ui_records_size,
                  g_expanded_sorting_workspace, sorting_size);
    Log(line);
    return true;
}

void ClearExpandedMenuLists()
{
    memset(g_expanded_ui_orders, 0, 4 * kNativeCatalogCapacity * sizeof(*g_expanded_ui_orders));
}

BYTE *EmitCallThunk(BYTE *output, const void *function, bool restore_menu_clear_size)
{
    const BYTE prologue[] = {0x48, 0x83, 0xec, 0x28, 0x48, 0xb8};
    memcpy(output, prologue, sizeof(prologue));
    const uintptr_t target = reinterpret_cast<uintptr_t>(function);
    memcpy(output + 6, &target, sizeof(target));
    const BYTE call_epilogue[] = {0xff, 0xd0, 0x48, 0x83, 0xc4, 0x28};
    memcpy(output + 14, call_epilogue, sizeof(call_epilogue));
    SIZE_T cursor = 20;
    if (restore_menu_clear_size)
    {
        const BYTE set_size[] = {0xba, 0xb4, 0x09, 0x00, 0x00};
        memcpy(output + cursor, set_size, sizeof(set_size));
        cursor += sizeof(set_size);
    }
    output[cursor++] = 0xc3;
    return output;
}

BYTE *EmitResetQueueThunk(BYTE *output, uintptr_t continuation)
{
    SIZE_T cursor = 0;
    const BYTE save_and_load[] = {0x57, 0x51, 0x50, 0x48, 0xbf}; // push rdi,rcx,rax; mov rdi,imm64
    memcpy(output + cursor, save_and_load, sizeof(save_and_load));
    cursor += sizeof(save_and_load);
    const uintptr_t queue = reinterpret_cast<uintptr_t>(g_expanded_playback_order);
    memcpy(output + cursor, &queue, sizeof(queue));
    cursor += sizeof(queue);
    const BYTE clear[] = {0x31, 0xc0, 0xb9, 0x00, 0x04, 0x00, 0x00, 0xf3, 0xab,
                          0x58, 0x59, 0x5f, 0x50, 0x48, 0xb8};
    memcpy(output + cursor, clear, sizeof(clear));
    cursor += sizeof(clear);
    const uintptr_t native_state = reinterpret_cast<uintptr_t>(g_executable_start + 0x1da3004);
    memcpy(output + cursor, &native_state, sizeof(native_state));
    cursor += sizeof(native_state);
    const BYTE set_state[] = {0x48, 0xc7, 0x00, 0x01, 0x00, 0x00, 0x00, 0x58, 0x48, 0xb8};
    memcpy(output + cursor, set_state, sizeof(set_state));
    cursor += sizeof(set_state);
    memcpy(output + cursor, &continuation, sizeof(continuation));
    cursor += sizeof(continuation);
    output[cursor++] = 0xff;
    output[cursor++] = 0xe0;
    return output;
}

BYTE *EmitOrderCopyThunk(BYTE *output, uintptr_t continuation, BYTE counter_kind)
{
    // At the first seven copy sites RAX is the destination and RCX is the source.
    const BYTE copy[] = {0x57, 0x56, 0x48, 0x89, 0xc7, 0x48, 0x89, 0xce, 0xb9, 0x00, 0x10, 0x00, 0x00,
                         0xf3, 0xa4, 0x48, 0x89, 0xf8, 0x48, 0x89, 0xf1, 0x5e, 0x5f};
    memcpy(output, copy, sizeof(copy));
    SIZE_T cursor = sizeof(copy);
    if (counter_kind == 0)
    {
        const BYTE zero[] = {0x31, 0xf6}; // ESI
        memcpy(output + cursor, zero, sizeof(zero));
        cursor += sizeof(zero);
    }
    else
    {
        const BYTE zero[] = {0x31, 0xd2}; // EDX
        memcpy(output + cursor, zero, sizeof(zero));
        cursor += sizeof(zero);
    }
    output[cursor++] = 0x48;
    output[cursor++] = 0xb8;
    memcpy(output + cursor, &continuation, sizeof(continuation));
    cursor += sizeof(continuation);
    output[cursor++] = 0xff;
    output[cursor++] = 0xe0;
    return output;
}

BYTE *EmitFinalOrderCopyThunk(BYTE *output, uintptr_t continuation)
{
    // The final site already has destination/source in RDI/RSI.
    const BYTE copy[] = {0xb9, 0x00, 0x10, 0x00, 0x00, 0xf3, 0xa4, 0x45, 0x31, 0xff, 0x48, 0xb8};
    memcpy(output, copy, sizeof(copy));
    memcpy(output + sizeof(copy), &continuation, sizeof(continuation));
    output[sizeof(copy) + sizeof(continuation)] = 0xff;
    output[sizeof(copy) + sizeof(continuation) + 1] = 0xe0;
    return output;
}

bool AddExpansionStoragePatches(SIZE_T &n)
{
    constexpr uintptr_t old_runtime = 0x1d911e0;
    constexpr uintptr_t old_order = 0x1da2de8;
    constexpr uintptr_t old_ui_orders = 0x1d6ef5c;
    constexpr uintptr_t old_ui_order_size = kOriginalCatalogCapacity * sizeof(int32_t);
    constexpr uintptr_t new_ui_order_size = kNativeCatalogCapacity * sizeof(int32_t);
    constexpr uintptr_t old_ui_records = 0x23f2d400;
    constexpr uintptr_t old_sorting_workspace = 0x23f35c10;
    const uintptr_t module = reinterpret_cast<uintptr_t>(g_executable_start);
    for (const auto &reference : kExpansionReferences)
    {
        // The identity-handoff entry is replaced as one checked detour below;
        // its trampoline already receives the relocated record-table address.
        if (reference.rva >= 0x7c700 && reference.rva < 0x7c711)
            continue;
        uintptr_t target = 0;
        switch (reference.storage)
        {
        case ExpansionStorage::RuntimeRecords:
            target = reinterpret_cast<uintptr_t>(g_expanded_runtime_records) + reference.old_target_rva - old_runtime;
            break;
        case ExpansionStorage::PlaybackOrder:
            target = reinterpret_cast<uintptr_t>(g_expanded_playback_order) + reference.old_target_rva - old_order;
            break;
        case ExpansionStorage::UiOrders:
        {
            const uintptr_t relative = reference.old_target_rva - old_ui_orders;
            const uintptr_t array = relative / old_ui_order_size;
            const uintptr_t element_offset = relative % old_ui_order_size;
            target = reinterpret_cast<uintptr_t>(g_expanded_ui_orders) + array * new_ui_order_size + element_offset;
            break;
        }
        case ExpansionStorage::UiRecords:
            target = reference.old_target_rva == old_ui_records + kOriginalCatalogCapacity * kUiRecordSize
                         ? reinterpret_cast<uintptr_t>(g_expanded_ui_records) +
                               kNativeCatalogCapacity * kUiRecordSize
                         : reinterpret_cast<uintptr_t>(g_expanded_ui_records) + reference.old_target_rva -
                               old_ui_records;
            break;
        case ExpansionStorage::SortingWorkspace:
            target = reinterpret_cast<uintptr_t>(g_expanded_sorting_workspace) + reference.old_target_rva - old_sorting_workspace;
            break;
        }
        const uintptr_t instruction = module + reference.rva;
        const intptr_t displacement = reference.rip_relative
                                          ? static_cast<intptr_t>(target - (instruction + reference.instruction_size))
                                          : static_cast<intptr_t>(target - module);
        if (displacement < INT32_MIN || displacement > INT32_MAX || n >= std::size(g_activation_patches))
            return false;
        const int32_t expected = reference.expected_displacement;
        const int32_t replacement = static_cast<int32_t>(displacement);
        DefinePatch(g_activation_patches[n++], reinterpret_cast<BYTE *>(instruction + reference.displacement_offset),
                    &expected, &replacement, sizeof(replacement));
    }
    auto add_displacement = [&](DWORD rva, BYTE offset, const BYTE expected_bytes[4], intptr_t value) {
        if (value < INT32_MIN || value > INT32_MAX || n >= std::size(g_activation_patches))
            return false;
        const int32_t replacement = static_cast<int32_t>(value);
        DefinePatch(g_activation_patches[n++], g_executable_start + rva + offset, expected_bytes, &replacement, 4);
        return true;
    };
    const BYTE queue_delta_bytes[4] = {0x08, 0x1c, 0x01, 0x00};
    const intptr_t expanded_queue_delta = reinterpret_cast<BYTE *>(g_expanded_playback_order) -
                                           g_expanded_runtime_records;
    for (DWORD rva : {0x7c3c3u, 0x7c4b1u, 0x7c4f3u, 0x7cc2fu})
        if (!add_displacement(rva, 4, queue_delta_bytes, expanded_queue_delta))
            return false;

    const uintptr_t end_instruction = module + 0x7ca37;
    const uintptr_t expanded_runtime_end = reinterpret_cast<uintptr_t>(g_expanded_runtime_records) +
                                           kNativeCatalogCapacity * kRuntimeRecordSize + 0x224;
    const BYTE runtime_end_bytes[4] = {0xc6, 0x65, 0xd2, 0x01};
    if (!add_displacement(0x7ca37, 3, runtime_end_bytes,
                          expanded_runtime_end - (end_instruction + 7)))
        return false;

    const BYTE minus_200[4] = {0x00, 0xfe, 0xff, 0xff};
    const BYTE plus_200[4] = {0x00, 0x02, 0x00, 0x00};
    if (!add_displacement(0xd02360, 2, minus_200, -static_cast<intptr_t>(new_ui_order_size)) ||
        !add_displacement(0xd052f1, 2, plus_200, static_cast<intptr_t>(new_ui_order_size)))
        return false;

    constexpr uintptr_t native_ui_state = 0x1d6edb0;
    const intptr_t displayed_from_native = reinterpret_cast<uintptr_t>(g_expanded_ui_orders) - (module + native_ui_state);
    const intptr_t saved_selection_from_native =
        reinterpret_cast<uintptr_t>(g_expanded_ui_orders) + 3 * new_ui_order_size - (module + native_ui_state);
    const BYTE old_displayed_from_native[4] = {0xac, 0x01, 0x00, 0x00};
    const BYTE old_saved_selection_from_native[4] = {0xac, 0x07, 0x00, 0x00};
    if (!add_displacement(0xd091ed, 3, old_displayed_from_native, displayed_from_native))
        return false;
    for (DWORD rva : {0xd092bcu, 0xd09ac3u, 0xd09af2u, 0xd09b26u})
    {
        const BYTE offset = rva >= 0xd09ac3 ? 4 : 3;
        if (!add_displacement(rva, offset, old_saved_selection_from_native, saved_selection_from_native))
            return false;
    }
    for (DWORD rva : {0xd09270u, 0xd09300u})
        if (!add_displacement(rva, 2, minus_200, -static_cast<intptr_t>(new_ui_order_size)))
            return false;

    // Shuffle construction walks pairs of track-information records. Its old
    // sentinel was record[129]; retain that relationship at expanded capacity.
    const uintptr_t shuffle_end_instruction = module + 0xd02259;
    const uintptr_t shuffle_end = reinterpret_cast<uintptr_t>(g_expanded_ui_records) +
                                  (kNativeCatalogCapacity + 1) * kUiRecordSize;
    const BYTE old_shuffle_end[4] = {0xb0, 0x3a, 0x23, 0x23};
    if (!add_displacement(0xd02259, 3, old_shuffle_end, shuffle_end - (shuffle_end_instruction + 7)))
        return false;

    BYTE *cursor = g_expansion_thunks;
    BYTE *menu_clear_thunk = cursor;
    EmitCallThunk(cursor, reinterpret_cast<const void *>(&ClearExpandedMenuLists), true);
    cursor += 32;

    auto add_relative_branch = [&](DWORD rva, BYTE opcode, BYTE *target) {
        if (n >= std::size(g_activation_patches))
            return false;
        BYTE expected[5]{};
        memcpy(expected, g_executable_start + rva, sizeof(expected));
        BYTE replacement[5] = {opcode};
        const intptr_t delta = target - (g_executable_start + rva + 5);
        if (delta < INT32_MIN || delta > INT32_MAX)
            return false;
        const int32_t relative = static_cast<int32_t>(delta);
        memcpy(replacement + 1, &relative, sizeof(relative));
        DefinePatch(g_activation_patches[n++], g_executable_start + rva, expected, replacement, sizeof(replacement));
        return true;
    };
    for (DWORD rva : {0x7bf63u, 0x7c29cu, 0x7cb6bu})
    {
        BYTE *reset_thunk = cursor;
        EmitResetQueueThunk(cursor, module + rva + 11);
        cursor += 64;
        if (!add_relative_branch(rva, 0xe9, reset_thunk))
            return false;
    }
    for (DWORD rva : {0x8a4be0u, 0x8a6822u})
        if (!add_relative_branch(rva, 0xe8, menu_clear_thunk))
            return false;

    struct CopySite
    {
        DWORD rva;
        DWORD continuation;
        BYTE counter_kind;
    };
    constexpr CopySite copy_sites[] = {{0xd035b1, 0xd03604, 0}, {0xd03930, 0xd03983, 0},
                                       {0xd03c30, 0xd03c83, 0}, {0xd03f73, 0xd03fc6, 0},
                                       {0xd04230, 0xd04283, 0}, {0xd095c4, 0xd09617, 1},
                                       {0xd09630, 0xd09683, 1}};
    for (const auto &site : copy_sites)
    {
        BYTE *copy_thunk = cursor;
        EmitOrderCopyThunk(cursor, module + site.continuation, site.counter_kind);
        cursor += 48;
        if (!add_relative_branch(site.rva, 0xe9, copy_thunk))
            return false;
    }
    BYTE *final_copy_thunk = cursor;
    EmitFinalOrderCopyThunk(cursor, module + 0xd097b3);
    cursor += 32;
    if (!add_relative_branch(0xd09760, 0xe9, final_copy_thunk))
        return false;
    DWORD old_protection = 0;
    if (!VirtualProtect(g_expansion_thunks, 0x1000, PAGE_EXECUTE_READ, &old_protection))
        return false;
    auto add_capacity = [&](DWORD rva, BYTE immediate_offset) {
        const uint32_t expected = kOriginalCatalogCapacity;
        const uint32_t replacement = kNativeCatalogCapacity;
        DefinePatch(g_activation_patches[n++], g_executable_start + rva + immediate_offset, &expected, &replacement,
                    sizeof(replacement));
    };
    add_capacity(0x7b9dc, 2);
    return true;
}

bool AddBackgroundPlaybackPatches(SIZE_T &n)
{
    if (!g_background_playback_enabled)
        return true;
    const uintptr_t base = reinterpret_cast<uintptr_t>(g_executable_start);
    for (uintptr_t offset = 0x21000000; offset <= 0x70000000 && !g_background_playback_thunks; offset += 0x10000)
        g_background_playback_thunks = static_cast<BYTE *>(VirtualAlloc(
            reinterpret_cast<void *>(base + offset), 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!g_background_playback_thunks)
        return false;

    BYTE *cursor = g_background_playback_thunks;
    auto emit8 = [&](BYTE value) { *cursor++ = value; };
    auto emit32 = [&](uint32_t value) { memcpy(cursor, &value, 4); cursor += 4; };
    auto emit64 = [&](uint64_t value) { memcpy(cursor, &value, 8); cursor += 8; };
    auto absolute_jump = [&](BYTE *destination) {
        const BYTE op[] = {0xff, 0x25, 0, 0, 0, 0};
        memcpy(cursor, op, sizeof(op)); cursor += sizeof(op);
        emit64(reinterpret_cast<uint64_t>(destination));
    };
    auto define_jump = [&](DWORD rva, const BYTE *expected, SIZE_T length, BYTE *thunk) {
        BYTE replacement[16] = {0xe9};
        const intptr_t delta = thunk - (g_executable_start + rva + 5);
        if (length < 5 || length > sizeof(replacement) || delta < INT32_MIN || delta > INT32_MAX ||
            n >= std::size(g_activation_patches))
            return false;
        const int32_t relative = static_cast<int32_t>(delta);
        memcpy(replacement + 1, &relative, 4);
        memset(replacement + 5, 0x90, length - 5);
        DefinePatch(g_activation_patches[n++], g_executable_start + rva, expected, replacement, length);
        return true;
    };
    auto marker_write = [&](LONG value) {
        emit8(0x48); emit8(0xb8); emit64(reinterpret_cast<uint64_t>(&g_background_playback_active));
        emit8(0xc7); emit8(0x00); emit32(static_cast<uint32_t>(value));
    };
    auto state_write = [&](LONG value) {
        emit8(0x48); emit8(0xb8); emit64(base + 0x1da3008);
        emit8(0xc7); emit8(0x00); emit32(static_cast<uint32_t>(value));
    };

    const BYTE automatic_expected[] = {0xb9, 0x05, 0, 0, 0};
    BYTE *automatic = cursor;
    emit8(0x48); emit8(0xb8); emit64(base + 0x1d911cc);
    emit8(0x83); emit8(0x38); emit8(0x00);
    BYTE *not_ordinary = cursor; emit8(0x75); emit8(0);
    marker_write(1);
    emit8(0x48); emit8(0xb8); emit64(base + 0x1da3004);
    emit8(0x83); emit8(0x38); emit8(0x00);
    BYTE *inactive = cursor; emit8(0x74); emit8(0);
    absolute_jump(g_executable_start + 0x7c90f);
    BYTE *common = cursor;
    absolute_jump(g_executable_start + 0x7c87a);
    inactive[1] = static_cast<BYTE>(common - (inactive + 2));
    BYTE *stock = cursor;
    marker_write(0);
    emit8(0xb9); emit32(5);
    absolute_jump(g_executable_start + 0x7c982);
    not_ordinary[1] = static_cast<BYTE>(stock - (not_ordinary + 2));
    if (!define_jump(0x7c97d, automatic_expected, sizeof(automatic_expected), automatic))
        return false;

    auto recovery_gate = [&](DWORD rva, DWORD continuation, const BYTE (&expected)[10]) {
        BYTE *thunk = cursor;
        emit8(0x48); emit8(0xb8); emit64(reinterpret_cast<uint64_t>(&g_background_playback_active));
        emit8(0x83); emit8(0x38); emit8(0x00);
        BYTE *normal_branch = cursor; emit8(0x74); emit8(0);
        absolute_jump(g_executable_start + continuation);
        BYTE *normal = cursor;
        state_write(0);
        absolute_jump(g_executable_start + continuation);
        normal_branch[1] = static_cast<BYTE>(normal - (normal_branch + 2));
        return define_jump(rva, expected, sizeof(expected), thunk);
    };
    const BYTE transition_expected[] = {0xc7, 0x05, 0xf1, 0x67, 0xd2, 0x01, 0, 0, 0, 0};
    const BYTE voice_expected[] = {0xc7, 0x05, 0x03, 0x63, 0xd2, 0x01, 0, 0, 0, 0};
    if (!recovery_gate(0x7c80d, 0x7c817, transition_expected) ||
        !recovery_gate(0x7ccfb, 0x7cd05, voice_expected))
        return false;

    auto destructive_state = [&](DWORD rva, LONG value, const BYTE (&expected)[10]) {
        BYTE *thunk = cursor;
        marker_write(0);
        state_write(value);
        absolute_jump(g_executable_start + rva + sizeof(expected));
        return define_jump(rva, expected, sizeof(expected), thunk);
    };
    const BYTE manual_expected[] = {0xc7, 0x05, 0xc5, 0x6c, 0xd2, 0x01, 4, 0, 0, 0};
    const BYTE title_expected[] = {0xc7, 0x05, 0x5f, 0x68, 0xd2, 0x01, 0, 0, 0, 0};
    const BYTE death_expected[] = {0xc7, 0x05, 0xdf, 0x66, 0xd2, 0x01, 0, 0, 0, 0};
    if (!destructive_state(0x7c339, 4, manual_expected) || !destructive_state(0x7c79f, 0, title_expected) ||
        !destructive_state(0x7c91f, 0, death_expected))
        return false;

    const BYTE reset_expected[] = {0x48, 0x83, 0xec, 0x28, 0x8b, 0x05, 0x9e, 0x68, 0xd2, 0x01};
    BYTE *reset = cursor;
    emit8(0x48); emit8(0xb8); emit64(base + 0x1da3008);
    emit8(0x83); emit8(0x38); emit8(0x05);
    BYTE *preserve = cursor; emit8(0x74); emit8(0);
    marker_write(0);
    BYTE *resume_reset = cursor;
    emit8(0x48); emit8(0x83); emit8(0xec); emit8(0x28);
    emit8(0x48); emit8(0xb8); emit64(base + 0x1da3008);
    emit8(0x8b); emit8(0x00);
    absolute_jump(g_executable_start + 0x7c76a);
    preserve[1] = static_cast<BYTE>(resume_reset - (preserve + 2));
    if (!define_jump(0x7c760, reset_expected, sizeof(reset_expected), reset))
        return false;

    DWORD old = 0;
    return VirtualProtect(g_background_playback_thunks, 0x1000, PAGE_EXECUTE_READ, &old) != FALSE;
}

bool ActivateNativePatchSet(BYTE *expanded, DWORD total_track_count)
{
    if (g_activation_phase.load() != ActivationPhase::ConstructionBlocked ||
        InterlockedCompareExchange(&g_catalog_gate_waiters, 0, 0) == 0 || g_activation_patch_count != 0)
    {
        Log("ERROR activation rejected: constructor not proven blocked");
        return false;
    }
    const bool appended = total_track_count > kStockTrackCount;
    bool podcast = false;
    for (const auto &track : g_custom_runtime)
        podcast |= track->podcast;
    const bool stock_filter =
        std::find(g_stock_disabled.begin(), g_stock_disabled.end(), BYTE{1}) != g_stock_disabled.end();
    if (!appended && !stock_filter && !g_background_playback_enabled)
    {
        return true;
    }
    BYTE *thunk = (podcast || stock_filter) ? PreparePodcastThunks() : nullptr;
    if ((podcast || stock_filter) && !thunk)
    {
        Log("ERROR preactivation thunk allocation failed; feature set inactive");
        return false;
    }
    g_activation_patch_count = 0;
    SIZE_T n = 0;
    if (!AddBackgroundPlaybackPatches(n))
    {
        Log("ERROR background playback patch preparation failed");
        return false;
    }
    if (!AddExpansionStoragePatches(n))
    {
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        Log("ERROR expansion relocation patch preparation failed");
        return false;
    }
    auto add_call = [&](DWORD rva, BYTE *target) {
        BYTE old[5] = {0xe8}, replacement[5] = {0xe8};
        const intptr_t old_delta = (g_executable_start + 0x7b8b0) - (g_executable_start + rva + 5);
        const intptr_t new_delta = target - (g_executable_start + rva + 5);
        if (old_delta < INT32_MIN || old_delta > INT32_MAX || new_delta < INT32_MIN || new_delta > INT32_MAX)
            return false;
        const int32_t o = static_cast<int32_t>(old_delta), v = static_cast<int32_t>(new_delta);
        memcpy(old + 1, &o, 4);
        memcpy(replacement + 1, &v, 4);
        DefinePatch(g_activation_patches[n++], g_executable_start + rva, old, replacement, 5);
        return true;
    };
    if (podcast)
    {
        const BYTE menu_old[] = {0x48, 0x83, 0xfe, 0x03}, menu_new[] = {0x48, 0x83, 0xfe, 0x04};
        DefinePatch(g_activation_patches[n++], g_executable_start + 0xd06323, menu_old, menu_new, 4);
        const BYTE nav_old[] = {0xb8, 0x02, 0, 0, 0}, nav_new[] = {0xb8, 0x03, 0, 0, 0};
        DefinePatch(g_activation_patches[n++], g_executable_start + 0xd028ba, nav_old, nav_new, 5);
        BYTE *call_target = stock_filter ? thunk + 320 : thunk;
        if (!add_call(0xd07715, call_target))
        {
            VirtualFree(thunk, 0, MEM_RELEASE);
            return false;
        }
        const BYTE traversal_old[] = {0x85, 0xc0, 0x75, 0x7c, 0x0f, 0xbf, 0x0d, 0xdd, 0x76, 0x06, 0x01};
        BYTE traversal_new[11] = {0xe9};
        const intptr_t delta = (thunk + 64) - (g_executable_start + 0xd0771f);
        if (delta < INT32_MIN || delta > INT32_MAX)
        {
            VirtualFree(thunk, 0, MEM_RELEASE);
            return false;
        }
        const int32_t relative = static_cast<int32_t>(delta);
        memcpy(traversal_new + 1, &relative, 4);
        memset(traversal_new + 5, 0x90, 6);
        DefinePatch(g_activation_patches[n++], g_executable_start + 0xd0771a, traversal_old, traversal_new, 11);
    }
    if (stock_filter)
    {
        BYTE selection_old[5] = {0xe8}, selection_new[5] = {0xe8};
        const intptr_t selection_old_delta = (g_executable_start + 0x75c20) - (g_executable_start + 0x7cca9 + 5);
        const intptr_t selection_new_delta = (thunk + 384) - (g_executable_start + 0x7cca9 + 5);
        if (selection_new_delta < INT32_MIN || selection_new_delta > INT32_MAX)
        {
            VirtualFree(thunk, 0, MEM_RELEASE);
            return false;
        }
        const int32_t selection_old_relative = static_cast<int32_t>(selection_old_delta);
        const int32_t selection_new_relative = static_cast<int32_t>(selection_new_delta);
        memcpy(selection_old + 1, &selection_old_relative, 4);
        memcpy(selection_new + 1, &selection_new_relative, 4);
        DefinePatch(g_activation_patches[n++], g_executable_start + 0x7cca9, selection_old, selection_new, 5);

        constexpr DWORD sites[] = {0xd02355, 0xd051cf, 0xd052e6, 0xd07427, 0xd075b2, 0xd07715, 0xd07852, 0xd07963,
                                   0xd07ad3, 0xd07c53, 0xd07e22, 0xd09a69, 0xd09acb, 0xd09afa, 0xd0a027};
        for (DWORD site : sites)
        {
            if (podcast && site == 0xd07715)
                continue;
            if (!add_call(site, thunk + 256))
            {
                VirtualFree(thunk, 0, MEM_RELEASE);
                return false;
            }
        }
    }
    if (appended)
    {
        const BYTE lea_old[] = {0x48, 0x8d, 0x0d, 0xb6, 0x9c, 0xa8, 0x01};
        BYTE lea_new[7] = {0x48, 0x8d, 0x0d};
        const intptr_t lea_delta = expanded - (g_executable_start + 0x7b9ca);
        if (lea_delta < INT32_MIN || lea_delta > INT32_MAX)
        {
            if (thunk)
                VirtualFree(thunk, 0, MEM_RELEASE);
            return false;
        }
        const int32_t lea_relative = static_cast<int32_t>(lea_delta);
        memcpy(lea_new + 3, &lea_relative, 4);
        DefinePatch(g_activation_patches[n++], g_executable_start + 0x7b9c3, lea_old, lea_new, 7);
        const BYTE count_old[] = {0xb8, 0x49, 0, 0, 0};
        BYTE count_new[] = {0xb8, 0, 0, 0, 0};
        memcpy(count_new + 1, &total_track_count, 4);
        DefinePatch(g_activation_patches[n++], g_executable_start + 0x7b9ca, count_old, count_new, 5);
    }
    if (!PrepareBankPublicationHook(g_activation_patches[n]))
    {
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        return false;
    }
    ++n;
    if (!PrepareProviderHooks(g_activation_patches + n))
    {
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        return false;
    }
    n += 11;
    if (!PrepareIdentityHandoffHook(g_activation_patches[n]))
    {
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        return false;
    }
    ++n;
    if (!PrepareEventStringLookupHook(g_activation_patches[n]))
    {
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        return false;
    }
    ++n;
    if (!PrepareBankLoadQueueHook(g_activation_patches[n]))
    {
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        return false;
    }
    ++n;
    if (!PrepareAudioManagerCapture(g_activation_patches[n]))
    {
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        return false;
    }
    ++n;
    if (!PrepareCustomIdentityHooks(g_activation_patches[n], g_activation_patches[n + 1]))
    {
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        return false;
    }
    n += 2;
    if (!PrepareProgrammerDurationHook(g_activation_patches[n]))
    {
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        return false;
    }
    ++n;
    for (SIZE_T i = 0; i < n; ++i)
    {
        const auto &candidate = g_activation_patches[i];
        if (!ipod::patching::Accessible(candidate.address, candidate.length) ||
            memcmp(candidate.address, candidate.expected, candidate.length) != 0)
        {
            char line[256]{};
            std::snprintf(line, sizeof(line),
                          "CATALOG_EXPANSION PATCH_MISMATCH index=%zu rva=0x%llX length=%zu actual=%02X%02X%02X%02X expected=%02X%02X%02X%02X",
                          i, static_cast<unsigned long long>(candidate.address - g_executable_start), candidate.length,
                          candidate.address[0], candidate.length > 1 ? candidate.address[1] : 0,
                          candidate.length > 2 ? candidate.address[2] : 0,
                          candidate.length > 3 ? candidate.address[3] : 0, candidate.expected[0],
                          candidate.length > 1 ? candidate.expected[1] : 0,
                          candidate.length > 2 ? candidate.expected[2] : 0,
                          candidate.length > 3 ? candidate.expected[3] : 0);
            Log(line);
            break;
        }
    }
    const auto result = ipod::patching::ApplyCheckedPatches(g_activation_patches, n);
    if (result == ipod::patching::Result::RecoveryFailed)
    {
        g_activation_patch_count = n;
        g_activation_phase.store(ActivationPhase::PatchStateUncertain);
        Log("CRITICAL activation recovery failed; constructor blocked and referenced allocations retained");
        return false;
    }
    if (result != ipod::patching::Result::Applied)
    {
        Log("ERROR checked activation rejected/rolled back; feature set inactive");
        if (thunk)
            VirtualFree(thunk, 0, MEM_RELEASE);
        return false;
    }
    g_activation_patch_count = n;
    char message[160]{};
    std::snprintf(message, sizeof(message), "checked native activation PREPARED as one %zu-patch batch; constructor blocked", n);
    Log(message);
    return true;
}

DWORD WINAPI InitializeRuntimeThread(void *) try
{
    HMODULE executable = GetModuleHandleW(nullptr);
    g_executable_start = reinterpret_cast<BYTE *>(executable);
    g_selected_ipod_index = reinterpret_cast<volatile LONG *>(g_executable_start + 0x1d89908);
    if (!ValidateExecutableProfile())
    {
        AbortPreConstructionActivation();
        return 1;
    }
    g_background_playback_enabled = LoadBackgroundPlaybackPreference();
    Log(g_background_playback_enabled ? "background playback enabled" : "background playback disabled");

    BYTE *constructor_entry = g_executable_start + 0x7b940;
    bool constructor_unpacked = false;
    for (int attempt = 0; attempt < 2000; ++attempt)
    {
        const SIZE_T prefix = constructor_entry[0] == 0x40 ? 1 : 0;
        const BYTE instruction_shape[] = {0x55, 0x48, 0x83, 0xec, 0x50, 0x80, 0x3d};
        if (memcmp(constructor_entry + prefix, instruction_shape, sizeof(instruction_shape)) == 0 &&
            constructor_entry[11 + prefix] == 0)
        {
            constructor_unpacked = true;
            break;
        }
        Sleep(5);
    }
    if (!constructor_unpacked)
    {
        Log("ERROR timed out waiting for catalog constructor");
        AbortPreConstructionActivation();
        return 1;
    }
    g_catalog_ready_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_catalog_ready_event || !InstallCatalogConstructorGate())
    {
        Log("ERROR catalog constructor gate unavailable");
        AbortPreConstructionActivation();
        return 1;
    }
    struct PreConstructionAbort
    {
        ~PreConstructionAbort()
        {
            AbortPreConstructionActivation();
        }
    } abort_preconstruction;

    if (!LoadDeployment())
        return 1;
    const DWORD total_track_count = kStockTrackCount + static_cast<DWORD>(g_custom_runtime.size());
    if (total_track_count > kNativeCatalogCapacity)
    {
        Log("ERROR deployment capacity arithmetic rejected; feature set inactive");
        return 11;
    }
    for (const auto &track : g_custom_runtime)
        if (track->catalog_index < kStockTrackCount || track->catalog_index >= total_track_count)
        {
            Log("ERROR runtime record target outside configured catalog; activation aborted before construction");
            return 12;
        }

    // The selected PC catalog path uses table 3. Wait until the protected
    // executable has unpacked this function, then redirect its table pointer
    // to a nearby expanded copy and change its immediate loop bound.
    BYTE *table3_lea = g_executable_start + 0x7b9c3;
    BYTE *table3_count = g_executable_start + 0x7b9ca;
    const BYTE expected_lea[] = {0x48, 0x8d, 0x0d, 0xb6, 0x9c, 0xa8, 0x01};
    const BYTE expected_count[] = {0xb8, 0x49, 0x00, 0x00, 0x00};
    bool unpacked = false;
    for (int attempt = 0; attempt < 2000; ++attempt)
    {
        if (memcmp(table3_lea, expected_lea, sizeof(expected_lea)) == 0 &&
            memcmp(table3_count, expected_count, sizeof(expected_count)) == 0)
        {
            unpacked = true;
            break;
        }
        Sleep(5);
    }
    if (!unpacked)
    {
        Log("ERROR catalog injector timed out waiting for unpacked function");
        return 1;
    }
    if (!ValidatePodcastPatchSet())
        return 10;
    // Observing a waiter proves this invocation has not executed the displaced constructor prologue.
    for (int attempt = 0; attempt < 2000 && InterlockedCompareExchange(&g_catalog_gate_waiters, 0, 0) == 0; ++attempt)
        Sleep(5);
    volatile int *constructed_count = reinterpret_cast<volatile int *>(g_executable_start + 0x1da2de0);
    if (InterlockedCompareExchange(&g_catalog_gate_waiters, 0, 0) == 0 || *constructed_count != 0)
    {
        Log("ERROR constructor blocking precondition unavailable or construction already observed; activation aborted");
        return 1;
    }
    g_activation_phase.store(ActivationPhase::ConstructionBlocked);
    if (!AllocateExpansionStorage())
    {
        Log("ERROR catalog expansion storage allocation failed");
        return 13;
    }

    BYTE *expanded = nullptr;
    const SIZE_T expanded_size = total_track_count * kCatalogEntrySize;
    const uintptr_t module_base = reinterpret_cast<uintptr_t>(g_executable_start);
    for (uintptr_t delta = 0x30000000; delta <= 0x70000000 && !expanded; delta += 0x10000)
    {
        expanded = reinterpret_cast<BYTE *>(VirtualAlloc(reinterpret_cast<void *>(module_base + delta), expanded_size,
                                                         MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    }
    if (!expanded)
    {
        Log("ERROR catalog injector could not allocate a near table");
        return 2;
    }

    BYTE *original_table3 = g_executable_start + 0x1b05680;
    memcpy(expanded, original_table3, kStockTrackCount * kCatalogEntrySize);
    for (const auto &track : g_custom_runtime)
    {
        BYTE *entry = expanded + track->catalog_index * kCatalogEntrySize;
        memset(entry, 0, kCatalogEntrySize);
        memcpy(entry, track->runtime_id.data(), track->runtime_id.size());
    }

    const intptr_t displacement = expanded - (table3_lea + 7);
    if (displacement < (std::numeric_limits<int32_t>::min)() || displacement > (std::numeric_limits<int32_t>::max)())
    {
        Log("ERROR catalog injector near allocation is outside rel32 range");
        VirtualFree(expanded, 0, MEM_RELEASE);
        return 3;
    }
    if (!ActivateNativePatchSet(expanded, total_track_count))
    {
        if (g_activation_phase.load() != ActivationPhase::PatchStateUncertain)
            VirtualFree(expanded, 0, MEM_RELEASE);
        return 4;
    }
    g_activation_phase.store(ActivationPhase::PatchedButConstructionBlocked);
    if (!CommitCatalogConstruction())
        return 14;
    if (!g_custom_runtime.empty())
    {
        char injection_message[256]{};
        std::snprintf(injection_message, sizeof(injection_message),
                      "catalog injection INSTALLED table=%p mod_entries=%zu count=%lu", expanded,
                      g_custom_runtime.size(), total_track_count);
        Log(injection_message);
    }
    BYTE *records = g_expanded_runtime_records;
    const ULONGLONG construction_started = GetTickCount64();
    for (int attempt = 0; attempt < 24000 && *constructed_count != static_cast<int>(total_track_count); ++attempt)
    {
        Sleep(5);
    }
    if (*constructed_count != static_cast<int>(total_track_count))
    {
        g_activation_phase.store(ActivationPhase::FailedAfterCommit);
        Log("ERROR configured runtime construction timed out after commit; expansion/hooks retained, record finalization skipped; restart MGS4");
        return 5;
    }
    g_activation_phase.store(ActivationPhase::ConstructionComplete);
    {
        char line[160]{};
        std::snprintf(line, sizeof(line), "catalog construction complete count=%d elapsed_ms=%llu", *constructed_count,
                      static_cast<unsigned long long>(GetTickCount64() - construction_started));
        Log(line);
    }
    for (const auto &track : g_custom_runtime)
    {
        BYTE *injected_record = records + track->catalog_index * kRuntimeRecordSize;
        auto type_and_unlock_flags = reinterpret_cast<unsigned long long *>(injected_record + 0x230);
        const unsigned long long original_flags = *type_and_unlock_flags;
        // Bit 0 participates in native flagged accounting; bit 2 selects Podcasts.
        // Preserve all other runtime bits while publishing the manifest classification.
        constexpr unsigned long long kCategoryFlags = 0x5;
        *type_and_unlock_flags = (original_flags & ~kCategoryFlags) | (track->podcast ? 0x5 : 0x1);
        memset(injected_record, 0, 32);
        memcpy(injected_record, track->runtime_id.data(), track->runtime_id.size());
    }
    g_activation_phase.store(ActivationPhase::RuntimeActive);
    return 0;
}
catch (const std::exception &error)
{
    Log("ERROR native activation exception");
    Log(error.what());
    if (CanRollbackActivation(g_activation_phase.load()) ||
        g_activation_phase.load() == ActivationPhase::PatchStateUncertain)
        AbortPreConstructionActivation();
    else
    {
        g_activation_phase.store(ActivationPhase::FailedAfterCommit);
        Log("ERROR post-commit initialization failed; expansion/hooks retained; restart MGS4");
    }
    return 15;
}
} // namespace

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        wchar_t module_path[MAX_PATH]{};
        GetModuleFileNameW(module, module_path, MAX_PATH);
        wchar_t *slash = wcsrchr(module_path, L'\\');
        if (slash)
            *(slash + 1) = L'\0';
        // Ultimate ASI Loader may load the plugin from scripts, plugins, or
        // update. Configuration and game assets remain relative to MGS4 root.
        size_t length = wcslen(module_path);
        if (length && module_path[length - 1] == L'\\')
            module_path[--length] = L'\0';
        wchar_t *folder = wcsrchr(module_path, L'\\');
        if (folder && (_wcsicmp(folder + 1, L"scripts") == 0 || _wcsicmp(folder + 1, L"plugins") == 0 ||
                       _wcsicmp(folder + 1, L"update") == 0))
        {
            *(folder + 1) = L'\0';
        }
        else if (length)
        {
            module_path[length] = L'\\';
            module_path[length + 1] = L'\0';
        }
        wcscpy_s(g_game_dir, module_path);
        swprintf_s(g_log_path, L"%siPodManager.log", module_path);
        Log("iPodManager attached");
        HANDLE thread = CreateThread(nullptr, 0, InitializeRuntimeThread, nullptr, 0, nullptr);
        if (thread)
            CloseHandle(thread);
        else
            Log("ERROR creating runtime initialization thread");
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        Log("iPodManager detached");
    }
    return TRUE;
}
