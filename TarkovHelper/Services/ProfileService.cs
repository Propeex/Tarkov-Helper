using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// PVP-only profile facade. ProfileType.Pve remains solely so existing
/// user_data.db rows can still be read and migrated without schema damage.
/// </summary>
public sealed class ProfileService
{
    private static ProfileService? _instance;
    public static ProfileService Instance => _instance ??= new ProfileService();

    private ProfileService()
    {
    }

    public ProfileType CurrentProfile
    {
        get => ProfileType.Pvp;
        set
        {
            // Legacy callers are normalized to PVP. No profile-switch UI or PVE
            // execution path exists in the application.
            SettingsService.Instance.LastProfileType = ProfileType.Pvp;
        }
    }

    public string GetProfileName(ProfileType type) => "PVP";
}
