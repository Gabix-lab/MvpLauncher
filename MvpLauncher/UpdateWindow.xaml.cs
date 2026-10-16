using System;
using System.Windows;

namespace MvpLauncher
{
    public partial class UpdateWindow : Window
    {
        private readonly UpdateInfo _info;
        private readonly UpdateService _service;

        public UpdateWindow(UpdateInfo info, UpdateService service)
        {
            InitializeComponent();
            _info = info;
            _service = service;

            lblVersionInfo.Text = $"Jelenlegi: {UpdateService.CurrentVersion}  →  Új: {_info.Version}";
            lblNotes.Text = string.IsNullOrEmpty(_info.Notes) ? "(nincs megjegyzés)" : _info.Notes;
            lblProgress.Text = "Várakozás a frissítésre...";
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            btnUpdate.IsEnabled = false;
            btnCancel.IsEnabled = false;

            lblProgress.Text = "Letöltés...";

            var progress = new Progress<int>(pct =>
            {
                progressBar.Value = pct;
                lblPercent.Text = $"{pct}%";
            });

            var zipPath = await _service.DownloadAsync(_info.Url, progress);

            if (zipPath == null)
            {
                lblProgress.Text = "A letöltés sikertelen.";
                MessageBox.Show("Nem sikerült letölteni a frissítést.",
                    "Hiba", MessageBoxButton.OK, MessageBoxImage.Error);

                btnUpdate.IsEnabled = true;
                btnCancel.IsEnabled = true;
                return;
            }

            lblProgress.Text = "Kibontás és telepítés...";
            progressBar.Value = 100;
            lblPercent.Text = "100%";

            _service.ApplyAndRestart(zipPath);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}