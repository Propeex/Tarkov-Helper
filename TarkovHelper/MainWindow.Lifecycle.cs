using System.Windows;
using TarkovHelper.Services;

namespace TarkovHelper;

public partial class MainWindow
{
    protected override void OnClosed(EventArgs e)
    {
        // Stop callbacks that can otherwise reach a closed dispatcher while the
        // process is tearing down or while a test host briefly creates the window.
        _fontRefreshTimer?.Dispose();
        _fontRefreshTimer = null;

        if (_fontWatcher != null)
        {
            _fontWatcher.EnableRaisingEvents = false;
            _fontWatcher.Dispose();
            _fontWatcher = null;
        }

        DatabaseUpdateService.Instance.DatabaseUpdated -= OnDatabaseUpdated;
        _logSyncService.QuestEventDetected -= OnQuestEventDetected;

        _loc.LanguageChanged -= OnLanguageChanged;
        _settingsService.PlayerLevelChanged -= OnPlayerLevelChanged;
        _settingsService.ScavRepChanged -= OnScavRepChanged;
        _settingsService.DspDecodeCountChanged -= OnDspDecodeCountChanged;
        _settingsService.HasEodEditionChanged -= OnEditionChanged;
        _settingsService.HasUnheardEditionChanged -= OnEditionChanged;
        _settingsService.PrestigeLevelChanged -= OnPrestigeLevelChanged;
        _settingsService.FontFamilyNameChanged -= OnFontFamilyNameChanged;
        ProfileService.Instance.ProfileChanged -= OnProfileChanged;

        RadioPvp.Checked -= ProfileRadio_Checked;
        RadioPve.Checked -= ProfileRadio_Checked;

        base.OnClosed(e);
    }
}
