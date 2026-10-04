namespace iPodManager;

internal static class TransferFailureMessages
{
    internal static string For(Mgs4IpodService.TransferFailureState state) => state switch
    {
        Mgs4IpodService.TransferFailureState.BeforeMutation =>
            "Sync failed. No deployment changes were applied. Check iPodManager.log for details.",
        Mgs4IpodService.TransferFailureState.PreviousRestored =>
            "Sync failed, but the previous deployment was restored safely. Check iPodManager.log for details.",
        Mgs4IpodService.TransferFailureState.RecoveryRequired =>
            "Sync did not complete. Deployment recovery requires attention. Do not retry; check iPodManager.log.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown transfer failure state.")
    };
}
