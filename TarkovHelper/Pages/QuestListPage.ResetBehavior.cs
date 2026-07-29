using System.Windows.Controls;
using TarkovHelper.Services;

namespace TarkovHelper.Pages;

public partial class QuestListPage
{
    public void ShowAllQuests()
    {
        _isInitializing = true;
        try
        {
            TxtSearch.Text = string.Empty;
            ChkKappaOnly.IsChecked = false;
            ChkItemRequired.IsChecked = false;
            CmbTrader.SelectedIndex = 0;
            CmbMap.SelectedIndex = 0;

            var allItem = CmbStatus.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    QuestStatusSelector.DefaultStatus,
                    StringComparison.OrdinalIgnoreCase));
            if (allItem != null)
                CmbStatus.SelectedItem = allItem;
        }
        finally
        {
            _isInitializing = false;
        }

        ApplyActualQuestStatuses();
    }
}
