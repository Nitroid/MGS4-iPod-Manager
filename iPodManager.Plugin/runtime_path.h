#pragma once

#include <windows.h>
#include <string>
#include <vector>

namespace ipod::runtime
{
inline bool Utf8ToWide(const std::string &value, std::wstring &wide)
{
    const int required = MultiByteToWideChar(
        CP_UTF8, MB_ERR_INVALID_CHARS, value.c_str(), -1, nullptr, 0);
    if (required <= 0)
        return false;
    std::vector<wchar_t> buffer(static_cast<std::size_t>(required));
    if (!MultiByteToWideChar(
            CP_UTF8, MB_ERR_INVALID_CHARS, value.c_str(), -1, buffer.data(), required))
        return false;
    wide.assign(buffer.data(), static_cast<std::size_t>(required - 1));
    return true;
}

inline bool WideToUtf8(const std::wstring &wide, std::string &value)
{
    const int required = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, wide.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (required <= 0)
        return false;
    std::vector<char> buffer(static_cast<std::size_t>(required));
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.c_str(), -1,
                             buffer.data(), required, nullptr, nullptr))
        return false;
    value.assign(buffer.data(), static_cast<std::size_t>(required - 1));
    return true;
}

inline bool BuildPlaybackSourcePath(const wchar_t *game_directory,
                                    const std::string &relative_source_path,
                                    std::string &playback_path,
                                    std::size_t *full_path_length = nullptr,
                                    bool *used_extended_path = nullptr)
{
    std::wstring relative;
    if (!game_directory || !Utf8ToWide(relative_source_path, relative))
        return false;
    for (wchar_t &character : relative)
        if (character == L'/')
            character = L'\\';

    std::wstring full = game_directory;
    full += L"iPod\\content\\";
    full += relative;
    if (full_path_length)
        *full_path_length = full.size();

    std::wstring resolved = full;
    bool extended = false;
    if (full.size() >= MAX_PATH)
    {
        resolved.insert(0, L"\\\\?\\");
        extended = true;
    }
    if (used_extended_path)
        *used_extended_path = extended;
    return WideToUtf8(resolved, playback_path);
}
} // namespace ipod::runtime
