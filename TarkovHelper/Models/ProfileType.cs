namespace TarkovHelper.Models;

/// <summary>
/// Stored profile discriminator. The application executes PVP only; PVE is
/// retained as a legacy database value so old user data remains readable.
/// </summary>
public enum ProfileType
{
    Pvp = 0,
    Pve = 1
}
