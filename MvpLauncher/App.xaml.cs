using System.Diagnostics;
using System.Windows;

namespace MvpLauncher
{
    public partial class App : Application
    {
        public static readonly Stopwatch StartupStopwatch = new Stopwatch();

        protected override void OnStartup(StartupEventArgs e)
        {
            StartupStopwatch.Start();

            var settings = AppSettings.Load();
            TranslationSource.Instance.Language = settings.Language;
            ThemeManager.ApplyTheme(settings.Theme);

            base.OnStartup(e);
        }
    }
}