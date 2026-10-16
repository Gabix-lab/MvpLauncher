using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

        public string DisplayName { get; set; } = "";
        public string BaseName { get; set; } = "";
        public string ModsFolder { get; set; } = "";
        public string ModId { get; set; } = "";

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

        public string DependencyText
        {
            get
            {
                if (Dependencies.Count == 0 && Dependents.Count == 0)
                    return "";

                var parts = new List<string>();
                if (Dependencies.Count > 0)
                    parts.Add($"Függ: {string.Join(", ", Dependencies)}");
                if (Dependents.Count > 0)
                    parts.Add($"Igényli: {string.Join(", ", Dependents)}");
                return string.Join(" | ", parts);
            }
        }

        public string Version
        {
            get => _version;
            set
            {
                if (_version == value) return;
                _version = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VersionDisplay));
            }
        }

        public string VersionDisplay =>
            string.IsNullOrEmpty(_version) ? "" : $"Verzió: {_version}";

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
                if (IsLocked) return;
                if (IsFromCache)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                    return;
                }

                bool success = TrySetEnabled(value);
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
        }

        private bool TrySetEnabled(bool enable)
        {
            try
            {
                string jarPath = Path.Combine(ModsFolder, BaseName);
                string disabledPath = jarPath + ".disabled";

                if (enable && File.Exists(disabledPath))
                {
                    File.Move(disabledPath, jarPath);
                }
                else if (!enable && File.Exists(jarPath))
                {
                    File.Move(jarPath, disabledPath);
                }

                return true;
            }
            catch
            {
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