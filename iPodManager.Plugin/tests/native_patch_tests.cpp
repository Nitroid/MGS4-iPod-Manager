#include "../checked_patch_batch.h"
#include <cassert>
#include <cstring>
#include <iostream>

int main() {
    auto* page = static_cast<BYTE*>(VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE,
                                                 PAGE_EXECUTE_READWRITE));
    assert(page);
    std::memset(page, 0x41, 4096);
    ipod::patching::Patch patches[39]{};
    for (SIZE_T i = 0; i < 39; ++i) {
        patches[i].address = page + i * 8;
        patches[i].length = 1;
        patches[i].expected[0] = 0x41;
        patches[i].replacement[0] = 0x42;
    }
    assert(ipod::patching::ApplyCheckedPatches(patches, 39) == ipod::patching::Result::Applied);
    for (SIZE_T i = 0; i < 39; ++i) assert(page[i * 8] == 0x42);

    auto wrong = patches[0];
    wrong.expected[0] = 0x41;
    wrong.replacement[0] = 0x43;
    assert(ipod::patching::ApplyCheckedPatches(&wrong, 1) == ipod::patching::Result::Rejected);
    assert(page[0] == 0x42);
    VirtualFree(page, 0, MEM_RELEASE);
    std::cout << "native patch tests passed\n";
}
