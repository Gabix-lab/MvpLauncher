using System.Diagnostics;
using System.Windows;

namespace MvpLauncher
{
    public partial class App : Application
    {
        public static readonly Stopwatch StartupStopwatch = new Stopwatch();

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            StartupStopwatch.Start();

            LoadingWindow? loading = null;

            try
            {
                var settings = AppSettings.Load();
                ThemeManager.ApplyTheme(settings.Theme);

                loading = new LoadingWindow();
                loading.Show();

                await loading.RunStartupAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] Betöltési hiba: {ex}");
            }

            try
            {
                var main = new MainWindow();
                MainWindow = main;
                main.Show();

                if (loading != null)
                {
                    await loading.FadeOutAndCloseAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] MainWindow hiba: {ex}");

                if (loading != null)
                {
                    try { await loading.FadeOutAndCloseAsync(); } catch { }
                }

                MessageBox.Show(
                    $"Hiba történt az indítás során:\n\n{ex.Message}\n\n{ex.GetType().Name}",
                    "MVP Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown(1);
            }
        }
    }
}