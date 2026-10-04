#include "../mgs4_ipod_asi.cpp"
#include <cassert>
#include <iostream>

int main()
{
    // Fake patch bytes and real Win32 events only. No MGS4 image or game constructor is executed.
    BYTE *page = static_cast<BYTE *>(VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    assert(page);
    g_catalog_ready_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    assert(g_catalog_ready_event);
    const BYTE original = 0x41, replacement = 0x42;
    auto reset = [&]() {
        assert(ResetEvent(g_catalog_ready_event));
        page[0] = original;
        g_activation_phase.store(ActivationPhase::PreActivation);
        g_activation_patch_count = 0;
    };
    auto patch = [&]() {
        DefinePatch(g_activation_patches[0], page, &original, &replacement, 1);
        assert(ipod::patching::ApplyCheckedPatches(g_activation_patches, 1) == ipod::patching::Result::Applied);
        g_activation_patch_count = 1;
        g_activation_phase.store(ActivationPhase::PatchedButConstructionBlocked);
    };
    reset();
    assert(!ActivateNativePatchSet(page, kNativeCatalogCapacity));
    g_activation_phase.store(ActivationPhase::ConstructionBlocked);
    assert(!ActivateNativePatchSet(page, kNativeCatalogCapacity));
    assert(page[0] == original && g_activation_patch_count == 0);
    std::cout << "PASS expansion cannot activate without an observed blocked constructor\n";
    ipod::patching::Patch invalid{};
    const BYTE wrong = 0x43;
    DefinePatch(invalid, page, &wrong, &replacement, 1);
    assert(ipod::patching::ApplyCheckedPatches(&invalid, 1) == ipod::patching::Result::Rejected);
    AbortPreConstructionActivation();
    assert(page[0] == original && g_activation_phase.load() == ActivationPhase::FailedBeforeCommit);
    assert(WaitForSingleObject(g_catalog_ready_event, 0) == WAIT_OBJECT_0);
    std::cout << "PASS rejected patch/precondition leaves original storage and releases only stock construction\n";

    reset();
    HANDLE event = g_catalog_ready_event;
    g_catalog_ready_event = nullptr; // constructor-unpack timeout: no gate/patch has been installed
    AbortPreConstructionActivation();
    assert(g_activation_phase.load() == ActivationPhase::FailedBeforeCommit && page[0] == original);
    assert(g_activation_patch_count == 0 && WaitForSingleObject(event, 0) == WAIT_TIMEOUT);
    g_catalog_ready_event = event;
    std::cout << "PASS constructor-unpack timeout cannot install or release expanded construction\n";

    reset();
    std::thread constructor([&]() { assert(WaitForCatalogDecision(event, 0) == WAIT_OBJECT_0); });
    for (int i = 0; i < 1000 && InterlockedCompareExchange(&g_catalog_gate_waiters, 0, 0) == 0; ++i) Sleep(1);
    assert(InterlockedCompareExchange(&g_catalog_gate_waiters, 0, 0) == 1);
    assert(!CommitCatalogConstruction());
    assert(WaitForSingleObject(event, 0) == WAIT_TIMEOUT);
    Sleep(10);
    assert(InterlockedCompareExchange(&g_catalog_gate_waiters, 0, 0) == 1);
    AbortPreConstructionActivation();
    constructor.join();
    assert(page[0] == original);
    std::cout << "PASS unreleased gate does not bypass the failed precondition or caller timeout\n";

    reset();
    patch();
    assert(RollbackActivation());
    assert(page[0] == original && g_activation_patch_count == 0);
    AbortPreConstructionActivation();
    std::cout << "PASS applied patch bytes restored before constructor release\n";

    reset();
    // Exercise all eight existing buffers at capacity, retaining their native alignment and guard pages.
    g_executable_start = page;
    assert(AllocateExpansionStorage());
    BYTE *allocation = g_expansion_allocation;
    static_assert(kNativeCatalogCapacity == 1024 && kStockTrackCount == 73);
    static_assert(sizeof(kExpansionReferences) / sizeof(kExpansionReferences[0]) == 104);
    g_expanded_runtime_records[1023 * kRuntimeRecordSize + kRuntimeRecordSize - 1] = 0x55;
    g_expanded_playback_order[1023] = 1023;
    for (int i = 0; i < 4; ++i) g_expanded_ui_orders[i * 1024 + 1023] = 1023;
    g_expanded_ui_records[1024 * kUiRecordSize - 1] = 0x55;
    g_expanded_sorting_workspace[1024 * kUiRecordSize - 1] = 0x55;
    ClearExpandedMenuLists();
    for (int i = 0; i < 4; ++i) assert(g_expanded_ui_orders[i * 1024 + 1023] == 0);
    std::cout << "PASS eight 1024-capacity buffers and existing relocation reference table unchanged\n";

    patch();
    assert(CommitCatalogConstruction());
    assert(g_activation_phase.load() == ActivationPhase::ConstructionStarted);
    assert(WaitForSingleObject(event, 0) == WAIT_OBJECT_0);
    for (ActivationPhase phase : {ActivationPhase::ConstructionStarted, ActivationPhase::ConstructionComplete,
                                  ActivationPhase::RuntimeActive, ActivationPhase::FailedAfterCommit})
    {
        g_activation_phase.store(phase);
        assert(!CanRollbackActivation(phase) && !RollbackActivation());
        AbortPreConstructionActivation();
        assert(page[0] == replacement && g_activation_patch_count == 1 && g_expansion_allocation == allocation);
        assert(ipod::patching::Accessible(allocation, kNativeCatalogCapacity * kRuntimeRecordSize));
    }
    assert(g_expanded_runtime_records[1024 * kRuntimeRecordSize - 1] == 0x55);
    std::cout << "PASS construction timeout/later failure/success states cannot rollback or free committed storage\n";

    reset();
    patch();
    g_catalog_ready_event = nullptr;
    assert(!CommitCatalogConstruction());
    assert(g_activation_phase.load() == ActivationPhase::FailedAfterCommit);
    assert(!RollbackActivation() && page[0] == replacement);
    g_catalog_ready_event = event;
    std::cout << "PASS signal failure remains conservatively committed\n";

    reset();
    patch();
    g_activation_phase.store(ActivationPhase::PatchStateUncertain);
    AbortPreConstructionActivation();
    AbortPreConstructionActivation();
    assert(!RollbackActivation() && page[0] == replacement);
    assert(g_activation_patch_count == 1 && WaitForSingleObject(event, 0) == WAIT_TIMEOUT);
    assert(g_expansion_allocation == allocation && ipod::patching::Accessible(allocation, 1));
    std::cout << "PASS uncertain patch recovery retains references/buffers and does not open the gate or retry rollback\n";

    // Test-owned allocations/events are released only after every simulated consumer has stopped.
    assert(CloseHandle(event));
    assert(VirtualFree(allocation, 0, MEM_RELEASE));
    assert(VirtualFree(page, 0, MEM_RELEASE));
    std::cout << "native activation tests passed\n";
}
