#pragma once

#include <cstdint>

namespace ipod::programmer
{
constexpr uint32_t kFmodTimeunitMilliseconds = 0x00000001;

inline bool IsValidResumePosition(bool stream_was_created, int64_t saved_position_ms, uint64_t duration_seconds)
{
    if (!stream_was_created || saved_position_ms <= 0 || duration_seconds > UINT64_MAX / 1000)
        return false;

    return static_cast<uint64_t>(saved_position_ms) < duration_seconds * 1000;
}
}
