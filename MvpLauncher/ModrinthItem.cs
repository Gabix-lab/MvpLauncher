using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MvpLauncher
{
    public class ModrinthItem : INotifyPropertyChanged
    {
        private string _title = "";
        private string _description = "";
        private string _iconUrl = "";
        private string _author = "";
        private string _downloads = "";

        public string ProjectId { get; set; } = "";
        public string Slug { get; set; } = "";

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
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

        public string Author
        {
            get => _author;
            set { _author = value; OnPropertyChanged(); }
        }

        public string DownloadsText
        {
            get => _downloads;
            set { _downloads = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}