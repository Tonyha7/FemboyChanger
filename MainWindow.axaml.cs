using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FemboyChanger.Core;
using System.Linq;
using System.Threading.Tasks;

namespace FemboyChanger
{
    public class WeaponGroup
    {
        public string Key { get; set; } = "";
        public IGrouping<int, SkinData>? Group { get; set; }
    }

    public partial class MainWindow : Window
    {
        private bool _isChinese = false;
        private IGrouping<int, SkinData> _currentWeaponGroup;
        private SkinData _selectedSkin;

        public MainWindow()
        {
            InitializeComponent();
            this.Closed += MainWindow_Closed;
            
            _ = InitializeDataAsync();
        }

        private async Task InitializeDataAsync()
        {
            StatusText.Text = "Status: Loading Offsets...";
            await Offsets.LoadOffsetsAsync();
            
            await LoadSkinsData();
            
            SkinChangerLogic.Start();
        }

        private async Task LoadSkinsData()
        {
            MainGrid.IsEnabled = false;
            StatusText.Text = "Status: Loading Skins...";
            await DataProvider.LoadSkinsAsync(_isChinese);
            
            var groupedSkins = DataProvider.Skins.GroupBy(s => s.WeaponId).ToList();
            
            // Map the ListBox Items
            var weaponsData = groupedSkins.Select(g => new WeaponGroup {
                Key = g.FirstOrDefault()?.WeaponName ?? $"Weapon {g.Key}",
                Group = g
            }).ToList();

            WeaponList.ItemsSource = weaponsData;
            StatusText.Text = "Status: Ready";
            MainGrid.IsEnabled = true;
        }

        private void OnLanguageToggled(object sender, RoutedEventArgs e)
        {
            var toggle = sender as Avalonia.Controls.Primitives.ToggleButton;
            _isChinese = toggle?.IsChecked ?? false;
            if (toggle != null)
                toggle.Content = _isChinese ? "Language: CN" : "Language: EN";
            
            _ = LoadSkinsData();
        }

        private void OnUpdateDataClick(object sender, RoutedEventArgs e)
        {
            _ = InitializeDataAsync();
        }

        private void OnWeaponSelected(object sender, SelectionChangedEventArgs e)
        {
            var selected = WeaponList.SelectedItem;
            if (selected == null) return;
            
            var propInfo = selected.GetType().GetProperty("Group");
            _currentWeaponGroup = propInfo.GetValue(selected) as IGrouping<int, SkinData>;
            
            if (_currentWeaponGroup != null)
            {
                SkinsList.ItemsSource = _currentWeaponGroup.ToList();
            }
        }

        private void OnSkinSelected(object sender, SelectionChangedEventArgs e)
        {
            _selectedSkin = SkinsList.SelectedItem as SkinData;
        }

        private void OnApplySkinClick(object sender, RoutedEventArgs e)
        {
            if (_selectedSkin == null) return;

            var skinInfo = new SkinInfo
            {
                PaintKit = _selectedSkin.PaintIndex,
                Wear = (float)WearSlider.Value,
                Seed = (int)(SeedBox.Value ?? 0),
                StatTrak = (int)(StatTrakBox.Value ?? -1)
            };

            // If it's a glove (WeaponId between 5027 and 5035 usually)
            if (_selectedSkin.WeaponId >= 5027 && _selectedSkin.WeaponId <= 5035)
            {
                SkinChangerLogic.GloveConfig = skinInfo;
                SkinChangerLogic.GloveDefIndex = _selectedSkin.WeaponId;
            }
            else
            {
                SkinChangerLogic.Config[_selectedSkin.WeaponId] = skinInfo;
            }

            SkinChangerLogic.ForceUpdate = true;
            SkinChangerLogic.Log($"UI: Applied {_selectedSkin.Name} (Paint: {skinInfo.PaintKit}) for WeaponId: {_selectedSkin.WeaponId}");
            StatusText.Text = $"Status: Applied {_selectedSkin.Name}";
        }

        private void OnForceUpdateClick(object sender, RoutedEventArgs e)
        {
            SkinChangerLogic.ForceUpdate = true;
            StatusText.Text = "Status: Force Update triggered";
        }

        private void MainWindow_Closed(object? sender, System.EventArgs e)
        {
            SkinChangerLogic.Stop();
        }
    }
}