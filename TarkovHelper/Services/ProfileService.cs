using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// PVP-only profile service.
/// ProfileType remains in the model for legacy user_data.db compatibility, but
/// the application no longer exposes or loads a PVE profile.
/// </summary>
public sealed class ProfileService
{
    private static ProfileService? _instance;
    public static ProfileService Instance => _instance ??= new ProfileService();

    private ProfileType _currentProfile = ProfileType.Pvp;

    public ProfileType CurrentProfile
    {
        get => ProfileType.Pvp;
        set
        {
            var changed = _currentProfile != ProfileType.Pvp;
            _currentProfile = ProfileType.Pvp;

            // Normalize legacy installations that last used PVE.
            UserDataDbService.Instance.SetSetting("app.lastProfileType", ProfileType.Pvp.ToString(), null);
            HideLegacyProfileSelector();

            // A legacy PVE click can still reach this setter from old code-behind.
            // Re-emit PVP so the generated radio-button state is immediately corrected.
            if (changed || value != ProfileType.Pvp)
                ProfileChanged?.Invoke(this, ProfileType.Pvp);
        }
    }

    public event EventHandler<ProfileType>? ProfileChanged;

    private ProfileService()
    {
        _instance = this;
        HideLegacyProfileSelector();
    }

    public string GetProfileName(ProfileType type) => "PVP";

    private static void HideLegacyProfileSelector()
    {
        var application = Application.Current;
        if (application?.Dispatcher == null)
            return;

        application.Dispatcher.BeginInvoke(() =>
        {
            var mainWindow = application.MainWindow
                ?? application.Windows.OfType<Window>().FirstOrDefault(window => window.GetType().Name == "MainWindow");
            if (mainWindow == null)
                return;

            if (mainWindow.FindName("RadioPvp") is RadioButton pvpButton)
                pvpButton.IsChecked = true;

            if (mainWindow.FindName("RadioPve") is RadioButton pveButton)
            {
                pveButton.IsEnabled = false;
                pveButton.IsChecked = false;

                // Hide the entire PVP/PVE switch container. The named controls are
                // retained only to keep the existing generated code-behind binary-safe.
                if (pveButton.Parent is FrameworkElement buttonPanel &&
                    buttonPanel.Parent is FrameworkElement profileContainer)
                {
                    profileContainer.Visibility = Visibility.Collapsed;
                }
                else
                {
                    pveButton.Visibility = Visibility.Collapsed;
                }
            }
        }, DispatcherPriority.Loaded);
    }
}
