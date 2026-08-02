namespace TarkovHelper.Models;

/// <summary>
/// Stored profile discriminator. Runtime behavior is PVP-only; the legacy PVE
/// value remains solely so existing user databases can still be read safely.
/// </summary>
public enum ProfileType
{
    /// <summary>Only runtime profile supported by Tarkov Helper.</summary>
    Pvp = 0,

    /// <summary>Legacy persistence value; never selected by the application.</summary>
    Pve = 1
}
