using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MvpLauncher
{
    public class InstanceProfile : INotifyPropertyChanged
    {
        private string _name = "";
        private bool _isEditing;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public List<string> EnabledMods { get; set; } = new();

        [JsonIgnore]
        public bool IsEditing
        {
            get => _isEditing;
            set { _isEditing = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}