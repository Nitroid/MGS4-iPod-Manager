#include "../background_playback.h"
#include <cassert>
#include <iostream>

int main()
{
    using namespace ipod::background;
    assert(!SuppressOrdinaryStop(false, 0));
    assert(SuppressOrdinaryStop(true, 0));
    assert(!SuppressOrdinaryStop(true, 1));
    assert(!SuppressOrdinaryStop(true, -1));
    assert(!AllowRecovery(false, true));
    assert(!AllowRecovery(true, false));
    assert(AllowRecovery(true, true));
    assert(ControllerContinuation(1) == 0x7c90f);
    assert(ControllerContinuation(0) == 0x7c87a);
    assert(PreserveMarkerOnFullReset(5));
    assert(!PreserveMarkerOnFullReset(0));
    const unsigned char automatic[] = {0xb9, 0x05, 0, 0, 0};
    const unsigned char transition[] = {0xc7, 0x05, 0xf1, 0x67, 0xd2, 0x01, 0, 0, 0, 0};
    const unsigned char voice[] = {0xc7, 0x05, 0x03, 0x63, 0xd2, 0x01, 0, 0, 0, 0};
    const unsigned char reset[] = {0x48, 0x83, 0xec, 0x28, 0x8b, 0x05, 0x9e, 0x68, 0xd2, 0x01};
    assert(sizeof(automatic) == 5 && sizeof(transition) == 10 && sizeof(voice) == 10 && sizeof(reset) == 10);
    std::cout << "background playback tests passed\n";
}
