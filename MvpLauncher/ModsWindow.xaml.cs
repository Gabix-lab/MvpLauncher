using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace MvpLauncher
{
    public partial class ModsWindow : Window
    {
        private readonly string _modsFolder;
        private readonly CacheModManager? _cacheManager;
        public ObservableCollection<ModItem> Mods { get; } = new();

        public ModsWindow(string modsFolder, CacheModManager? cacheManager)
        {
            InitializeComponent();
            _modsFolder = modsFolder;
            _cacheManager = cacheManager;
            DataContext = this;

            if (_cacheManager != null)
            {
                _cacheManager.VersionChanged += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        var cacheItem = Mods.FirstOrDefault(m => m.IsLocked);
                        if (cacheItem != null)
                        {
                            cacheItem.Version = _cacheManager.CurrentVersion;
                        }
                    });
                };
            }

            LoadMods();

            _ = _cacheManager?.FetchVersionAsync();
        }

        private void LoadMods()
        {
            Mods.Clear();

            if (Directory.Exists(_modsFolder))
            {
                var map = new Dictionary<string, ModItem>();

                foreach (var path in Directory.GetFiles(_modsFolder, "*.jar"))
                {
                    string fileName = Path.GetFileName(path);
                    map[fileName] = new ModItem
                    {
                        DisplayName = fileName,
                        BaseName = fileName,
                        ModsFolder = _modsFolder,
                        IsEnabled = true
                    };
                }

                foreach (var path in Directory.GetFiles(_modsFolder, "*.jar.disabled"))
                {
                    string fileName = Path.GetFileName(path);
                    string actualBase = fileName.Substring(0, fileName.Length - ".disabled".Length);
                    map[actualBase] = new ModItem
                    {
                        DisplayName = actualBase,
                        BaseName = actualBase,
                        ModsFolder = _modsFolder,
                        IsEnabled = false
                    };
                }

                foreach (var item in map.Values.OrderBy(m => m.DisplayName))
                {
                    Mods.Add(item);
                }
            }

            var cacheItem = _cacheManager?.GetCacheModItem();
            if (cacheItem != null)
            {
                Mods.Add(cacheItem);
            }

            lblCount.Text = $"{Mods.Count} mod";
            lblEmpty.Visibility = Mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(_modsFolder))
                {
                    Directory.CreateDirectory(_modsFolder);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = _modsFolder,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Nem sikerült megnyitni a mappát: {ex.Message}",
                    "Hiba", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}