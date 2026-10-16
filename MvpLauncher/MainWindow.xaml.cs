using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ProcessBuilder;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace MvpLauncher
{
    public partial class MainWindow : Window
    {
        private const string DefaultServerName = "WoodCraft";
        private const string DefaultServerIp = "play.woodcraft.hu";
        private const int MaxMultiLaunchClients = 10;

        private MinecraftPath _minecraftPath;
        private string _modsFolder;
        private CacheModManager _cacheManager;
        private AppSettings _settings;
        private bool _isUpdatingMods;
        private bool _isLoadingProfiles;
        private bool _isLoadingSettings;
        private bool _updatingMultiCount;
        private bool _restoringMultiLaunch;
        private JELoginHandler? _loginHandler;
        private UpdateInfo? _pendingUpdate;
        private System.Windows.Threading.DispatcherTimer? _updateTimer;
        private System.Windows.Threading.DispatcherTimer? _searchDebounceTimer;
        private string? _javaPath;

        private readonly ModrinthService _modrinth = new();
        private readonly UpdateService _updateService = new();

        public ObservableCollection<ModItem> AllMods { get; } = new();
        public ObservableCollection<ModItem> EnabledMods { get; } = new();
        public ObservableCollection<ModItem> MandatoryMods { get; } = new();
        public ObservableCollection<ModItem> DisabledMods { get; } = new();

        public ObservableCollection<ModrinthItem> ModrinthResults { get; } = new();
        public ObservableCollection<ModrinthVersion> ModrinthVersions { get; } = new();

        public ObservableCollection<MultiLaunchInstance> MultiLaunchInstances { get; } = new();

        private string T(string key) => TranslationSource.Instance[key];

        private static string GetStringProperty(object obj, string propertyName)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propertyName);
                if (prop != null)
                {
                    var value = prop.GetValue(obj);
                    if (value is string s && !string.IsNullOrEmpty(s))
                    {
                        return s;
                    }
                }
            }
            catch { }
            return "";
        }

        private static bool TryRemoveAccount(object accountManager, object account)
        {
            if (accountManager == null || account == null) return false;

            var methodNames = new[] { "RemoveAccount", "Remove", "DeleteAccount", "Delete", "Unregister", "RemoveAccountAsync" };

            foreach (var methodName in methodNames)
            {
                try
                {
                    var methods = accountManager.GetType().GetMethods();

                    foreach (var method in methods)
                    {
                        if (method.Name != methodName) continue;

                        var parameters = method.GetParameters();
                        if (parameters.Length != 1) continue;

                        var paramType = parameters[0].ParameterType;

                        if (!paramType.IsInstanceOfType(account)) continue;

                        method.Invoke(accountManager, new[] { account });

                        try
                        {
                            var saveMethod = accountManager.GetType().GetMethod("SaveAccounts");
                            saveMethod?.Invoke(accountManager, null);
                        }
                        catch { }

                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            string customPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".mvplauncher");
            _minecraftPath = new MinecraftPath(customPath);
            _modsFolder = Path.Combine(_minecraftPath.BasePath, "mods");

            _cacheManager = new CacheModManager(
                Path.Combine(Path.GetTempPath(), "mvplauncher-cache"));

            _cacheManager.ClearCache();

            _cacheManager.Mods.Add(new CacheMod
            {
                FileName = "mvp.jar",
                Url = "https://mvp-backend-bods.onrender.com/mod/mvp",
                VersionUrl = "https://mvp-backend-bods.onrender.com/mod/version"
            });

            _settings = AppSettings.Load();
            TranslationSource.Instance.Language = _settings.Language;

            try
            {
                _loginHandler = JELoginHandlerBuilder.BuildDefault();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Microsoft] Init hiba: {ex.Message}");
                _loginHandler = null;
            }

            _javaPath = JavaManager.FindJava();

            _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _searchDebounceTimer.Tick += async (s, ev) =>
            {
                _searchDebounceTimer.Stop();
                await ModrinthSearchAsync(txtModrinthSearch.Text.Trim());
            };

            EnsureModsFolderExists();
            SyncBundledModsToModsFolder();
            EnsureDefaultServerExists();
            LoadMods();
            LoadProfiles();
            LoadSettingsIntoUI();

            long totalRamMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024L);
            sldRam.Maximum = Math.Max(totalRamMb, 2048);

            Loaded += (s, e) =>
            {
                App.StartupStopwatch.Stop();
                lblStartupTime.Text = $"{T("StartupTime")}: {App.StartupStopwatch.ElapsedMilliseconds} ms";

                if (lblAboutVersion != null)
                {
                    lblAboutVersion.Text = $"{T("AboutVersion")}: {UpdateService.CurrentVersion}";
                }

                UpdateSkin();
                RestoreMultiLaunchFromSettings();
                _ = CheckForUpdatesAsync(showPopup: true);
                StartUpdateTimer();
            };

            _ = _cacheManager.FetchVersionAsync();

            Closing += (s, e) =>
            {
                SaveCurrentSettings();
                SaveMultiLaunchToSettings();
                _cacheManager.ClearCache();
            };
        }

        private void EnsureModsFolderExists()
        {
            try
            {
                if (!Directory.Exists(_modsFolder))
                {
                    Directory.CreateDirectory(_modsFolder);
                }
            }
            catch { }
        }

        private void EnsureDefaultServerExists()
        {
            try
            {
                string serversDatPath = Path.Combine(_minecraftPath.BasePath, "servers.dat");
                ServersDatManager.EnsureServerExists(serversDatPath, DefaultServerName, DefaultServerIp);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ServersDat] HIBA: {ex.Message}");
            }
        }

        private void SyncBundledModsToModsFolder()
        {
            try
            {
                string bundledPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "BundledMods");

                if (!Directory.Exists(bundledPath)) return;

                foreach (var bundledFile in Directory.GetFiles(bundledPath, "*.jar"))
                {
                    string fileName = Path.GetFileName(bundledFile);
                    string destEnabled = Path.Combine(_modsFolder, fileName);
                    string destDisabled = destEnabled + ".disabled";

                    if (File.Exists(destEnabled) || File.Exists(destDisabled))
                    {
                        continue;
                    }

                    File.Copy(bundledFile, destEnabled, true);
                }
            }
            catch { }
        }

        private void LoadMods()
        {
            foreach (var mod in AllMods)
            {
                mod.PropertyChanged -= OnModPropertyChanged;
            }

            AllMods.Clear();

            if (Directory.Exists(_modsFolder))
            {
                foreach (var path in Directory.GetFiles(_modsFolder, "*.jar"))
                {
                    AddModItem(path, true);
                }

                foreach (var path in Directory.GetFiles(_modsFolder, "*.jar.disabled"))
                {
                    AddModItem(path, false);
                }
            }

            var cacheItem = _cacheManager.GetCacheModItem();
            if (cacheItem != null)
            {
                AllMods.Add(cacheItem);
            }

            foreach (var mod in AllMods)
            {
                mod.PropertyChanged += OnModPropertyChanged;
            }

            BuildDependencyGraph();
            RefreshModCategories();
        }

        private void AddModItem(string path, bool enabled)
        {
            string fileName = Path.GetFileName(path);
            string actualBase = enabled
                ? fileName
                : fileName.Substring(0, fileName.Length - ".disabled".Length);

            string fullPath = Path.Combine(_modsFolder, actualBase);

            var modItem = new ModItem
            {
                DisplayName = actualBase,
                BaseName = actualBase,
                ModsFolder = _modsFolder,
                IsEnabled = enabled
            };

            var info = FabricMetadataReader.Read(File.Exists(fullPath) ? fullPath : path);
            if (info != null)
            {
                modItem.ModId = info.Id;
                modItem.Version = info.Version;
                foreach (var dep in info.Depends)
                {
                    modItem.Dependencies.Add(dep);
                }
            }

            AllMods.Add(modItem);
        }

        private void BuildDependencyGraph()
        {
            var byId = GetAllModsById();

            foreach (var mod in AllMods)
            {
                mod.Dependents.Clear();
            }

            foreach (var mod in AllMods)
            {
                foreach (var depId in mod.Dependencies)
                {
                    if (byId.TryGetValue(depId, out var depMod))
                    {
                        if (!depMod.Dependents.Contains(mod.DisplayName))
                        {
                            depMod.Dependents.Add(mod.DisplayName);
                        }
                    }
                }
            }

            foreach (var mod in AllMods)
            {
                mod.RefreshDependencyText();
            }

            UpdateLocks();
        }

        private Dictionary<string, ModItem> GetAllModsById()
        {
            var byId = new Dictionary<string, ModItem>();
            foreach (var mod in AllMods)
            {
                if (!string.IsNullOrEmpty(mod.ModId))
                {
                    byId[mod.ModId] = mod;
                }
            }
            return byId;
        }

        private void UpdateLocks()
        {
            foreach (var mod in AllMods)
            {
                if (mod.IsFromCache)
                {
                    mod.IsLocked = true;
                    continue;
                }

                bool anyEnabledDependent = false;
                foreach (var dependentName in mod.Dependents)
                {
                    var dependent = AllMods.FirstOrDefault(m => m.DisplayName == dependentName);
                    if (dependent != null && dependent.IsEnabled)
                    {
                        anyEnabledDependent = true;
                        break;
                    }
                }

                mod.IsLocked = anyEnabledDependent;
            }
        }

        private void RefreshModCategories()
        {
            EnabledMods.Clear();
            MandatoryMods.Clear();
            DisabledMods.Clear();

            foreach (var mod in AllMods.OrderBy(m => m.DisplayName))
            {
                if (mod.IsLocked || mod.IsFromCache)
                {
                    MandatoryMods.Add(mod);
                }
                else if (mod.IsEnabled)
                {
                    EnabledMods.Add(mod);
                }
                else
                {
                    DisabledMods.Add(mod);
                }
            }
        }

        private void OnModPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ModItem.IsEnabled)) return;
            if (_isUpdatingMods) return;

            _isUpdatingMods = true;
            try
            {
                if (sender is ModItem mod && mod.IsEnabled)
                {
                    EnableDependenciesRecursive(mod, new HashSet<ModItem>());
                }

                UpdateLocks();
                RefreshModCategories();
            }
            finally
            {
                _isUpdatingMods = false;
            }
        }

        private void EnableDependenciesRecursive(ModItem mod, HashSet<ModItem> visited)
        {
            if (!visited.Add(mod)) return;

            var byId = GetAllModsById();
            foreach (var depId in mod.Dependencies)
            {
                if (byId.TryGetValue(depId, out var depMod) && !depMod.IsEnabled)
                {
                    depMod.IsEnabled = true;
                    EnableDependenciesRecursive(depMod, visited);
                }
            }
        }

        private void UpdateSkin()
        {
            try
            {
                string identifier = "";

                if (!string.IsNullOrEmpty(_settings.SelectedProfile))
                {
                    var profile = _settings.Profiles.FirstOrDefault(p => p.Name == _settings.SelectedProfile);
                    if (profile != null && profile.Type == "Microsoft" && !string.IsNullOrEmpty(profile.Uuid))
                    {
                        identifier = profile.Uuid;
                    }
                    else
                    {
                        identifier = _settings.SelectedProfile ?? "";
                    }
                }

                if (string.IsNullOrEmpty(identifier))
                {
                    imgSkin.Source = null!;
                    lblSkinLoading.Visibility = Visibility.Visible;
                    return;
                }

                lblSkinLoading.Visibility = Visibility.Collapsed;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(SkinService.GetBodyUrl(identifier), UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();

                imgSkin.Source = bitmap;
            }
            catch
            {
                imgSkin.Source = null!;
                lblSkinLoading.Visibility = Visibility.Visible;
            }
        }

        private void LoadProfiles()
        {
            _isLoadingProfiles = true;

            if (_loginHandler != null)
            {
                try
                {
                    var accounts = _loginHandler.AccountManager.GetAccounts();
                    foreach (var account in accounts)
                    {
                        var identifier = account.Identifier;
                        if (string.IsNullOrEmpty(identifier) ||
                            _settings.Profiles.Any(p => p.Uuid == identifier))
                        {
                            continue;
                        }

                        string displayName = "";

                        try
                        {
                            var profileProp = account.GetType().GetProperty("Profile");
                            var profile = profileProp?.GetValue(account);
                            if (profile != null)
                            {
                                displayName = GetStringProperty(profile, "Gamertag");
                                if (string.IsNullOrEmpty(displayName))
                                {
                                    displayName = GetStringProperty(profile, "Name");
                                }
                            }
                        }
                        catch { }

                        if (string.IsNullOrEmpty(displayName))
                        {
                            displayName = GetStringProperty(account, "Gamertag");
                        }

                        if (string.IsNullOrEmpty(displayName))
                        {
                            displayName = GetStringProperty(account, "Name");
                        }

                        if (string.IsNullOrEmpty(displayName))
                        {
                            if (identifier.Length >= 8)
                            {
                                displayName = $"MS_{identifier.Substring(0, 8)}";
                            }
                            else
                            {
                                displayName = $"MS_{identifier}";
                            }
                        }

                        _settings.Profiles.Add(new Profile
                        {
                            Name = displayName,
                            Type = "Microsoft",
                            Uuid = identifier
                        });
                    }
                    _settings.Save();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Microsoft] LoadProfiles hiba: {ex.Message}");
                }
            }

            cmbProfiles.Items.Clear();
            var sortedNames = _settings.Profiles
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var name in sortedNames)
            {
                cmbProfiles.Items.Add(name);
            }

            if (!string.IsNullOrEmpty(_settings.SelectedProfile) &&
                cmbProfiles.Items.Contains(_settings.SelectedProfile))
            {
                cmbProfiles.SelectedItem = _settings.SelectedProfile;
            }
            else if (cmbProfiles.Items.Count > 0)
            {
                cmbProfiles.SelectedIndex = 0;
            }

            _isLoadingProfiles = false;

            UpdateSkin();
            UpdateMultiLaunchAccounts();
        }

        private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingProfiles) return;
            if (cmbProfiles.SelectedItem is string name)
            {
                _settings.SelectedProfile = name;
                _settings.Save();
                UpdateSkin();
            }
        }

        private void BtnAddProfile_Click(object sender, RoutedEventArgs e)
        {
            txtNewProfile.Text = "";
            lblNewProfileError.Text = "";
            ProfileOverlay.Visibility = Visibility.Visible;
            txtNewProfile.Focus();
        }

        private void BtnCancelProfile_Click(object sender, RoutedEventArgs e)
        {
            ProfileOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnConfirmProfile_Click(object sender, RoutedEventArgs e)
        {
            AddProfileFromInput();
        }

        private void TxtNewProfile_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddProfileFromInput();
            }
            else if (e.Key == Key.Escape)
            {
                ProfileOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void AddProfileFromInput()
        {
            string name = txtNewProfile.Text.Trim();

            if (string.IsNullOrEmpty(name))
            {
                lblNewProfileError.Text = T("ErrorNameRequired");
                return;
            }

            if (name.Length > 16)
            {
                lblNewProfileError.Text = T("ErrorNameTooLong");
                return;
            }

            if (_settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                lblNewProfileError.Text = T("ErrorNameExists");
                return;
            }

            _settings.Profiles.Add(new Profile { Name = name, Type = "Offline" });
            _settings.SelectedProfile = name;
            _settings.Save();

            LoadProfiles();
            ProfileOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            string selectedName = _settings.SelectedProfile;

            if (string.IsNullOrEmpty(selectedName))
            {
                MessageBox.Show("Nincs kiválasztott profil.",
                    "Törlés", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var profile = _settings.Profiles.FirstOrDefault(p => p.Name == selectedName);
            if (profile == null)
            {
                MessageBox.Show("A kiválasztott profil nem található.",
                    "Törlés", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_settings.Profiles.Count <= 1)
            {
                MessageBox.Show("Legalább egy profilnak mindig léteznie kell.",
                    "Törlés", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Biztosan törlöd a(z) \"{selectedName}\" profilt?",
                "Profil törlése",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            if (profile.Type == "Microsoft" && !string.IsNullOrEmpty(profile.Uuid) && _loginHandler != null)
            {
                try
                {
                    var accounts = _loginHandler.AccountManager.GetAccounts();
                    var toRemove = accounts.FirstOrDefault(a => a.Identifier == profile.Uuid);
                    if (toRemove != null)
                    {
                        TryRemoveAccount(_loginHandler.AccountManager, toRemove);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Microsoft] Törlés hiba: {ex.Message}");
                }
            }

            _settings.Profiles.Remove(profile);

            var nextProfile = _settings.Profiles.FirstOrDefault();
            _settings.SelectedProfile = nextProfile?.Name ?? "";
            _settings.Save();

            LoadProfiles();

            lblStatus.Text = $"Profil törölve: {selectedName}";
        }

        private async void BtnAddMicrosoft_Click(object sender, RoutedEventArgs e)
        {
            if (_loginHandler == null)
            {
                MessageBox.Show(T("MicrosoftNotAvailable"),
                    T("Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                btnAddMicrosoft.IsEnabled = false;
                lblStatus.Text = T("MicrosoftLogin");

                var session = await _loginHandler.Authenticate();

                if (session != null)
                {
                    try
                    {
                        _loginHandler.AccountManager.SaveAccounts();
                    }
                    catch { }

                    string sessionName = GetStringProperty(session, "Username");
                    if (string.IsNullOrEmpty(sessionName))
                    {
                        sessionName = GetStringProperty(session, "PlayerName");
                    }
                    if (string.IsNullOrEmpty(sessionName))
                    {
                        sessionName = GetStringProperty(session, "Name");
                    }
                    if (string.IsNullOrEmpty(sessionName))
                    {
                        sessionName = "Microsoft";
                    }

                    var identifier = session.UUID;
                    if (!_settings.Profiles.Any(p => p.Uuid == identifier))
                    {
                        _settings.Profiles.Add(new Profile
                        {
                            Name = sessionName,
                            Type = "Microsoft",
                            Uuid = identifier
                        });
                    }

                    _settings.SelectedProfile = sessionName;
                    _settings.Save();

                    LoadProfiles();

                    lblStatus.Text = $"{T("LoggedIn")}: {sessionName}";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{T("MicrosoftLoginError")}:\n{ex.Message}",
                    T("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = T("LoginFailed");
            }
            finally
            {
                btnAddMicrosoft.IsEnabled = true;
            }
        }

        private void LoadSettingsIntoUI()
        {
            _isLoadingSettings = true;

            int ram = _settings.RamMb;
            if (ram < sldRam.Minimum) ram = (int)sldRam.Minimum;
            if (ram > sldRam.Maximum) ram = (int)sldRam.Maximum;
            sldRam.Value = ram;
            lblRamValue.Text = $"{ram} MB";

            switch (_settings.Theme)
            {
                case "Dark": rbThemeDark.IsChecked = true; break;
                case "Light": rbThemeLight.IsChecked = true; break;
                default: rbThemeSystem.IsChecked = true; break;
            }

            UpdateThemeDisplay();

            switch (_settings.Language)
            {
                case "en": rbLanguageEn.IsChecked = true; break;
                default: rbLanguageHu.IsChecked = true; break;
            }

            switch (_settings.LauncherBehavior)
            {
                case "Close": rbBehClose.IsChecked = true; break;
                case "Nothing": rbBehNothing.IsChecked = true; break;
                default: rbBehHide.IsChecked = true; break;
            }

            SelectJavaPreset(_settings.JavaPreset);
            txtJavaArgs.Text = _settings.JavaArgs ?? "";

            _isLoadingSettings = false;
        }

        private void SelectJavaPreset(string preset)
        {
            for (int i = 0; i < cmbJavaPreset.Items.Count; i++)
            {
                if (cmbJavaPreset.Items[i] is ComboBoxItem item &&
                    item.Tag is string tag && tag == preset)
                {
                    cmbJavaPreset.SelectedIndex = i;
                    return;
                }
            }

            if (cmbJavaPreset.Items.Count > 0)
            {
                cmbJavaPreset.SelectedIndex = 0;
            }
        }

        private void UpdateThemeDisplay()
        {
            switch (_settings.Theme)
            {
                case "Dark": lblThemeDisplay.Text = T("ThemeDark"); break;
                case "Light": lblThemeDisplay.Text = T("ThemeLight"); break;
                default: lblThemeDisplay.Text = T("ThemeSystem"); break;
            }
        }

        private void SaveCurrentSettings()
        {
            _settings.RamMb = (int)sldRam.Value;
            _settings.Save();
        }

        private void SldRam_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblRamValue != null)
            {
                lblRamValue.Text = $"{(int)e.NewValue} MB";
            }
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_isLoadingSettings) return;
            if (sender is RadioButton rb && rb.Tag is string theme)
            {
                _settings.Theme = theme;
                _settings.Save();
                ThemeManager.ApplyTheme(theme);
                if (lblThemeDisplay != null)
                {
                    UpdateThemeDisplay();
                }
            }
        }

        private void Language_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_isLoadingSettings) return;
            if (sender is RadioButton rb && rb.Tag is string lang)
            {
                _settings.Language = lang;
                _settings.Save();
                TranslationSource.Instance.Language = lang;

                UpdateThemeDisplay();

                if (!App.StartupStopwatch.IsRunning)
                {
                    lblStartupTime.Text = $"{T("StartupTime")}: {App.StartupStopwatch.ElapsedMilliseconds} ms";
                }
            }
        }

        private void Behavior_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_isLoadingSettings) return;
            if (sender is RadioButton rb && rb.Tag is string behavior)
            {
                _settings.LauncherBehavior = behavior;
                _settings.Save();
            }
        }

        private void CmbJavaPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_isLoadingSettings) return;

            if (cmbJavaPreset.SelectedItem is ComboBoxItem item &&
                item.Tag is string preset)
            {
                _settings.JavaPreset = preset;
                _settings.Save();
            }
        }

        private void TxtJavaArgs_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_isLoadingSettings) return;

            _settings.JavaArgs = txtJavaArgs.Text;
            _settings.Save();
        }

        private List<string> BuildJavaArguments()
        {
            var args = new List<string>();

            switch (_settings.JavaPreset)
            {
                case "G1GC":
                    args.Add("-XX:+UnlockExperimentalVMOptions");
                    args.Add("-XX:+UseG1GC");
                    args.Add("-XX:G1NewSizePercent=20");
                    args.Add("-XX:G1ReservePercent=20");
                    args.Add("-XX:MaxGCPauseMillis=50");
                    args.Add("-XX:G1HeapRegionSize=32M");
                    break;
                case "ZGC":
                    args.Add("-XX:+UnlockExperimentalVMOptions");
                    args.Add("-XX:+UseZGC");
                    break;
                case "Custom":
                    if (!string.IsNullOrWhiteSpace(_settings.JavaArgs))
                    {
                        var parts = _settings.JavaArgs
                            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in parts)
                        {
                            args.Add(p.Trim());
                        }
                    }
                    break;
            }

            return args;
        }

        private void StartUpdateTimer()
        {
            _updateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(30)
            };

            _updateTimer.Tick += async (s, e) =>
            {
                await CheckForUpdatesAsync(showPopup: false);
            };

            _updateTimer.Start();
        }

        private async Task CheckForUpdatesAsync(bool showPopup)
        {
            try
            {
                var update = await _updateService.CheckAsync();

                if (update == null)
                {
                    _pendingUpdate = null;
                    ShowUpdateBadge(false);
                    if (lblUpdateStatus != null)
                    {
                        lblUpdateStatus.Text = T("UpdateUpToDate");
                    }
                    if (lblUpdateVersion != null)
                    {
                        lblUpdateVersion.Visibility = Visibility.Collapsed;
                    }
                    if (lblUpdateBadge != null)
                    {
                        lblUpdateBadge.Visibility = Visibility.Collapsed;
                    }
                    return;
                }

                _pendingUpdate = update;
                ShowUpdateBadge(true);

                if (lblUpdateStatus != null)
                {
                    lblUpdateStatus.Text = T("UpdateAvailable");
                }
                if (lblUpdateVersion != null)
                {
                    lblUpdateVersion.Text = string.Format(T("UpdateAvailableShort"), update.Version);
                    lblUpdateVersion.Visibility = Visibility.Visible;
                }
                if (lblUpdateBadge != null)
                {
                    lblUpdateBadge.Visibility = Visibility.Visible;
                }

                if (showPopup)
                {
                    var window = new UpdateWindow(update, _updateService)
                    {
                        Owner = this
                    };
                    window.ShowDialog();
                }
            }
            catch
            {
                if (showPopup && lblUpdateStatus != null)
                {
                    lblUpdateStatus.Text = T("UpdateCheckError");
                }
            }
        }

        private void ShowUpdateBadge(bool visible)
        {
            try
            {
                var template = btnSettings.Template;
                if (template == null) return;

                var badge = template.FindName("UpdateBadge", btnSettings) as Border;
                if (badge != null)
                {
                    badge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch { }
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            btnCheckUpdate.IsEnabled = false;
            if (lblUpdateStatus != null)
            {
                lblUpdateStatus.Text = T("UpdateChecking");
            }

            await CheckForUpdatesAsync(showPopup: false);

            if (_pendingUpdate != null)
            {
                var window = new UpdateWindow(_pendingUpdate, _updateService)
                {
                    Owner = this
                };
                window.ShowDialog();
            }

            btnCheckUpdate.IsEnabled = true;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Any(f =>
                    f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
                {
                    e.Effects = DragDropEffects.Copy;
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files == null || files.Length == 0) return;

                EnsureModsFolderExists();

                int imported = 0;

                foreach (var file in files)
                {
                    if (!file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var destPath = Path.Combine(_modsFolder, fileName);
                        File.Copy(file, destPath, true);
                        imported++;
                    }
                    catch { }
                }

                if (imported > 0)
                {
                    LoadMods();
                    lblStatus.Text = string.Format(T("ModsImported"), imported);
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"{T("Error")}: {ex.Message}";
            }
        }

        private void ShowOnly(UIElement view)
        {
            MainView.Visibility = view == MainView ? Visibility.Visible : Visibility.Collapsed;
            ModsView.Visibility = view == ModsView ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = view == SettingsView ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnToggleMods_Click(object sender, RoutedEventArgs e)
        {
            if (ModsView.Visibility == Visibility.Visible)
            {
                ShowOnly(MainView);
                txtModrinthSearch.Text = "";
                ModrinthResults.Clear();
                ModrinthVersions.Clear();
            }
            else
            {
                ShowOnly(ModsView);
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsView.Visibility == Visibility.Visible)
            {
                ShowOnly(MainView);
            }
            else
            {
                ShowOnly(SettingsView);
            }
        }

        private void TxtMultiCount_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _updatingMultiCount || _restoringMultiLaunch) return;

            if (!int.TryParse(txtMultiCount.Text, out int count))
                return;

            if (count < 0) count = 0;
            if (count > MaxMultiLaunchClients) count = MaxMultiLaunchClients;

            while (MultiLaunchInstances.Count < count)
                AddMultiLaunchInstance();

            while (MultiLaunchInstances.Count > count)
                MultiLaunchInstances.RemoveAt(MultiLaunchInstances.Count - 1);

            RefreshMultiLaunchIndices();
            UpdateMultiEmptyLabel();
            SaveMultiLaunchToSettings();
        }

        private void BtnAddMultiInstance_Click(object sender, RoutedEventArgs e)
        {
            if (MultiLaunchInstances.Count >= MaxMultiLaunchClients) return;
            AddMultiLaunchInstance();
            RefreshMultiLaunchIndices();
            SyncMultiCountBox();
            UpdateMultiEmptyLabel();
            SaveMultiLaunchToSettings();
        }

        private void BtnRemoveMultiInstance_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is MultiLaunchInstance inst)
            {
                MultiLaunchInstances.Remove(inst);
                RefreshMultiLaunchIndices();
                SyncMultiCountBox();
                UpdateMultiEmptyLabel();
                SaveMultiLaunchToSettings();
            }
        }

        private void AddMultiLaunchInstance()
        {
            var inst = new MultiLaunchInstance();

            var sortedNames = _settings.Profiles
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var n in sortedNames)
                inst.AvailableAccounts.Add(n);

            if (inst.AvailableAccounts.Count > 0)
            {
                inst.SelectedAccount = !string.IsNullOrEmpty(_settings.SelectedProfile) &&
                                       inst.AvailableAccounts.Contains(_settings.SelectedProfile)
                    ? _settings.SelectedProfile
                    : inst.AvailableAccounts[0];
            }

            inst.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(MultiLaunchInstance.SelectedAccount))
                {
                    SaveMultiLaunchToSettings();
                }
            };

            inst.Index = MultiLaunchInstances.Count + 1;
            MultiLaunchInstances.Add(inst);
        }

        private void RefreshMultiLaunchIndices()
        {
            for (int i = 0; i < MultiLaunchInstances.Count; i++)
                MultiLaunchInstances[i].Index = i + 1;
        }

        private void SyncMultiCountBox()
        {
            _updatingMultiCount = true;
            txtMultiCount.Text = MultiLaunchInstances.Count.ToString();
            _updatingMultiCount = false;
        }

        private void UpdateMultiEmptyLabel()
        {
            if (lblMultiEmpty != null)
            {
                lblMultiEmpty.Visibility = MultiLaunchInstances.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void UpdateMultiLaunchAccounts()
        {
            var sortedNames = _settings.Profiles
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var inst in MultiLaunchInstances)
            {
                var prev = inst.SelectedAccount;

                inst.AvailableAccounts.Clear();
                foreach (var n in sortedNames)
                    inst.AvailableAccounts.Add(n);

                if (!string.IsNullOrEmpty(prev) && sortedNames.Contains(prev))
                {
                    inst.SelectedAccount = prev;
                }
                else if (sortedNames.Count > 0)
                {
                    inst.SelectedAccount = sortedNames[0];
                }
                else
                {
                    inst.SelectedAccount = "";
                }
            }
        }

        private void SaveMultiLaunchToSettings()
        {
            try
            {
                _settings.MultiLaunchAccounts = MultiLaunchInstances
                    .Select(i => i.SelectedAccount)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                _settings.Save();
            }
            catch { }
        }

        private void RestoreMultiLaunchFromSettings()
        {
            _restoringMultiLaunch = true;
            try
            {
                var validNames = _settings.Profiles
                    .Select(p => p.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var saved = _settings.MultiLaunchAccounts ?? new List<string>();

                var restoreList = saved
                    .Where(s => !string.IsNullOrEmpty(s) && validNames.Contains(s))
                    .Take(MaxMultiLaunchClients)
                    .ToList();

                if (restoreList.Count == 0 && _settings.Profiles.Count >= 1)
                {
                    restoreList.Add(_settings.SelectedProfile);
                }

                MultiLaunchInstances.Clear();

                foreach (var name in restoreList)
                {
                    var inst = new MultiLaunchInstance { SelectedAccount = name };

                    var sortedNames = _settings.Profiles
                        .Select(p => p.Name)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var n in sortedNames)
                        inst.AvailableAccounts.Add(n);

                    inst.PropertyChanged += (s, ev) =>
                    {
                        if (ev.PropertyName == nameof(MultiLaunchInstance.SelectedAccount))
                        {
                            SaveMultiLaunchToSettings();
                        }
                    };

                    inst.Index = MultiLaunchInstances.Count + 1;
                    MultiLaunchInstances.Add(inst);
                }

                SyncMultiCountBox();
                UpdateMultiEmptyLabel();
            }
            finally
            {
                _restoringMultiLaunch = false;
            }
        }

        private async void BtnModrinthSearch_Click(object sender, RoutedEventArgs e)
        {
            _searchDebounceTimer?.Stop();
            await ModrinthSearchAsync(txtModrinthSearch.Text.Trim());
        }

        private void TxtModrinthSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;

            _searchDebounceTimer?.Stop();
            _searchDebounceTimer?.Start();
        }

        private async void TxtModrinthSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _searchDebounceTimer?.Stop();
                await ModrinthSearchAsync(txtModrinthSearch.Text.Trim());
            }
        }

        private async Task ModrinthSearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                ModrinthResults.Clear();
                ModrinthVersions.Clear();
                lblModrinthStatus.Text = "";
                lblModrinthEmpty.Text = T("ModrinthEmpty");
                lblModrinthEmpty.Visibility = Visibility.Visible;
                return;
            }

            lblModrinthStatus.Text = T("Searching");
            ModrinthResults.Clear();
            ModrinthVersions.Clear();
            lblModrinthEmpty.Visibility = Visibility.Collapsed;

            try
            {
                var hits = await _modrinth.SearchAsync(query, "1.19.4", "fabric");

                foreach (var hit in hits)
                {
                    ModrinthResults.Add(new ModrinthItem
                    {
                        ProjectId = hit.ProjectId,
                        Slug = hit.Slug,
                        Title = hit.Title,
                        Description = hit.Description,
                        IconUrl = hit.IconUrl,
                        Author = hit.Author,
                        DownloadsText = FormatDownloads(hit.Downloads)
                    });
                }

                lblModrinthStatus.Text = $"{ModrinthResults.Count} {T("Results")}";
                lblModrinthEmpty.Visibility = ModrinthResults.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                lblModrinthStatus.Text = $"{T("Error")}: {ex.Message}";
            }
        }

        private static string FormatDownloads(int n)
        {
            if (n >= 1_000_000) return $"{n / 1_000_000.0:0.#}M";
            if (n >= 1_000) return $"{n / 1_000.0:0.#}k";
            return $"{n}";
        }

        private async void ModrinthResult_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not ModrinthItem item) return;

            lblModrinthStatus.Text = $"{item.Title} {T("LoadingVersions")}";
            ModrinthVersions.Clear();

            try
            {
                var versions = await _modrinth.GetVersionsAsync(item.ProjectId, "1.19.4", "fabric");

                foreach (var v in versions)
                {
                    ModrinthVersions.Add(v);
                }

                lblModrinthStatus.Text = $"{item.Title}: {ModrinthVersions.Count} {T("Versions")}";
            }
            catch (Exception ex)
            {
                lblModrinthStatus.Text = $"{T("Error")}: {ex.Message}";
            }
        }

        private async void BtnDownloadVersion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            if (el.Tag is not ModrinthVersion version) return;

            btnModrinthSearch.IsEnabled = false;

            var progress = new Progress<string>(msg => lblModrinthStatus.Text = msg);

            string? downloaded = await _modrinth.DownloadModAsync(version, _modsFolder, progress);

            if (downloaded != null)
            {
                lblModrinthStatus.Text = $"{T("Downloaded")}: {Path.GetFileName(downloaded)}";
                LoadMods();
            }
            else
            {
                lblModrinthStatus.Text = T("DownloadFailed");
            }

            btnModrinthSearch.IsEnabled = true;
        }

        private async Task<bool> EnsureJavaAsync()
        {
            if (!string.IsNullOrEmpty(_javaPath) && File.Exists(_javaPath))
            {
                return true;
            }

            _javaPath = JavaManager.FindJava();

            if (!string.IsNullOrEmpty(_javaPath) && File.Exists(_javaPath))
            {
                return true;
            }

            var result = MessageBox.Show(
                T("JavaNotFoundMessage"),
                T("JavaNotFound"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return false;
            }

            SetUIEnabled(false);
            lblStatus.Text = T("JavaDownloading");
            progressBar.Value = 0;

            var progress = new Progress<int>(pct =>
            {
                progressBar.Value = pct;
                lblPercent.Text = $"{pct}%";
            });

            var installedJava = await JavaManager.DownloadAndInstallAsync(progress);

            if (string.IsNullOrEmpty(installedJava) || !File.Exists(installedJava))
            {
                MessageBox.Show(
                    T("JavaInstallFailedMessage"),
                    T("JavaInstallFailed"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                lblStatus.Text = T("JavaInstallFailed");
                SetUIEnabled(true);
                return false;
            }

            _javaPath = installedJava;
            lblStatus.Text = T("JavaInstalled");

            progressBar.Value = 100;
            lblPercent.Text = "100%";

            SetUIEnabled(true);

            return true;
        }

        private void BtnCopySystemInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new System.Text.StringBuilder();

                sb.AppendLine("=== MVP Launcher System Info ===");
                sb.AppendLine($"Launcher version: {UpdateService.CurrentVersion}");
                sb.AppendLine($"OS: {Environment.OSVersion}");
                sb.AppendLine($"OS Architecture: {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
                sb.AppendLine($"Machine: {Environment.MachineName}");
                sb.AppendLine($"User: {Environment.UserName}");
                sb.AppendLine($".NET: {Environment.Version}");

                try
                {
                    long totalRamMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024L);
                    sb.AppendLine($"Total RAM: {totalRamMb} MB");
                }
                catch { }

                sb.AppendLine($"Java path: {_javaPath ?? "not found"}");

                if (!string.IsNullOrEmpty(_javaPath) && File.Exists(_javaPath))
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = _javaPath,
                            Arguments = "-version",
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            var output = proc.StandardError.ReadToEnd();
                            proc.WaitForExit(3000);
                            sb.AppendLine($"Java version: {output.Trim()}");
                        }
                    }
                    catch { }
                }

                sb.AppendLine($"Minecraft path: {_minecraftPath.BasePath}");
                sb.AppendLine($"Mods count: {AllMods.Count}");
                sb.AppendLine($"Selected profile: {_settings.SelectedProfile}");
                sb.AppendLine($"Theme: {_settings.Theme}");
                sb.AppendLine($"Language: {_settings.Language}");
                sb.AppendLine($"RAM: {_settings.RamMb} MB");
                sb.AppendLine("=== End ===");

                Clipboard.SetText(sb.ToString());
                lblStatus.Text = T("AboutSystemInfoCopied");
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"{T("Error")}: {ex.Message}";
            }
        }

        private async Task<Process?> CreateAndStartGameAsync(
            string username,
            string accountName,
            int ramMb,
            string fabricVersionName,
            bool attachExitHandler,
            string behavior)
        {
            var launcher = new MinecraftLauncher(_minecraftPath);

            launcher.FileProgressChanged += (s, args) =>
            {
                double pct = args.TotalTasks > 0
                    ? (double)args.ProgressedTasks / args.TotalTasks * 100.0
                    : 0;

                Dispatcher.Invoke(() =>
                {
                    progressBar.Value = pct;
                    lblPercent.Text = $"{pct:0}%";
                    lblStatus.Text = string.IsNullOrEmpty(args.Name)
                        ? T("Downloading")
                        : $"{T("Downloading")}: {args.Name}";
                });
            };

            MSession? session = null;

            var selectedProfile = _settings.Profiles.FirstOrDefault(p => p.Name == accountName);

            if (selectedProfile != null && selectedProfile.Type == "Microsoft" && _loginHandler != null)
            {
                try
                {
                    var accounts = _loginHandler.AccountManager.GetAccounts();
                    var targetAccount = accounts.FirstOrDefault(a => a.Identifier == selectedProfile.Uuid);
                    if (targetAccount != null)
                        session = await _loginHandler.Authenticate(targetAccount);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiLaunch] Auth hiba ({accountName}): {ex.Message}");
                }
            }

            if (session == null)
                session = MSession.CreateOfflineSession(username);

            string javaPathForLaunch = _javaPath ?? "";
            if (!string.IsNullOrEmpty(javaPathForLaunch) &&
                javaPathForLaunch.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))
            {
                var javawPath = javaPathForLaunch.Substring(0, javaPathForLaunch.Length - 8) + "javaw.exe";
                if (File.Exists(javawPath))
                    javaPathForLaunch = javawPath;
            }

            var jvmArgs = BuildJavaArguments();
            jvmArgs.Add($"-Dfabric.addMods={_cacheManager.CacheFolderPath}");

            var mArgs = new MArgument[jvmArgs.Count];
            for (int i = 0; i < jvmArgs.Count; i++)
                mArgs[i] = new MArgument(jvmArgs[i]);

            var launchOption = new MLaunchOption
            {
                MaximumRamMb = ramMb,
                Session = session,
                JavaPath = javaPathForLaunch,
                ExtraJvmArguments = mArgs
            };

            var process = await launcher.CreateProcessAsync(fabricVersionName, launchOption);

            try
            {
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process.StartInfo.UseShellExecute = false;
            }
            catch { }

            if (attachExitHandler)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (s2, ev) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _cacheManager.ClearCache();

                        if (behavior == "Close")
                        {
                            Application.Current.Shutdown();
                        }
                        else if (behavior == "Hide")
                        {
                            Show();
                            WindowState = WindowState.Normal;
                            Activate();
                            lblStatus.Text = T("GameClosed");
                            SetUIEnabled(true);
                        }
                        else
                        {
                            lblStatus.Text = T("GameClosed");
                            SetUIEnabled(true);
                        }
                    });
                };
            }

            process.Start();
            return process;
        }

        private async void BtnLaunchMultiple_Click(object sender, RoutedEventArgs e)
        {
            var instances = MultiLaunchInstances
                .Where(i => !string.IsNullOrEmpty(i.SelectedAccount))
                .ToList();

            if (instances.Count == 0)
            {
                MessageBox.Show(T("MultiLaunchNoInstances"),
                    T("Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int ramMb = (int)sldRam.Value;

            SetUIEnabled(false);
            btnLaunchMultiple.IsEnabled = false;
            progressBar.Value = 0;
            lblPercent.Text = "0%";
            lblStatus.Text = T("Preparing");
            lblGameStartupTime.Text = "";

            var gameStartupSw = Stopwatch.StartNew();

            try
            {
                if (!await EnsureJavaAsync())
                {
                    lblStatus.Text = T("LaunchCancelled");
                    SetUIEnabled(true);
                    btnLaunchMultiple.IsEnabled = true;
                    return;
                }

                var cacheProgress = new Progress<string>(msg =>
                {
                    lblStatus.Text = msg;
                    System.Diagnostics.Debug.WriteLine($"[CacheMod] {msg}");
                });

                bool cacheOk = await _cacheManager.PrepareAsync(cacheProgress);

                if (!cacheOk)
                {
                    var result = MessageBox.Show(
                        T("CacheModFailed"),
                        T("CacheModError"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.No)
                    {
                        lblStatus.Text = T("LaunchCancelled");
                        SetUIEnabled(true);
                        btnLaunchMultiple.IsEnabled = true;
                        return;
                    }
                }

                lblStatus.Text = T("InstallingFabric");
                var fabricInstaller = new FabricInstaller(new HttpClient());
                string fabricVersionName = await fabricInstaller.Install("1.19.4", _minecraftPath);

                EnsureDefaultServerExists();

                int launched = 0;

                foreach (var inst in instances)
                {
                    try
                    {
                        lblStatus.Text = string.Format(T("MultiLaunchLaunching"), inst.SelectedAccount);

                        await CreateAndStartGameAsync(
                            inst.SelectedAccount,
                            inst.SelectedAccount,
                            ramMb,
                            fabricVersionName,
                            attachExitHandler: false,
                            behavior: "Nothing");

                        launched++;

                        await Task.Delay(1500);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MultiLaunch] '{inst.SelectedAccount}' indítási hiba: {ex.Message}");
                    }
                }

                gameStartupSw.Stop();
                lblGameStartupTime.Text =
                    $"{T("GameStartupTime")}: {gameStartupSw.Elapsed.TotalSeconds:0.00} s";

                lblStatus.Text = string.Format(T("MultiLaunchClientStarted"), launched);
                progressBar.Value = 100;
                lblPercent.Text = "100%";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{T("LaunchError")}:\n{ex.Message}",
                    T("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = T("LaunchError");
            }
            finally
            {
                SetUIEnabled(true);
                btnLaunchMultiple.IsEnabled = true;
            }
        }

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            string username = _settings.SelectedProfile;

            if (string.IsNullOrEmpty(username))
            {
                MessageBox.Show(T("ChooseProfile"),
                    T("MissingName"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int ramMb = (int)sldRam.Value;

            SetUIEnabled(false);
            progressBar.Value = 0;
            lblPercent.Text = "0%";
            lblStatus.Text = T("Preparing");
            lblGameStartupTime.Text = "";

            var gameStartupSw = Stopwatch.StartNew();

            try
            {
                if (!await EnsureJavaAsync())
                {
                    lblStatus.Text = T("LaunchCancelled");
                    SetUIEnabled(true);
                    return;
                }

                var cacheProgress = new Progress<string>(msg =>
                {
                    lblStatus.Text = msg;
                    System.Diagnostics.Debug.WriteLine($"[CacheMod] {msg}");
                });

                bool cacheOk = await _cacheManager.PrepareAsync(cacheProgress);

                if (!cacheOk)
                {
                    var result = MessageBox.Show(
                        T("CacheModFailed"),
                        T("CacheModError"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.No)
                    {
                        lblStatus.Text = T("LaunchCancelled");
                        SetUIEnabled(true);
                        return;
                    }
                }

                lblStatus.Text = T("InstallingFabric");
                var fabricInstaller = new FabricInstaller(new HttpClient());
                string fabricVersionName = await fabricInstaller.Install("1.19.4", _minecraftPath);

                lblStatus.Text = T("LoggingIn");

                EnsureDefaultServerExists();

                string behavior = _settings.LauncherBehavior;

                var selectedProfile = _settings.Profiles.FirstOrDefault(p => p.Name == username);

                MSession? session = null;

                if (selectedProfile != null && selectedProfile.Type == "Microsoft" && _loginHandler != null)
                {
                    try
                    {
                        var accounts = _loginHandler.AccountManager.GetAccounts();
                        var targetAccount = accounts.FirstOrDefault(a => a.Identifier == selectedProfile.Uuid);

                        if (targetAccount != null)
                        {
                            session = await _loginHandler.Authenticate(targetAccount);
                        }

                        if (session == null)
                        {
                            session = await _loginHandler.Authenticate();
                        }
                    }
                    catch (Exception ex)
                    {
                        var result = MessageBox.Show(
                            $"{T("MicrosoftSessionError")}:\n{ex.Message}\n\n{T("ContinueOffline")}",
                            T("LoginError"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (result == MessageBoxResult.No)
                        {
                            lblStatus.Text = T("LaunchCancelled");
                            SetUIEnabled(true);
                            return;
                        }
                    }
                }

                if (session == null)
                {
                    session = MSession.CreateOfflineSession(username);
                }

                lblStatus.Text = T("LaunchingGame");

                string javaPathForLaunch = _javaPath ?? "";
                if (!string.IsNullOrEmpty(javaPathForLaunch) &&
                    javaPathForLaunch.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var javawPath = javaPathForLaunch.Substring(0, javaPathForLaunch.Length - 8) + "javaw.exe";
                    if (File.Exists(javawPath))
                    {
                        javaPathForLaunch = javawPath;
                    }
                }

                var jvmArgs = BuildJavaArguments();
                jvmArgs.Add($"-Dfabric.addMods={_cacheManager.CacheFolderPath}");

                var mArgs = new MArgument[jvmArgs.Count];
                for (int i = 0; i < jvmArgs.Count; i++)
                {
                    mArgs[i] = new MArgument(jvmArgs[i]);
                }

                var launchOption = new MLaunchOption
                {
                    MaximumRamMb = ramMb,
                    Session = session,
                    JavaPath = javaPathForLaunch,
                    ExtraJvmArguments = mArgs
                };

                var launcher = new MinecraftLauncher(_minecraftPath);

                launcher.FileProgressChanged += (s, args) =>
                {
                    double pct = args.TotalTasks > 0
                        ? (double)args.ProgressedTasks / args.TotalTasks * 100.0
                        : 0;

                    Dispatcher.Invoke(() =>
                    {
                        progressBar.Value = pct;
                        lblPercent.Text = $"{pct:0}%";
                        lblStatus.Text = string.IsNullOrEmpty(args.Name)
                            ? T("Downloading")
                            : $"{T("Downloading")}: {args.Name}";
                    });
                };

                var process = await launcher.CreateProcessAsync(fabricVersionName, launchOption);
                process.EnableRaisingEvents = true;

                try
                {
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    process.StartInfo.UseShellExecute = false;
                }
                catch { }

                process.Exited += (s2, ev) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _cacheManager.ClearCache();

                        if (behavior == "Close")
                        {
                            Application.Current.Shutdown();
                        }
                        else if (behavior == "Hide")
                        {
                            Show();
                            WindowState = WindowState.Normal;
                            Activate();
                            lblStatus.Text = T("GameClosed");
                            SetUIEnabled(true);
                        }
                        else
                        {
                            lblStatus.Text = T("GameClosed");
                            SetUIEnabled(true);
                        }
                    });
                };

                process.Start();

                gameStartupSw.Stop();
                lblGameStartupTime.Text = $"{T("GameStartupTime")}: {gameStartupSw.Elapsed.TotalSeconds:0.00} s";

                lblStatus.Text = T("GameLaunched");
                progressBar.Value = 100;
                lblPercent.Text = "100%";

                if (behavior == "Close" || behavior == "Hide")
                {
                    Hide();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{T("LaunchError")}:\n{ex.Message}",
                    T("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = T("LaunchError");
                SetUIEnabled(true);
            }
        }

        private void SetUIEnabled(bool enabled)
        {
            btnPlay.IsEnabled = enabled;
            btnToggleMods.IsEnabled = enabled;
            btnSettings.IsEnabled = enabled;
            cmbProfiles.IsEnabled = enabled;
            btnAddProfile.IsEnabled = enabled;
            btnAddMicrosoft.IsEnabled = enabled;
            btnModrinthSearch.IsEnabled = enabled;
            btnCheckUpdate.IsEnabled = enabled;
            if (btnDeleteProfile != null)
                btnDeleteProfile.IsEnabled = enabled;
            if (btnLaunchMultiple != null)
                btnLaunchMultiple.IsEnabled = enabled;
        }
    }
}