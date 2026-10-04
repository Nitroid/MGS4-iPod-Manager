#pragma once

namespace ipod::background
{
inline bool SuppressOrdinaryStop(bool enabled, int discriminator) { return enabled && discriminator == 0; }
inline bool AllowRecovery(bool enabled, bool marker_active) { return enabled && marker_active; }
inline unsigned ControllerContinuation(int controller_flag) { return controller_flag != 0 ? 0x7c90f : 0x7c87a; }
inline bool PreserveMarkerOnFullReset(int playback_state) { return playback_state == 5; }
}
