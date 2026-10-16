using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MvpLauncher
{
    public class MultiLaunchInstance : INotifyPropertyChanged
    {
        private int _index;
        private string _selectedAccount = "";

        public int Index
        {
            get => _index;
            set { _index = value; OnPropertyChanged(); }
        }

        public string SelectedAccount
        {
            get => _selectedAccount;
            set { _selectedAccount = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> AvailableAccounts { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}