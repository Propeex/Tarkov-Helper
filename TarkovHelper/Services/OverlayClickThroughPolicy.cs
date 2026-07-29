namespace TarkovHelper.Services;

/// <summary>
/// Prevents initialization-time checkbox events from mutating the native overlay
/// style and applies a toggle only when the requested state differs from the
/// currently persisted setting.
/// </summary>
public static class OverlayClickThroughPolicy
{
    public static bool ShouldToggle(bool isInitializing, bool currentState, bool requestedState) =>
        !isInitializing && currentState != requestedState;
}
