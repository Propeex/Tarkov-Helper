using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using TarkovHelper.Models.Ammo;
using TarkovHelper.Services;
using TarkovHelper.Services.Ammo;

namespace TarkovHelper.Pages;

public partial class AmmoPage : UserControl
{
    private const string SelectedCaliberKey = "ammo.selectedCaliber";
    private readonly AmmoDbService _service = AmmoDbService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly ObservableCollection<AmmoItem> _visibleItems = new();
    private bool _updating;

    public AmmoPage()
    {
        InitializeComponent();
        AmmoGrid.ItemsSource = _visibleItems;
    }

    private async void AmmoPage_Loaded(object sender, RoutedEventArgs e)
    {
        _service.DataRefreshed -= Service_DataRefreshed;
        _service.DataRefreshed += Service_DataRefreshed;
        LoadColumnSettings();
        await _service.RefreshAsync();
        PopulateCalibers();
    }

    private void AmmoPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _service.DataRefreshed -= Service_DataRefreshed;
    }

    private void Service_DataRefreshed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(PopulateCalibers);
    }

    private void PopulateCalibers()
    {
        var previous = _settings.GetValue(SelectedCaliberKey);
        var calibers = _service.Items
            .GroupBy(item => item.Caliber, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CaliberChoice(group.Key, group.First().CaliberDisplay, group.Count()))
            .OrderBy(choice => choice.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        _updating = true;
        CaliberList.ItemsSource = calibers;
        CaliberList.DisplayMemberPath = nameof(CaliberChoice.Label);
        CaliberList.SelectedItem = calibers.FirstOrDefault(choice => string.Equals(choice.Key, previous, StringComparison.OrdinalIgnoreCase))
                                   ?? calibers.FirstOrDefault();
        _updating = false;
        ApplySelectedCaliber();
    }

    private void CaliberList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating)
            return;
        ApplySelectedCaliber();
    }

    private void ApplySelectedCaliber()
    {
        _visibleItems.Clear();
        if (CaliberList.SelectedItem is not CaliberChoice selected)
        {
            TxtSummary.Text = "표시할 탄약 데이터가 없습니다.";
            return;
        }

        _settings.SetValue(SelectedCaliberKey, selected.Key);
        foreach (var item in _service.Items.Where(item => string.Equals(item.Caliber, selected.Key, StringComparison.OrdinalIgnoreCase)))
            _visibleItems.Add(item);

        TxtSummary.Text = $"{selected.DisplayName} · {_visibleItems.Count:N0}종";
    }

    private void LoadColumnSettings()
    {
        _updating = true;
        ChkDamage.IsChecked = ReadBool("ammo.column.damage", true);
        ChkPenetration.IsChecked = ReadBool("ammo.column.penetration", true);
        ChkArmorDamage.IsChecked = ReadBool("ammo.column.armorDamage", true);
        ChkAccuracy.IsChecked = ReadBool("ammo.column.accuracy", true);
        ChkRecoil.IsChecked = ReadBool("ammo.column.recoil", true);
        ChkFragmentation.IsChecked = ReadBool("ammo.column.fragmentation", true);
        ChkBleed.IsChecked = ReadBool("ammo.column.bleed", true);
        ChkArmorClasses.IsChecked = ReadBool("ammo.column.armorClasses", true);
        ChkAcquisition.IsChecked = ReadBool("ammo.column.acquisition", true);
        _updating = false;
        ApplyColumnVisibility();
    }

    private void ColumnOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;

        _settings.SetValue("ammo.column.damage", (ChkDamage.IsChecked == true).ToString());
        _settings.SetValue("ammo.column.penetration", (ChkPenetration.IsChecked == true).ToString());
        _settings.SetValue("ammo.column.armorDamage", (ChkArmorDamage.IsChecked == true).ToString());
        _settings.SetValue("ammo.column.accuracy", (ChkAccuracy.IsChecked == true).ToString());
        _settings.SetValue("ammo.column.recoil", (ChkRecoil.IsChecked == true).ToString());
        _settings.SetValue("ammo.column.fragmentation", (ChkFragmentation.IsChecked == true).ToString());
        _settings.SetValue("ammo.column.bleed", (ChkBleed.IsChecked == true).ToString());
        _settings.SetValue("ammo.column.armorClasses", (ChkArmorClasses.IsChecked == true).ToString());
        _settings.SetValue("ammo.column.acquisition", (ChkAcquisition.IsChecked == true).ToString());
        ApplyColumnVisibility();
    }

    private void ApplyColumnVisibility()
    {
        ColDamage.Visibility = Visible(ChkDamage);
        ColPenetration.Visibility = Visible(ChkPenetration);
        ColArmorDamage.Visibility = Visible(ChkArmorDamage);
        ColAccuracy.Visibility = Visible(ChkAccuracy);
        ColRecoil.Visibility = Visible(ChkRecoil);
        ColFragmentation.Visibility = Visible(ChkFragmentation);
        ColBleed.Visibility = Visible(ChkBleed);
        ColArmorClasses.Visibility = Visible(ChkArmorClasses);
        ColAcquisition.Visibility = Visible(ChkAcquisition);
    }

    private bool ReadBool(string key, bool defaultValue) =>
        bool.TryParse(_settings.GetValue(key, defaultValue.ToString()), out var value) ? value : defaultValue;

    private static Visibility Visible(CheckBox checkBox) => checkBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private sealed record CaliberChoice(string Key, string DisplayName, int Count)
    {
        public string Label => $"{DisplayName} ({Count})";
    }
}
