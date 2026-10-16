using System.ComponentModel;

namespace MvpLauncher
{
    public class TranslationSource : INotifyPropertyChanged
    {
        private static readonly TranslationSource _instance = new();
        public static TranslationSource Instance => _instance;

        private string _language = "hu";

        public string this[string key]
        {
            get
            {
                if (Translations.All.TryGetValue(_language, out var dict) &&
                    dict.TryGetValue(key, out var value))
                {
                    return value;
                }

                if (Translations.All.TryGetValue("en", out var fallback) &&
                    fallback.TryGetValue(key, out var fallbackValue))
                {
                    return fallbackValue;
                }

                return key;
            }
        }

        public string Language
        {
            get => _language;
            set
            {
                if (_language == value) return;
                _language = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}