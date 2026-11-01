using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace MvpLauncher
{
    public class ModItem : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private bool _isLocked;
        private bool _isFromCache;
        private string _version = "";
        private string _author = "Unknown";
        private string _description = "No description available.";
        private string _iconUrl = "";
        private string _downloadsText = "-";
        private string _updatedText = "-";
        private string _gameVersionText = "1.19.4";
        private string? _lockReason;

        public string DisplayName { get; set; } = "";
        public string BaseName { get; set; } = "";
        public string ModsFolder { get; set; } = "";
        public string ModId { get; set; } = "";
        public bool IsMandatory { get; set; } = false;

        public ObservableCollection<string> Provides { get; } = new();

        public string Author
        {
            get => _author;
            set { _author = value; OnPropertyChanged(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        public string IconUrl
        {
            get => _iconUrl;
            set { _iconUrl = value; OnPropertyChanged(); }
        }

        public string DownloadsText
        {
            get => _downloadsText;
            set { _downloadsText = value; OnPropertyChanged(); }
        }

        public string UpdatedText
        {
            get => _updatedText;
            set { _updatedText = value; OnPropertyChanged(); }
        }

        public string GameVersionText
        {
            get => _gameVersionText;
            set { _gameVersionText = value; OnPropertyChanged(); }
        }

        public bool IsFromCache
        {
            get => _isFromCache;
            set
            {
                if (_isFromCache == value) return;
                _isFromCache = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<string> Dependencies { get; } = new();
        public ObservableCollection<string> Dependents { get; } = new();
        public ObservableCollection<string> MissingDependencies { get; } = new();

        public bool HasDependencyInfo => Dependencies.Count > 0 || Dependents.Count > 0;

        public bool HasMissingDependencies => MissingDependencies.Count > 0;

        public string DependencyText
        {
            get
            {
                if (Dependencies.Count == 0 && Dependents.Count == 0)
                    return "";

                var parts = new System.Collections.Generic.List<string>();
                if (Dependencies.Count > 0)
                    parts.Add($"Függ: {string.Join(", ", Dependencies)}");
                if (Dependents.Count > 0)
                    parts.Add($"Igényli: {string.Join(", ", Dependents)}");
                return string.Join(" | ", parts);
            }
        }

        public string MissingDependencyText =>
            MissingDependencies.Count == 0
                ? ""
                : $"Hiányzó függőség: {string.Join(", ", MissingDependencies)}";

        public bool HasVersion => !string.IsNullOrEmpty(_version);

        public string Version
        {
            get => _version;
            set
            {
                if (_version == value) return;
                _version = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VersionDisplay));
                OnPropertyChanged(nameof(HasVersion));
            }
        }

        public string VersionDisplay =>
            string.IsNullOrEmpty(_version) ? "" :
            (_version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? _version : $"v{_version}");

        public bool HasLockReason => !string.IsNullOrEmpty(_lockReason);

        public string? LockReason
        {
            get => _lockReason;
            set
            {
                if (_lockReason == value) return;
                _lockReason = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLockReason));
            }
        }

        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                if (_isLocked == value) return;
                _isLocked = value;
                OnPropertyChanged();
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;

                bool success = TrySetEnabled(value, out _);
                if (success)
                {
                    _isEnabled = value;
                }
                OnPropertyChanged();
            }
        }

        public void RefreshDependencyText()
        {
            OnPropertyChanged(nameof(DependencyText));
            OnPropertyChanged(nameof(HasDependencyInfo));
            OnPropertyChanged(nameof(MissingDependencyText));
            OnPropertyChanged(nameof(HasMissingDependencies));
        }

        public bool ToggleEnabled()
        {
            return TryToggleEnabled(out _);
        }

        public bool TryToggleEnabled(out string? error)
        {
            error = null;

            bool target = !_isEnabled;
            bool success = TrySetEnabled(target, out error);
            if (success)
            {
                _isEnabled = target;
            }
            OnPropertyChanged(nameof(IsEnabled));
            return success;
        }

        private bool TrySetEnabled(bool enable, out string? error)
        {
            error = null;
            try
            {
                string jarPath = Path.Combine(ModsFolder, BaseName);
                string disabledPath = jarPath + ".disabled";

                if (enable)
                {
                    if (File.Exists(disabledPath))
                    {
                        File.Move(disabledPath, jarPath, overwrite: true);
                    }
                    return File.Exists(jarPath);
                }
                else
                {
                    if (File.Exists(jarPath))
                    {
                        File.Move(jarPath, disabledPath, overwrite: true);
                    }
                    return File.Exists(disabledPath);
                }
            }
            catch (IOException ex)
            {
                error = "A mod fájlját jelenleg egy futó folyamat használja " +
                        "(valószínűleg a játék fut). Zárd be a játékot, majd próbáld újra.";
                Debug.WriteLine($"[ModItem] TrySetEnabled({enable}) IO hiba '{BaseName}': {ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "Nincs jogosultság a mod fájl módosításához. " +
                        "Próbáld rendszergazdaként futtatni a launchurt.";
                Debug.WriteLine($"[ModItem] TrySetEnabled({enable}) jogosultság hiba '{BaseName}': {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                error = $"A mod állapotát nem sikerült megváltoztatni: {ex.Message}";
                Debug.WriteLine($"[ModItem] TrySetEnabled({enable}) hiba '{BaseName}': {ex.Message}");
                return false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}