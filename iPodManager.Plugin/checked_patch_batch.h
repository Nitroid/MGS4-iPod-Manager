#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <windows.h>

// Caller must establish that no game thread can execute/read affected locations
// for the whole operation. Byte rollback alone does not make live patching atomic.
namespace ipod::patching
{
struct Patch
{
    BYTE *address;
    SIZE_T length;
    BYTE expected[32];
    BYTE replacement[32];
};
enum class Result
{
    Applied,
    Rejected,
    RolledBack,
    RecoveryFailed
};

inline bool Accessible(const void *pointer, SIZE_T size)
{
    const uintptr_t start = reinterpret_cast<uintptr_t>(pointer);
    if (!start || !size || size > UINTPTR_MAX - start)
        return false;
    uintptr_t cursor = start;
    while (cursor < start + size)
    {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQuery(reinterpret_cast<void *>(cursor), &info, sizeof(info)) || info.State != MEM_COMMIT ||
            (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)))
            return false;
        const DWORD access = info.Protect & 0xff;
        if (access != PAGE_READONLY && access != PAGE_READWRITE && access != PAGE_WRITECOPY &&
            access != PAGE_EXECUTE_READ && access != PAGE_EXECUTE_READWRITE && access != PAGE_EXECUTE_WRITECOPY)
            return false;
        const uintptr_t base = reinterpret_cast<uintptr_t>(info.BaseAddress);
        if (info.RegionSize > UINTPTR_MAX - base || base + info.RegionSize <= cursor)
            return false;
        cursor = base + info.RegionSize;
    }
    return true;
}

inline Result ApplyCheckedPatches(Patch *patches, SIZE_T count)
{
    constexpr SIZE_T maximum = 320;
    if (!patches || !count || count > maximum)
        return Result::Rejected;
    // No allocation or code mutation occurs until EVERY expectation matches.
    for (SIZE_T i = 0; i < count; ++i)
    {
        const auto &p = patches[i];
        if (!p.length || p.length > sizeof(p.expected) || !Accessible(p.address, p.length))
            return Result::Rejected;
        // Keep each protection operation on one page so its original protection
        // can be restored exactly. Shared pages are restored in reverse order.
        SYSTEM_INFO system{};
        GetSystemInfo(&system);
        const uintptr_t address = reinterpret_cast<uintptr_t>(p.address);
        if (address / system.dwPageSize != (address + p.length - 1) / system.dwPageSize)
            return Result::Rejected;
        for (SIZE_T j = 0; j < i; ++j)
        {
            const uintptr_t other = reinterpret_cast<uintptr_t>(patches[j].address);
            if (address < other + patches[j].length && other < address + p.length)
                return Result::Rejected;
        }
        if (memcmp(p.address, p.expected, p.length))
            return Result::Rejected;
    }
    DWORD protection[maximum]{};
    SIZE_T prepared = 0;
    auto restore_protection = [&]() {
        bool ok = true;
        while (prepared)
        {
            const SIZE_T i = --prepared;
            DWORD ignored = 0;
            if (!VirtualProtect(patches[i].address, patches[i].length, protection[i], &ignored))
                ok = false;
        }
        return ok;
    };
    for (; prepared < count; ++prepared)
    {
        const auto &p = patches[prepared];
        if (!VirtualProtect(p.address, p.length, PAGE_EXECUTE_READWRITE, &protection[prepared]))
        {
            return restore_protection() ? Result::Rejected : Result::RecoveryFailed;
        }
    }
    // Recheck at the actual activation boundary.
    for (SIZE_T i = 0; i < count; ++i)
    {
        if (memcmp(patches[i].address, patches[i].expected, patches[i].length))
            return restore_protection() ? Result::Rejected : Result::RecoveryFailed;
    }
    SIZE_T written = 0;
    bool failed = false;
    for (; written < count;)
    {
        auto &p = patches[written];
        memcpy(p.address, p.replacement, p.length);
        ++written;
        if (!FlushInstructionCache(GetCurrentProcess(), p.address, p.length))
        {
            failed = true;
            break;
        }
    }
    if (failed)
    {
        bool restored = true;
        while (written)
        {
            auto &p = patches[--written];
            memcpy(p.address, p.expected, p.length);
            if (!FlushInstructionCache(GetCurrentProcess(), p.address, p.length))
                restored = false;
        }
        const bool protections_restored = restore_protection();
        return restored && protections_restored ? Result::RolledBack : Result::RecoveryFailed;
    }
    // RecoveryFailed is deliberately distinct: caller may not report the mod
    // inactive or free referenced thunks after a protection restoration failure.
    return restore_protection() ? Result::Applied : Result::RecoveryFailed;
}
} // namespace ipod::patching
