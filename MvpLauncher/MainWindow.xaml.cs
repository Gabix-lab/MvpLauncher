using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ProcessBuilder;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Velopack;
using Velopack.Sources;

namespace MvpLauncher
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private const int MaxMultiLaunchClients = 10;
        private const string SingleInstanceName = "Single Launch";
        private const string MultiInstanceName = "Multi Launch";

        public static readonly string AppVersion =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        private const string GithubRepoUrl =
            "https://github.com/Gabix-lab/MvpLauncher";

        private MinecraftPath _minecraftPath;
        private string _modsFolder;
        private CacheModManager _cacheManager;
        private AppSettings _settings;
        private bool _isUpdatingMods;
        private bool _isLoadingProfiles;
        private bool _isLoadingSettings;
        private bool _updatingMultiCount;
        private bool _restoringMultiLaunch;
        private bool _applyingInstance;
        private bool _isLoadingInstanceSelector = true;
        private bool _isClosed;
        private bool _viewTransitioning;
        private bool _marcsiMode;
        private JELoginHandler? _loginHandler;
        private System.Windows.Threading.DispatcherTimer? _updateTimer;
        private System.Windows.Threading.DispatcherTimer? _searchDebounceTimer;
        private string? _javaPath;
        private UpdateManager? _velopack;
        private string? _cachedFabricVersionName;
        private string? _cachedAssetsVersionName;

        public ICollectionView FilteredMods { get; }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value) return;

                if (!string.IsNullOrEmpty(value) &&
                    value.Trim().Equals("marcsi", StringComparison.OrdinalIgnoreCase) &&
                    !_marcsiMode)
                {
                    _marcsiMode = true;
                    _searchText = "";
                    OnPropertyChanged(nameof(SearchText));

                    foreach (var mod in AllMods)
                    {
                        mod.IsLocked = false;
                        mod.LockReason = null;
                    }

                    FilteredMods?.Refresh();
                    RefreshModCategories();

                    if (lblStatus != null)
                        lblStatus.Text = "Marcsi mód aktiválva – minden mod kikapcsolható!";
                    return;
                }

                _searchText = value ?? "";
                OnPropertyChanged();
                FilteredMods?.Refresh();
                RefreshModCategories();
            }
        }

        private ModFilter _selectedModFilter = ModFilter.All;
        public ModFilter SelectedModFilter
        {
            get => _selectedModFilter;
            set
            {
                if (_selectedModFilter == value) return;
                _selectedModFilter = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterEnabled));
                OnPropertyChanged(nameof(IsFilterMandatory));
                OnPropertyChanged(nameof(IsFilterDisabled));
                FilteredMods?.Refresh();
            }
        }

        public bool IsFilterAll
        {
            get => SelectedModFilter == ModFilter.All;
            set { if (value) SelectedModFilter = ModFilter.All; }
        }

        public bool IsFilterEnabled
        {
            get => SelectedModFilter == ModFilter.Enabled;
            set { if (value) SelectedModFilter = ModFilter.Enabled; }
        }

        public bool IsFilterMandatory
        {
            get => SelectedModFilter == ModFilter.Mandatory;
            set { if (value) SelectedModFilter = ModFilter.Mandatory; }
        }

        public bool IsFilterDisabled
        {
            get => SelectedModFilter == ModFilter.Disabled;
            set { if (value) SelectedModFilter = ModFilter.Disabled; }
        }

        public enum ModFilter { All, Enabled, Mandatory, Disabled }

        private readonly ModrinthService _modrinth = new();

        public ObservableCollection<ModItem> AllMods { get; } = new();
        public ObservableCollection<ModItem> EnabledMods { get; } = new();
        public ObservableCollection<ModItem> MandatoryMods { get; } = new();
        public ObservableCollection<ModItem> DisabledMods { get; } = new();

        public ObservableCollection<ModrinthItem> ModrinthResults { get; } = new();
        public ObservableCollection<ModrinthVersion> ModrinthVersions { get; } = new();

        private ModrinthItem? _selectedModrinthItem;
        private int _versionLoadToken;

        public ModrinthItem? SelectedModrinthItem
        {
            get => _selectedModrinthItem;
            set
            {
                if (ReferenceEquals(_selectedModrinthItem, value)) return;
                _selectedModrinthItem = value;
                OnPropertyChanged();

                if (value != null)
                    _ = LoadVersionsForSelectedAsync(value);
                else
                    ModrinthVersions?.Clear();
            }
        }

        private async Task LoadVersionsForSelectedAsync(ModrinthItem item)
        {
            int token = ++_versionLoadToken;
            if (lblModrinthStatus != null)
                lblModrinthStatus.Text = $"{item.Title} verzióinak betöltése...";
            ModrinthVersions.Clear();

            try
            {
                var versions = await _modrinth.GetVersionsAsync(item.ProjectId, "1.19.4", "fabric");
                if (token != _versionLoadToken) return;
                foreach (var v in versions) ModrinthVersions.Add(v);
                if (lblModrinthStatus != null)
                    lblModrinthStatus.Text = $"{item.Title}: {ModrinthVersions.Count} verzió";
            }
            catch (Exception ex)
            {
                if (token != _versionLoadToken) return;
                if (lblModrinthStatus != null)
                    lblModrinthStatus.Text = $"Hiba: {ex.Message}";
            }
        }

        public ObservableCollection<MultiLaunchInstance> MultiLaunchInstances { get; } = new();

        public ObservableCollection<InstanceProfile> InstanceProfiles => _settings.InstanceProfiles;

        public event PropertyChangedEventHandler? PropertyChanged;

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
            _settings = AppSettings.Load();

            InitializeComponent();

            Opacity = 0;
            IsVisibleChanged += MainWindow_IsVisibleChanged;

            FilteredMods = CollectionViewSource.GetDefaultView(AllMods);
            FilteredMods.Filter = FilterMods;
            FilteredMods.SortDescriptions.Add(new SortDescription(nameof(ModItem.DisplayName), ListSortDirection.Ascending));

            DataContext = this;

            string customPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".mvplauncher");
            _minecraftPath = new MinecraftPath(customPath);
            _modsFolder = Path.Combine(_minecraftPath.BasePath, "mods");

            _cacheManager = new CacheModManager(
                Path.Combine(Path.GetTempPath(), "mvplauncher-cache"));

            _cacheManager.Mods.Add(new CacheMod
            {
                FileName = "mvp.jar",
                GitHubRepo = "Gabix-lab/Actionbar-Snapshot"
            });

            _cacheManager.VersionChanged += (s, e) =>
            {
                _ = Dispatcher.InvokeAsync(UpdateCachedModVersion);
            };

            try
            {
                _loginHandler = JELoginHandlerBuilder.BuildDefault();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Microsoft] Init hiba: {ex.Message}");
                _loginHandler = null;
            }

            _javaPath = JavaManager.FindJava();

            JavaManager.JavaReady += (s, path) =>
            {
                if (!string.IsNullOrEmpty(path))
                {
                    _ = Dispatcher.InvokeAsync(() => _javaPath = path);
                }
            };

            JavaManager.InstallProgress += (s, pct) =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    SetProgress(pct);
                    if (JavaManager.IsInstalling && lblStatus != null)
                        lblStatus.Text = $"Java letöltése... ({pct}%)";
                });
            };

            JavaManager.InstallStateChanged += (s, running) =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (lblStatus != null && running && string.IsNullOrEmpty(_javaPath))
                        lblStatus.Text = "Java 17 automatikus előkészítése...";
                });
            };

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

            long totalRamMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024L);
            sldRam.Maximum = Math.Max(totalRamMb, 2048);

            LoadMods();
            LoadProfiles();
            LoadSettingsIntoUI();
            EnsureDefaultInstances();

            Loaded += async (s, e) =>
            {
                try
                {
                    App.StartupStopwatch.Stop();
                    if (lblStartupTime != null)
                        lblStartupTime.Text = $"Indulási idő: {App.StartupStopwatch.ElapsedMilliseconds} ms";
                    if (lblHeaderVersion != null)
                        lblHeaderVersion.Text = $"v{AppVersion}";
                    if (lblAboutVersion != null)
                    {
                        lblAboutVersion.Text = $"Verzió: {AppVersion}";
                    }

                    JavaManager.StartBackgroundInstallIfNeeded();

                    LoadSettingsIntoUI();
                    UpdateSkin();
                    RestoreMultiLaunchFromSettings();

                    _ = CheckForUpdatesAsync(showPopup: false);

                    StartUpdateTimer();

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Loaded] Hiba: {ex}");
                    if (lblStatus != null)
                        lblStatus.Text = $"Hiba: {ex.Message}";
                }
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    await _cacheManager.FetchVersionAsync();
                    await Dispatcher.InvokeAsync(UpdateCachedModVersion);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CacheMod] FetchVersion hiba: {ex.Message}");
                }
            });

            Closing += (s, e) =>
            {
                _isClosed = true;
                try
                {
                    SaveCurrentSettings();
                    SaveMultiLaunchToSettings();
                    _updateTimer?.Stop();
                    _searchDebounceTimer?.Stop();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Closing] Hiba: {ex.Message}");
                }
            };
        }

        private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool visible && visible)
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(220),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
        }

        private void SetProgress(double pct, bool smooth = true)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;

            if (lblPercent != null)
                lblPercent.Text = $"{(int)pct}%";

            if (progressBar == null) return;

            if (smooth)
            {
                progressBar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation
                {
                    To = pct,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            else
            {
                progressBar.BeginAnimation(RangeBase.ValueProperty, null);
                progressBar.Value = pct;
            }
        }

        private UIElement? GetVisibleView()
        {
            if (MainView?.Visibility == Visibility.Visible) return MainView;
            if (ModsView?.Visibility == Visibility.Visible) return ModsView;
            if (SettingsView?.Visibility == Visibility.Visible) return SettingsView;
            return null;
        }

        private async Task ShowOnlyAsync(UIElement target)
        {
            if (_viewTransitioning) return;
            var previous = GetVisibleView();
            if (previous == target) return;

            _viewTransitioning = true;
            try
            {
                if (previous != null)
                {
                    var tcs = new TaskCompletionSource();
                    var fadeOut = new DoubleAnimation
                    {
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(130),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                    };
                    fadeOut.Completed += (s, e) => tcs.TrySetResult();
                    previous.BeginAnimation(OpacityProperty, fadeOut);
                    await Task.WhenAny(tcs.Task, Task.Delay(200));
                    previous.Visibility = Visibility.Collapsed;
                    previous.BeginAnimation(OpacityProperty, null);
                    previous.Opacity = 1;
                }

                target.Opacity = 0;
                var translate = new TranslateTransform(0, 14);
                target.RenderTransform = translate;
                target.Visibility = Visibility.Visible;

                target.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(240),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
                {
                    From = 14,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(280),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            finally
            {
                _viewTransitioning = false;
            }
        }

        private void UpdateCachedModVersion()
        {
            var cacheItem = AllMods.FirstOrDefault(m => m.IsFromCache);
            if (cacheItem != null && !string.IsNullOrEmpty(_cacheManager.CurrentVersion))
            {
                cacheItem.Version = _cacheManager.CurrentVersion;
            }
        }

        private bool FilterMods(object obj)
        {
            if (obj is not ModItem mod) return false;

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                if (!mod.DisplayName.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            switch (_selectedModFilter)
            {
                case ModFilter.Enabled:
                    return mod.IsEnabled && !mod.IsLocked && !mod.IsFromCache;
                case ModFilter.Mandatory:
                    return mod.IsLocked || mod.IsFromCache;
                case ModFilter.Disabled:
                    return !mod.IsEnabled && !mod.IsLocked && !mod.IsFromCache;
                default:
                    return true;
            }
        }

        private string GetFabricMarkerPath()
        {
            string basePath = _minecraftPath.BasePath ?? "";
            return Path.Combine(basePath, "fabric-version.marker");
        }

        private string GetAssetsMarkerPath()
        {
            string basePath = _minecraftPath.BasePath ?? "";
            return Path.Combine(basePath, "assets-checked.marker");
        }

        private async Task<string> GetOrInstallFabricVersionAsync()
        {
            if (!string.IsNullOrEmpty(_cachedFabricVersionName))
                return _cachedFabricVersionName!;

            string markerPath = GetFabricMarkerPath();

            try
            {
                if (File.Exists(markerPath))
                {
                    var cached = File.ReadAllText(markerPath).Trim();
                    if (!string.IsNullOrEmpty(cached))
                    {
                        string versionsBase = _minecraftPath.BasePath ?? "";
                        var versionsDir = Path.Combine(versionsBase, "versions", cached);
                        if (Directory.Exists(versionsDir))
                        {
                            _cachedFabricVersionName = cached;
                            return cached;
                        }
                    }
                }
            }
            catch { }

            var fabricInstaller = new FabricInstaller(new HttpClient());
            var versionName = await fabricInstaller.Install("1.19.4", _minecraftPath);

            try
            {
                string? dir = Path.GetDirectoryName(markerPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(markerPath, versionName);
            }
            catch { }

            _cachedFabricVersionName = versionName;
            return versionName;
        }

        private bool ShouldSkipAssetProgress(string versionName)
        {
            if (string.Equals(_cachedAssetsVersionName, versionName, StringComparison.OrdinalIgnoreCase))
                return true;

            string markerPath = GetAssetsMarkerPath();

            try
            {
                if (File.Exists(markerPath))
                {
                    var cached = File.ReadAllText(markerPath).Trim();
                    if (string.Equals(cached, versionName, StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedAssetsVersionName = cached;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private void MarkAssetsChecked(string versionName)
        {
            _cachedAssetsVersionName = versionName;
            string markerPath = GetAssetsMarkerPath();

            try
            {
                string? dir = Path.GetDirectoryName(markerPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(markerPath, versionName);
            }
            catch { }
        }

        private void EnsureDefaultInstances()
        {
            var toRemove = _settings.InstanceProfiles
                .Where(p => p.Name != SingleInstanceName && p.Name != MultiInstanceName)
                .ToList();
            foreach (var p in toRemove)
            {
                _settings.InstanceProfiles.Remove(p);
            }

            var currentEnabled = AllMods
                .Where(m => m.IsEnabled && !m.IsMandatory && !m.IsFromCache)
                .Select(m => m.DisplayName)
                .ToList();

            if (!_settings.InstanceProfiles.Any(p => p.Name == SingleInstanceName))
            {
                _settings.InstanceProfiles.Add(new InstanceProfile
                {
                    Name = SingleInstanceName,
                    EnabledMods = new List<string>(currentEnabled)
                });
            }

            if (!_settings.InstanceProfiles.Any(p => p.Name == MultiInstanceName))
            {
                _settings.InstanceProfiles.Add(new InstanceProfile
                {
                    Name = MultiInstanceName,
                    EnabledMods = new List<string>(currentEnabled)
                });
            }

            if (_settings.SelectedInstanceProfile != SingleInstanceName &&
                _settings.SelectedInstanceProfile != MultiInstanceName)
            {
                _settings.SelectedInstanceProfile = SingleInstanceName;
            }

            _settings.Save();

            _isLoadingInstanceSelector = true;
            cmbInstanceSelector.SelectedIndex =
                _settings.SelectedInstanceProfile == MultiInstanceName ? 1 : 0;
            _isLoadingInstanceSelector = false;

            UpdateCurrentInstanceLabel();

            var profile = _settings.InstanceProfiles
                .First(p => p.Name == _settings.SelectedInstanceProfile);
            ApplyInstanceProfile(profile);
        }

        private void UpdateCurrentInstanceLabel()
        {
            if (lblCurrentInstance != null)
            {
                lblCurrentInstance.Text = _settings.SelectedInstanceProfile == MultiInstanceName
                    ? "Multi Launch"
                    : "Single Launch";
            }
        }

        private void SwitchToInstance(string instanceName)
        {
            if (_settings.SelectedInstanceProfile == instanceName) return;

            var profile = _settings.InstanceProfiles
                .FirstOrDefault(p => p.Name == instanceName);
            if (profile == null) return;

            SyncCurrentInstanceFromMods();

            _settings.SelectedInstanceProfile = instanceName;
            _settings.Save();

            _isLoadingInstanceSelector = true;
            cmbInstanceSelector.SelectedIndex =
                instanceName == MultiInstanceName ? 1 : 0;
            _isLoadingInstanceSelector = false;

            UpdateCurrentInstanceLabel();
            ApplyInstanceProfile(profile);
        }

        private void CmbInstanceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_isLoadingInstanceSelector) return;
            if (cmbInstanceSelector.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag) return;

            string newProfileName = tag == "Multi" ? MultiInstanceName : SingleInstanceName;

            if (_settings.SelectedInstanceProfile == newProfileName) return;

            SyncCurrentInstanceFromMods();

            _settings.SelectedInstanceProfile = newProfileName;
            _settings.Save();

            UpdateCurrentInstanceLabel();

            var profile = _settings.InstanceProfiles
                .FirstOrDefault(p => p.Name == newProfileName);
            if (profile != null)
            {
                ApplyInstanceProfile(profile);
            }
        }

        private void ApplyInstanceProfile(InstanceProfile profile)
        {
            if (_applyingInstance) return;
            _applyingInstance = true;
            _isUpdatingMods = true;
            try
            {
                var enabledSet = new HashSet<string>(
                    profile.EnabledMods ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var mod in AllMods)
                {
                    if (mod.IsMandatory || mod.IsFromCache) continue;
                    if (enabledSet.Contains(mod.DisplayName)) continue;
                    if (!mod.IsEnabled) continue;

                    bool wasLocked = mod.IsLocked;
                    mod.IsLocked = false;
                    mod.IsEnabled = false;
                    mod.IsLocked = wasLocked;
                }

                UpdateLocks();

                foreach (var mod in AllMods)
                {
                    if (mod.IsMandatory || mod.IsFromCache) continue;
                    if (!enabledSet.Contains(mod.DisplayName)) continue;
                    if (mod.IsEnabled) continue;

                    mod.IsLocked = false;
                    mod.IsEnabled = true;
                    EnableDependenciesRecursive(mod, new HashSet<ModItem>());
                }

                UpdateLocks();
                RefreshModCategories();
            }
            finally
            {
                _isUpdatingMods = false;
                _applyingInstance = false;
            }
        }

        private void SyncCurrentInstanceFromMods()
        {
            if (_applyingInstance) return;

            var profile = _settings.InstanceProfiles
                .FirstOrDefault(p => p.Name == _settings.SelectedInstanceProfile);
            if (profile == null) return;

            profile.EnabledMods = AllMods
                .Where(m => m.IsEnabled && !m.IsMandatory && !m.IsFromCache)
                .Select(m => m.DisplayName)
                .ToList();

            _settings.Save();
        }

        private void AddModsToCurrentInstance(IEnumerable<string> modNames)
        {
            var profile = _settings.InstanceProfiles
                .FirstOrDefault(p => p.Name == _settings.SelectedInstanceProfile);
            if (profile == null) return;

            profile.EnabledMods ??= new List<string>();

            foreach (var name in modNames)
            {
                if (!profile.EnabledMods.Any(n =>
                    string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    profile.EnabledMods.Add(name);
                }
            }

            _settings.Save();
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

            var profile = _settings.InstanceProfiles
                .FirstOrDefault(p => p.Name == _settings.SelectedInstanceProfile);
            if (profile != null)
            {
                ApplyInstanceProfile(profile);
            }

            FilteredMods?.Refresh();
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

            bool isMandatory = actualBase.StartsWith("fabric-api-", StringComparison.OrdinalIgnoreCase) ||
                               actualBase.StartsWith("satin-", StringComparison.OrdinalIgnoreCase);

            if (isMandatory)
            {
                modItem.IsMandatory = true;
                modItem.IsLocked = true;

                if (!enabled)
                {
                    try
                    {
                        string disabledPath = fullPath + ".disabled";
                        if (File.Exists(disabledPath))
                        {
                            File.Move(disabledPath, fullPath, overwrite: true);
                            modItem.IsEnabled = true;
                        }
                    }
                    catch { }
                }
            }

            var info = FabricMetadataReader.Read(File.Exists(fullPath) ? fullPath : path);
            if (info != null)
            {
                modItem.ModId = info.Id;
                modItem.Version = info.Version;
                modItem.Author = info.Author;
                modItem.Description = info.Description;
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
            if (_marcsiMode)
            {
                foreach (var mod in AllMods)
                {
                    mod.IsLocked = false;
                    mod.LockReason = null;
                }
                FilteredMods?.Refresh();
                return;
            }

            foreach (var mod in AllMods)
            {
                if (mod.IsFromCache)
                {
                    mod.IsLocked = true;
                    mod.LockReason = "Cache mod — automatikusan töltődik, nem kapcsolható ki.";
                    continue;
                }

                if (mod.IsMandatory)
                {
                    mod.IsLocked = true;
                    mod.LockReason = "Kötelező mod — a játék működéséhez szükséges, nem kapcsolható ki.";
                    continue;
                }

                var enabledDependents = new List<string>();
                foreach (var dependentName in mod.Dependents)
                {
                    var dependent = AllMods.FirstOrDefault(m => m.DisplayName == dependentName);
                    if (dependent != null && dependent.IsEnabled)
                    {
                        enabledDependents.Add(dependent.DisplayName);
                    }
                }

                if (enabledDependents.Count > 0)
                {
                    mod.IsLocked = true;
                    mod.LockReason = $"Ezek a modok igénylik, ezért nem kapcsolható ki: {string.Join(", ", enabledDependents)}";
                }
                else
                {
                    mod.IsLocked = false;
                    mod.LockReason = null;
                }
            }
            FilteredMods?.Refresh();
        }

        private void RefreshModCategories()
        {
            Dispatcher.BeginInvoke(new Action(DoRefreshModCategories),
                System.Windows.Threading.DispatcherPriority.Normal);
        }

        private void DoRefreshModCategories()
        {
            var filtered = AllMods
                .Where(m => string.IsNullOrWhiteSpace(_searchText) ||
                            m.DisplayName.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.DisplayName)
                .ToList();

            var mandatory = filtered.Where(m => m.IsLocked || m.IsFromCache).ToList();
            var enabled = filtered.Where(m => !m.IsLocked && !m.IsFromCache && m.IsEnabled).ToList();
            var disabled = filtered.Where(m => !m.IsLocked && !m.IsFromCache && !m.IsEnabled).ToList();

            SyncCollection(MandatoryMods, mandatory);
            SyncCollection(EnabledMods, enabled);
            SyncCollection(DisabledMods, disabled);
        }

        private static void SyncCollection(ObservableCollection<ModItem> target, List<ModItem> desired)
        {
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(target[i]))
                {
                    target.RemoveAt(i);
                }
            }

            for (int i = 0; i < desired.Count; i++)
            {
                if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                    continue;

                int existing = target.IndexOf(desired[i]);
                if (existing >= 0)
                {
                    target.Move(existing, i);
                }
                else
                {
                    target.Insert(i, desired[i]);
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
                SyncCurrentInstanceFromMods();
                RefreshModCategories();
            }
            finally
            {
                _isUpdatingMods = false;
            }
        }

        private void ModItemToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb) return;
            if (cb.DataContext is not ModItem mod) return;
            e.Handled = true;

            if (mod.IsFromCache && !_marcsiMode && mod.IsEnabled)
            {
                if (lblStatus != null)
                    lblStatus.Text = mod.LockReason ?? "Ez a mod nem kapcsolható ki.";
                cb.IsChecked = mod.IsEnabled;
                return;
            }

            if (mod.IsLocked && !_marcsiMode)
            {
                if (lblStatus != null)
                    lblStatus.Text = mod.LockReason ?? "Ez a mod nem kapcsolható ki.";
                cb.IsChecked = mod.IsEnabled;
                return;
            }

            if (!mod.TryToggleEnabled(out string? error))
            {
                cb.IsChecked = mod.IsEnabled;

                if (!string.IsNullOrEmpty(error))
                {
                    MessageBox.Show(error, "Mod állapotváltás sikertelen",
                        MessageBoxButton.OK, MessageBoxImage.Warning);

                    if (lblStatus != null)
                        lblStatus.Text = "A mod állapotát nem sikerült megváltoztatni.";
                }
                return;
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
                    if (imgSkin != null) imgSkin.Source = null;
                    if (lblSkinLoading != null) lblSkinLoading.Visibility = Visibility.Visible;
                    return;
                }

                if (lblSkinLoading != null) lblSkinLoading.Visibility = Visibility.Collapsed;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(SkinService.GetBodyUrl(identifier), UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();

                if (imgSkin != null) imgSkin.Source = bitmap;
            }
            catch
            {
                if (imgSkin != null) imgSkin.Source = null;
                if (lblSkinLoading != null) lblSkinLoading.Visibility = Visibility.Visible;
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
                    Debug.WriteLine($"[Microsoft] LoadProfiles hiba: {ex.Message}");
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
                if (lblNewProfileError != null) lblNewProfileError.Text = "Adj meg egy nevet!";
                return;
            }

            if (name.Length > 16)
            {
                if (lblNewProfileError != null) lblNewProfileError.Text = "A név maximum 16 karakter lehet!";
                return;
            }

            if (_settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                if (lblNewProfileError != null) lblNewProfileError.Text = "Ez a név már létezik!";
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
                        bool removed = TryRemoveAccount(_loginHandler.AccountManager, toRemove);
                        if (!removed)
                        {
                            MessageBox.Show(
                                "A Microsoft fiókot nem sikerült eltávolítani a bejelentkezett fiókok közül.\n" +
                                "A profil törlése megszakítva, különben a következő indításnál újra megjelenne.",
                                "Törlés sikertelen", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Microsoft] Törlés hiba: {ex.Message}");
                    MessageBox.Show(
                        $"Nem sikerült eltávolítani a Microsoft fiókot:\n{ex.Message}",
                        "Hiba", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            _settings.Profiles.Remove(profile);

            var nextProfile = _settings.Profiles.FirstOrDefault();
            _settings.SelectedProfile = nextProfile?.Name ?? "";
            _settings.Save();

            LoadProfiles();

            if (lblStatus != null) lblStatus.Text = $"Profil törölve: {selectedName}";
        }

        private async void BtnAddMicrosoft_Click(object sender, RoutedEventArgs e)
        {
            if (_loginHandler == null)
            {
                MessageBox.Show("A Microsoft bejelentkezés nem elérhető.\n\nTelepítsd a WebView2 Runtime-ot, majd próbáld újra.",
                    "Hiba", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                btnAddMicrosoft.IsEnabled = false;
                if (lblStatus != null) lblStatus.Text = "Microsoft bejelentkezés...";

                var session = await _loginHandler.Authenticate();

                if (session == null)
                {
                    MessageBox.Show("A bejelentkezés nem sikerült (üres session).",
                        "Hiba", MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (lblStatus != null) lblStatus.Text = "Bejelentkezés sikertelen.";
                    return;
                }

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

                string identifier = session.UUID ?? string.Empty;
                if (!string.IsNullOrEmpty(identifier) && !_settings.Profiles.Any(p => p.Uuid == identifier))
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

                if (lblStatus != null) lblStatus.Text = $"Bejelentkezve: {sessionName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Hiba a Microsoft bejelentkezés közben:\n{ex.Message}",
                    "Hiba", MessageBoxButton.OK, MessageBoxImage.Error);
                if (lblStatus != null) lblStatus.Text = "Bejelentkezés sikertelen.";
            }
            finally
            {
                btnAddMicrosoft.IsEnabled = true;
            }
        }

        private void LoadSettingsIntoUI()
        {
            _isLoadingSettings = true;
            try
            {
                int ram = _settings.RamMb;
                if (ram < sldRam.Minimum) ram = (int)sldRam.Minimum;
                if (ram > sldRam.Maximum) ram = (int)sldRam.Maximum;
                sldRam.Value = ram;
                if (lblRamValue != null) lblRamValue.Text = $"{ram} MB";

                switch (_settings.Theme)
                {
                    case "Dark": if (rbThemeDark != null) rbThemeDark.IsChecked = true; break;
                    case "Light": if (rbThemeLight != null) rbThemeLight.IsChecked = true; break;
                    default: if (rbThemeSystem != null) rbThemeSystem.IsChecked = true; break;
                }

                UpdateThemeDisplay();

                switch (_settings.LauncherBehavior)
                {
                    case "Close": if (rbBehClose != null) rbBehClose.IsChecked = true; break;
                    case "Nothing": if (rbBehNothing != null) rbBehNothing.IsChecked = true; break;
                    default: if (rbBehHide != null) rbBehHide.IsChecked = true; break;
                }

                SelectJavaPreset(_settings.JavaPreset);
                if (txtJavaArgs != null) txtJavaArgs.Text = _settings.JavaArgs ?? "";
            }
            finally
            {
                _isLoadingSettings = false;
            }
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
            if (lblThemeDisplay == null) return;
            switch (_settings.Theme)
            {
                case "Dark": lblThemeDisplay.Text = "Sötét"; break;
                case "Light": lblThemeDisplay.Text = "Világos"; break;
                default: lblThemeDisplay.Text = "Rendszer"; break;
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

            if (!IsLoaded || _isLoadingSettings) return;

            _settings.RamMb = (int)e.NewValue;
            _settings.Save();
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
                    args.Add("-XX:G1NewSizePercent=30");
                    args.Add("-XX:G1MaxNewSizePercent=40");
                    args.Add("-XX:G1HeapRegionSize=8M");
                    args.Add("-XX:G1ReservePercent=20");
                    args.Add("-XX:G1HeapWastePercent=5");
                    args.Add("-XX:G1MixedGCCountTarget=4");
                    args.Add("-XX:InitiatingHeapOccupancyPercent=15");
                    args.Add("-XX:G1MixedGCLiveThresholdPercent=90");
                    args.Add("-XX:G1RSetUpdatingPauseTimePercent=5");
                    args.Add("-XX:SurvivorRatio=32");
                    args.Add("-XX:+PerfDisableSharedMem");
                    args.Add("-XX:MaxTenuringThreshold=1");
                    args.Add("-XX:+UseStringDeduplication");
                    args.Add("-XX:+ParallelRefProcEnabled");
                    args.Add("-XX:MaxGCPauseMillis=200");
                    break;
                case "ZGC":
                    args.Add("-XX:+UnlockExperimentalVMOptions");
                    args.Add("-XX:+UseZGC");
                    args.Add("-XX:+UseStringDeduplication");
                    args.Add("-XX:+ParallelRefProcEnabled");
                    args.Add("-XX:ZCollectionInterval=5");
                    args.Add("-XX:ZAllocationSpikeTolerance=2");
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
                if (_isClosed) return;
                await CheckForUpdatesAsync(showPopup: false);
            };

            _updateTimer.Start();
        }

        private async Task CheckForUpdatesAsync(bool showPopup)
        {
            try
            {
                _velopack ??= new UpdateManager(new SimpleWebSource("https://github.com/Gabix-lab/MvpLauncher/releases/latest/download"));

                if (!_velopack.IsInstalled)
                {
                    Debug.WriteLine("[Velopack] NEM TELEPITETT");
                    ShowUpdateBadge(false);
                    if (lblUpdateStatus != null)
                        lblUpdateStatus.Text = "Nem telepített verzió";

                    if (showPopup)
                    {
                        MessageBox.Show(
                            "A launcher nincs Velopack-kal telepítve.\n\n" +
                            $"Futó hely: {AppDomain.CurrentDomain.BaseDirectory}\n\n" +
                            "Telepítsd a Setup.exe-vel, hogy automatikusan frissüljön.",
                            "Frissítés info", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                if (showPopup && lblUpdateStatus != null)
                    lblUpdateStatus.Text = "Frissítés keresése...";

                var newVersion = await _velopack.CheckForUpdatesAsync();

                if (newVersion == null)
                {
                    Debug.WriteLine($"[Velopack] Nincs új verzió (fut: v{AppVersion})");
                    ShowUpdateBadge(false);
                    if (lblUpdateStatus != null)
                        lblUpdateStatus.Text = "A launcher naprakész.";
                    if (lblUpdateVersion != null)
                        lblUpdateVersion.Visibility = Visibility.Collapsed;
                    if (lblUpdateBadge != null)
                        lblUpdateBadge.Visibility = Visibility.Collapsed;

                    if (showPopup)
                    {
                        MessageBox.Show(
                            $"A launcher naprakész.\n\nFutó verzió: v{AppVersion}",
                            "Frissítés", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                var newVer = newVersion.TargetFullRelease.Version.ToString();
                Debug.WriteLine($"[Velopack] Új verzió elérhető: {newVer}");

                ShowUpdateBadge(true);
                if (lblUpdateStatus != null)
                    lblUpdateStatus.Text = "Új verzió elérhető";
                if (lblUpdateVersion != null)
                {
                    lblUpdateVersion.Text = $"Új verzió: {newVer}";
                    lblUpdateVersion.Visibility = Visibility.Visible;
                }
                if (lblUpdateBadge != null)
                    lblUpdateBadge.Visibility = Visibility.Visible;

                if (!showPopup) return;

                var result = MessageBox.Show(
                    $"Új verzió érhető el: {newVer}\n\nJelenlegi: v{AppVersion}\n\nTelepíted most? A launcher újraindul.",
                    "MVP Launcher frissítés",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                await DownloadAndApplyAsync(newVersion);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Velopack] HIBA: {ex}");

                ShowUpdateBadge(false);
                if (lblUpdateStatus != null)
                    lblUpdateStatus.Text = "Nem sikerült ellenőrizni a frissítést.";

                if (showPopup)
                {
                    MessageBox.Show(
                        $"Frissítés ellenőrzés sikertelen:\n\n" +
                        $"Hiba: {ex.Message}\n" +
                        $"Típus: {ex.GetType().Name}\n\n" +
                        $"Ha ez ismétlődik, töltsd le kézzel:\n{GithubRepoUrl}/releases/latest",
                        "Frissítés hiba",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async Task DownloadAndApplyAsync(UpdateInfo veloUpdate)
        {
            if (_velopack == null) return;

            try
            {
                SetUIEnabled(false);
                if (lblStatus != null) lblStatus.Text = "Frissítés letöltése...";
                SetProgress(0, smooth: false);

                await _velopack.DownloadUpdatesAsync(veloUpdate, pct =>
                {
                    _ = Dispatcher.InvokeAsync(() => SetProgress(pct));
                });

                _velopack.ApplyUpdatesAndRestart(veloUpdate);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Frissítés letöltése sikertelen:\n\n{ex.Message}\n\n" +
                    $"Töltsd le kézzel:\n{GithubRepoUrl}/releases/latest",
                    "Hiba", MessageBoxButton.OK, MessageBoxImage.Error);
                SetUIEnabled(true);
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
            try
            {
                if (lblUpdateStatus != null)
                    lblUpdateStatus.Text = "Frissítés keresése...";

                await CheckForUpdatesAsync(showPopup: true);
            }
            finally
            {
                btnCheckUpdate.IsEnabled = true;
            }
        }

        private async void BtnForceUpdate_Click(object sender, RoutedEventArgs e)
        {
            bool isInstalled = false;
            try
            {
                _velopack ??= new UpdateManager(new SimpleWebSource("https://github.com/Gabix-lab/MvpLauncher/releases/latest/download"));
                isInstalled = _velopack.IsInstalled;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ForceUpdate] Telepítettség ellenőrzése sikertelen: {ex.Message}");
            }

            if (!isInstalled)
            {
                var portableAnswer = MessageBox.Show(
                    "Úgy tűnik, a launcher nincs Setup.exe-vel telepítve (portable módban fut).\n\n" +
                    "A kényszerített frissítés letölti és elindítja a legújabb telepítőt, ami feltelepíti a programot a rendszerre.\n" +
                    "A jelenlegi portable mappád érintetlen marad.\n\n" +
                    "Szeretnéd folytatni?",
                    "Portable verzió észlelve",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (portableAnswer != MessageBoxResult.Yes) return;
            }

            var result = MessageBox.Show(
                "Ez a művelet letölti és elindítja a legújabb (vagy GitHubon 'Latest'-nek jelölt) telepítőt.\n\n" +
                "A folyamat felülírja a jelenlegi telepítést. Folytatod?",
                "Kényszerített frissítés",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            if (btnForceUpdate != null) btnForceUpdate.IsEnabled = false;
            if (btnCheckUpdate != null) btnCheckUpdate.IsEnabled = false;

            try
            {
                string setupUrl = "https://github.com/Gabix-lab/MvpLauncher/releases/latest/download/MvpLauncher-win-Setup.exe";
                string tempPath = Path.Combine(Path.GetTempPath(), "MvpLauncher-update-Setup.exe");

                if (lblUpdateStatus != null)
                    lblUpdateStatus.Text = "Telepítő letöltése...";

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "MvpLauncher-ForceUpdate");

                    using (var response = await client.GetAsync(setupUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        {
                            var buffer = new byte[8192];
                            long totalRead = 0;
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fs.WriteAsync(buffer, 0, read);
                                totalRead += read;
                                if (totalBytes > 0)
                                {
                                    double pct = (double)totalRead / totalBytes * 100;
                                    await Dispatcher.InvokeAsync(() => SetProgress(pct));
                                }
                            }
                        }
                    }
                }

                if (lblUpdateStatus != null)
                    lblUpdateStatus.Text = "Telepítő indítása...";

                await Task.Delay(500);

                _ = Process.Start(new ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Hiba a kényszerített frissítés során:\n\n{ex.Message}\n\n" +
                                $"Ha a hiba ismétlődik, töltsd le kézzel:\n{GithubRepoUrl}/releases/latest",
                    "Hiba", MessageBoxButton.OK, MessageBoxImage.Error);

                if (lblUpdateStatus != null)
                    lblUpdateStatus.Text = "Kényszerített frissítés sikertelen.";

                if (btnForceUpdate != null) btnForceUpdate.IsEnabled = true;
                if (btnCheckUpdate != null) btnCheckUpdate.IsEnabled = true;
            }
        }

        private void BtnOpenModsFolder_Click(object sender, RoutedEventArgs e)
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

        private async void BtnInstallJava_Click(object sender, RoutedEventArgs e)
        {
            if (btnInstallJava != null) btnInstallJava.IsEnabled = false;
            try
            {
                if (!string.IsNullOrEmpty(_javaPath) && File.Exists(_javaPath))
                {
                    var version = JavaManager.GetJavaMajorVersion(_javaPath);
                    var folder = Path.GetFileName(
                        Path.GetDirectoryName(Path.GetDirectoryName(_javaPath) ?? "") ?? "");

                    if (lblStatus != null)
                    {
                        if (version.HasValue && !string.IsNullOrEmpty(folder))
                            lblStatus.Text = $"Java megtalálva: Java {version.Value} ({folder})";
                        else if (version.HasValue)
                            lblStatus.Text = $"Java megtalálva: Java {version.Value}";
                        else
                            lblStatus.Text = "Java megtalálva";
                    }
                    return;
                }

                bool ok = await EnsureJavaAsync();
                if (!ok && lblStatus != null)
                    lblStatus.Text = "A Java telepítése sikertelen";
            }
            catch (Exception ex)
            {
                if (lblStatus != null) lblStatus.Text = $"Hiba: {ex.Message}";
            }
            finally
            {
                if (btnInstallJava != null) btnInstallJava.IsEnabled = true;
            }
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
                var importedNames = new List<string>();

                foreach (var file in files)
                {
                    if (!file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var destPath = Path.Combine(_modsFolder, fileName);
                        File.Copy(file, destPath, true);

                        var disabledPath = destPath + ".disabled";
                        if (File.Exists(disabledPath))
                        {
                            try { File.Delete(disabledPath); } catch { }
                        }

                        imported++;
                        importedNames.Add(fileName);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Drop] '{file}' másolási hiba: {ex.Message}");
                    }
                }

                if (imported > 0)
                {
                    AddModsToCurrentInstance(importedNames);
                    LoadMods();
                    if (lblStatus != null) lblStatus.Text = $"{imported} mod importálva";
                }
            }
            catch (Exception ex)
            {
                if (lblStatus != null) lblStatus.Text = $"Hiba: {ex.Message}";
            }
        }

        private async void BtnToggleMods_Click(object sender, RoutedEventArgs e)
        {
            if (ModsView.Visibility == Visibility.Visible)
            {
                await ShowOnlyAsync(MainView);
                txtModrinthSearch.Text = "";
                ModrinthResults.Clear();
                ModrinthVersions.Clear();
                SelectedModrinthItem = null;
            }
            else
            {
                await ShowOnlyAsync(ModsView);
            }
        }

        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsView.Visibility == Visibility.Visible)
                await ShowOnlyAsync(MainView);
            else
                await ShowOnlyAsync(SettingsView);
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
            if (txtMultiCount != null) txtMultiCount.Text = MultiLaunchInstances.Count.ToString();
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
                SelectedModrinthItem = null;
                if (lblModrinthStatus != null) lblModrinthStatus.Text = "";
                if (lblModrinthEmpty != null)
                {
                    lblModrinthEmpty.Text = "Keress modokat a fenti mezővel.";
                    lblModrinthEmpty.Visibility = Visibility.Visible;
                }
                return;
            }

            if (lblModrinthStatus != null) lblModrinthStatus.Text = "Keresés...";
            ModrinthResults.Clear();
            ModrinthVersions.Clear();
            SelectedModrinthItem = null;
            if (lblModrinthEmpty != null) lblModrinthEmpty.Visibility = Visibility.Collapsed;

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

                if (lblModrinthStatus != null) lblModrinthStatus.Text = $"{ModrinthResults.Count} találat";
                if (lblModrinthEmpty != null)
                {
                    lblModrinthEmpty.Visibility = ModrinthResults.Count == 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                if (lblModrinthStatus != null) lblModrinthStatus.Text = $"Hiba: {ex.Message}";
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
            SelectedModrinthItem = item;
        }

        private async void BtnDownloadVersion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            if (el.Tag is not ModrinthVersion version) return;

            Button? btn = el as Button;
            if (btn != null) btn.IsEnabled = false;
            if (btnModrinthSearch != null) btnModrinthSearch.IsEnabled = false;

            try
            {
                var progress = new Progress<string>(msg => { if (lblModrinthStatus != null) lblModrinthStatus.Text = msg; });
                string? downloaded = await _modrinth.DownloadModAsync(version, _modsFolder, progress);

                if (downloaded != null)
                {
                    var downloadedName = Path.GetFileName(downloaded);

                    var disabledPath = downloaded + ".disabled";
                    if (File.Exists(disabledPath))
                    {
                        try { File.Delete(disabledPath); } catch { }
                    }

                    AddModsToCurrentInstance(new[] { downloadedName });

                    if (lblModrinthStatus != null) lblModrinthStatus.Text = $"Letöltve: {downloadedName}";
                    LoadMods();
                }
                else
                {
                    if (lblModrinthStatus != null) lblModrinthStatus.Text = "Letöltés sikertelen";
                }
            }
            catch (Exception ex)
            {
                if (lblModrinthStatus != null) lblModrinthStatus.Text = $"Hiba: {ex.Message}";
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
                if (btnModrinthSearch != null) btnModrinthSearch.IsEnabled = true;
            }
        }

        private async Task<bool> EnsureJavaAsync()
        {
            if (!string.IsNullOrEmpty(_javaPath) && File.Exists(_javaPath) && JavaManager.IsVersionSufficient(_javaPath))
                return true;

            _javaPath = JavaManager.FindJava();
            if (!string.IsNullOrEmpty(_javaPath) && File.Exists(_javaPath))
                return true;

            if (lblStatus != null) lblStatus.Text = "Java 17 automatikus előkészítése...";
            SetProgress(0, smooth: false);

            try
            {
                var result = await JavaManager.EnsureJavaAsync();

                if (!string.IsNullOrEmpty(result) && File.Exists(result))
                {
                    _javaPath = result;
                    if (lblStatus != null) lblStatus.Text = "Java sikeresen telepítve";
                    SetProgress(100);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Java] Telepítési hiba: {ex.Message}");
            }

            if (lblStatus != null) lblStatus.Text = "A Java telepítése sikertelen";
            return false;
        }

        private void BtnCopySystemInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new System.Text.StringBuilder();

                sb.AppendLine("=== MVP Launcher System Info ===");
                sb.AppendLine($"Launcher version: {AppVersion}");
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
                sb.AppendLine($"Selected instance: {_settings.SelectedInstanceProfile}");
                sb.AppendLine($"Theme: {_settings.Theme}");
                sb.AppendLine($"Language: {_settings.Language}");
                sb.AppendLine($"RAM: {_settings.RamMb} MB");

                try
                {
                    var singleProfile = _settings.InstanceProfiles
                        .FirstOrDefault(p => p.Name == SingleInstanceName);
                    var multiProfile = _settings.InstanceProfiles
                        .FirstOrDefault(p => p.Name == MultiInstanceName);
                    sb.AppendLine($"Single mods count: {singleProfile?.EnabledMods?.Count ?? 0}");
                    sb.AppendLine($"Multi mods count: {multiProfile?.EnabledMods?.Count ?? 0}");
                }
                catch { }

                sb.AppendLine("=== End ===");

                Clipboard.SetText(sb.ToString());
                if (lblStatus != null) lblStatus.Text = "Rendszerinformáció a vágólapra másolva!";
            }
            catch (Exception ex)
            {
                if (lblStatus != null) lblStatus.Text = $"Hiba: {ex.Message}";
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

            bool skipProgress = ShouldSkipAssetProgress(fabricVersionName);

            launcher.FileProgressChanged += (s, args) =>
            {
                if (skipProgress) return;

                double pct = args.TotalTasks > 0
                    ? (double)args.ProgressedTasks / args.TotalTasks * 100.0
                    : 0;

                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (progressBar != null) progressBar.Value = pct;
                    if (lblPercent != null) lblPercent.Text = $"{pct:0}%";
                    if (lblStatus != null)
                        lblStatus.Text = string.IsNullOrEmpty(args.Name)
                            ? "Letöltés"
                            : $"Letöltés: {args.Name}";
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
                    Debug.WriteLine($"[MultiLaunch] Auth hiba ({accountName}): {ex.Message}");
                }
            }

            if (session == null)
                session = MSession.CreateOfflineSession(username)!;

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

            if (process == null)
                throw new InvalidOperationException("Nem sikerült létrehozni a játék folyamatot.");

            if (!skipProgress)
                MarkAssetsChecked(fabricVersionName);

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
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (_isClosed) return;

                        if (behavior == "Close")
                        {
                            Application.Current.Shutdown();
                        }
                        else if (behavior == "Hide")
                        {
                            Show();
                            WindowState = WindowState.Normal;
                            Activate();
                            if (lblStatus != null) lblStatus.Text = "A játék bezárult. Cache törölve.";
                            SetUIEnabled(true);
                        }
                        else
                        {
                            if (lblStatus != null) lblStatus.Text = "A játék bezárult. Cache törölve.";
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
            SwitchToInstance(MultiInstanceName);

            var instances = MultiLaunchInstances
                .Where(i => !string.IsNullOrEmpty(i.SelectedAccount))
                .ToList();

            if (instances.Count == 0)
            {
                MessageBox.Show("Adj hozzá legalább egy klienst!",
                    "Hiba", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int ramMb = (int)sldRam.Value;

            SetUIEnabled(false);
            if (btnLaunchMultiple != null) btnLaunchMultiple.IsEnabled = false;
            SetProgress(0, smooth: false);
            if (lblStatus != null) lblStatus.Text = "Előkészítés...";
            if (lblGameStartupTime != null) lblGameStartupTime.Text = "";

            var gameStartupSw = Stopwatch.StartNew();

            try
            {
                if (!await EnsureJavaAsync())
                {
                    if (lblStatus != null) lblStatus.Text = "Java nem elérhető — próbáld újra pár perc múlva.";
                    SetUIEnabled(true);
                    if (btnLaunchMultiple != null) btnLaunchMultiple.IsEnabled = true;
                    return;
                }

                var cacheProgress = new Progress<string>(msg =>
                {
                    if (lblStatus != null) lblStatus.Text = msg;
                    Debug.WriteLine($"[CacheMod] {msg}");
                });

                bool cacheOk = await _cacheManager.PrepareAsync(cacheProgress);

                if (!cacheOk)
                {
                    var result = MessageBox.Show(
                        "A cache mod letöltése sikertelen.\n\nEl akarod indítani a játékot cache mod nélkül?",
                        "Cache mod hiba",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.No)
                    {
                        if (lblStatus != null) lblStatus.Text = "Indítás megszakítva.";
                        SetUIEnabled(true);
                        if (btnLaunchMultiple != null) btnLaunchMultiple.IsEnabled = true;
                        return;
                    }
                }

                if (lblStatus != null) lblStatus.Text = "Fabric loader telepítése...";
                string fabricVersionName = await GetOrInstallFabricVersionAsync();

                int launched = 0;

                foreach (var inst in instances)
                {
                    try
                    {
                        if (lblStatus != null) lblStatus.Text = $"Indítás: {inst.SelectedAccount}...";

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
                        Debug.WriteLine(
                            $"[MultiLaunch] '{inst.SelectedAccount}' indítási hiba: {ex.Message}");
                    }
                }

                gameStartupSw.Stop();
                if (lblGameStartupTime != null)
                    lblGameStartupTime.Text = $"Játék indult: {gameStartupSw.Elapsed.TotalSeconds:0.00} s";

                if (lblStatus != null) lblStatus.Text = $"{launched} kliens elindítva!";
                SetProgress(100);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Hiba történt az indításkor.:\n{ex.Message}",
                    "Hiba", MessageBoxButton.OK, MessageBoxImage.Error);
                if (lblStatus != null) lblStatus.Text = "Hiba történt az indításkor.";
            }
            finally
            {
                SetUIEnabled(true);
                if (btnLaunchMultiple != null) btnLaunchMultiple.IsEnabled = true;
            }
        }

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            SwitchToInstance(SingleInstanceName);

            string username = _settings.SelectedProfile;

            if (string.IsNullOrEmpty(username))
            {
                MessageBox.Show("Kérlek válassz vagy adj hozzá egy felhasználónevet!",
                    "Hiányzó név", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int ramMb = (int)sldRam.Value;

            SetUIEnabled(false);
            SetProgress(0, smooth: false);
            if (lblStatus != null) lblStatus.Text = "Előkészítés...";
            if (lblGameStartupTime != null) lblGameStartupTime.Text = "";

            var gameStartupSw = Stopwatch.StartNew();

            try
            {
                if (!await EnsureJavaAsync())
                {
                    if (lblStatus != null) lblStatus.Text = "Java nem elérhető — próbáld újra pár perc múlva.";
                    SetUIEnabled(true);
                    return;
                }

                var cacheProgress = new Progress<string>(msg =>
                {
                    if (lblStatus != null) lblStatus.Text = msg;
                    Debug.WriteLine($"[CacheMod] {msg}");
                });

                bool cacheOk = await _cacheManager.PrepareAsync(cacheProgress);

                if (!cacheOk)
                {
                    var result = MessageBox.Show(
                        "A cache mod letöltése sikertelen.\n\nEl akarod indítani a játékot cache mod nélkül?",
                        "Cache mod hiba",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.No)
                    {
                        if (lblStatus != null) lblStatus.Text = "Indítás megszakítva.";
                        SetUIEnabled(true);
                        return;
                    }
                }

                if (lblStatus != null) lblStatus.Text = "Fabric loader telepítése...";
                string fabricVersionName = await GetOrInstallFabricVersionAsync();

                if (lblStatus != null) lblStatus.Text = "Bejelentkezés...";

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
                            $"Microsoft bejelentkezés sikertelen:\n{ex.Message}\n\nOffline módban folytatod?",
                            "Bejelentkezés hiba",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (result == MessageBoxResult.No)
                        {
                            if (lblStatus != null) lblStatus.Text = "Indítás megszakítva.";
                            SetUIEnabled(true);
                            return;
                        }
                    }
                }

                if (session == null)
                {
                    session = MSession.CreateOfflineSession(username)!;
                }

                if (lblStatus != null) lblStatus.Text = "Játék indítása...";

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

                bool skipProgress = ShouldSkipAssetProgress(fabricVersionName);

                launcher.FileProgressChanged += (s, args) =>
                {
                    if (skipProgress) return;

                    double pct = args.TotalTasks > 0
                        ? (double)args.ProgressedTasks / args.TotalTasks * 100.0
                        : 0;

                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (progressBar != null) progressBar.Value = pct;
                        if (lblPercent != null) lblPercent.Text = $"{pct:0}%";
                        if (lblStatus != null)
                            lblStatus.Text = string.IsNullOrEmpty(args.Name)
                                ? "Letöltés"
                                : $"Letöltés: {args.Name}";
                    });
                };

                var process = await launcher.CreateProcessAsync(fabricVersionName, launchOption);

                if (process == null)
                    throw new InvalidOperationException("Nem sikerült létrehozni a játék folyamatot.");

                if (!skipProgress)
                    MarkAssetsChecked(fabricVersionName);

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
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (_isClosed) return;

                        if (behavior == "Close")
                        {
                            Application.Current.Shutdown();
                        }
                        else if (behavior == "Hide")
                        {
                            Show();
                            WindowState = WindowState.Normal;
                            Activate();
                            if (lblStatus != null) lblStatus.Text = "A játék bezárult. Cache törölve.";
                            SetUIEnabled(true);
                        }
                        else
                        {
                            if (lblStatus != null) lblStatus.Text = "A játék bezárult. Cache törölve.";
                            SetUIEnabled(true);
                        }
                    });
                };

                process.Start();

                gameStartupSw.Stop();
                if (lblGameStartupTime != null)
                    lblGameStartupTime.Text = $"Játék indult: {gameStartupSw.Elapsed.TotalSeconds:0.00} s";

                if (lblStatus != null) lblStatus.Text = "A játék sikeresen elindult! Jó szórakozást!";
                SetProgress(100);

                if (behavior == "Close" || behavior == "Hide")
                {
                    Hide();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Hiba történt az indításkor.:\n{ex.Message}",
                    "Hiba", MessageBoxButton.OK, MessageBoxImage.Error);
                if (lblStatus != null) lblStatus.Text = "Hiba történt az indításkor.";
                SetUIEnabled(true);
            }
        }

        private async void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            if (MainView.Visibility == Visibility.Visible) return;

            await ShowOnlyAsync(MainView);

            if (txtModrinthSearch != null) txtModrinthSearch.Text = "";
            ModrinthResults.Clear();
            ModrinthVersions.Clear();
            SelectedModrinthItem = null;
        }

        private void SetUIEnabled(bool enabled)
        {
            if (btnPlay != null) btnPlay.IsEnabled = enabled;
            if (btnToggleMods != null) btnToggleMods.IsEnabled = enabled;
            if (btnSettings != null) btnSettings.IsEnabled = enabled;
            if (cmbProfiles != null) cmbProfiles.IsEnabled = enabled;
            if (btnAddProfile != null) btnAddProfile.IsEnabled = enabled;
            if (btnAddMicrosoft != null) btnAddMicrosoft.IsEnabled = enabled;
            if (btnModrinthSearch != null) btnModrinthSearch.IsEnabled = enabled;
            if (btnCheckUpdate != null) btnCheckUpdate.IsEnabled = enabled;
            if (cmbInstanceSelector != null) cmbInstanceSelector.IsEnabled = enabled;
            if (btnDeleteProfile != null) btnDeleteProfile.IsEnabled = enabled;
            if (btnLaunchMultiple != null) btnLaunchMultiple.IsEnabled = enabled;
            if (btnInstallJava != null) btnInstallJava.IsEnabled = enabled;
            if (btnOpenModsFolder != null) btnOpenModsFolder.IsEnabled = enabled;
        }

        protected void OnPropertyChanged(string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}