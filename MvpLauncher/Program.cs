using Velopack;

namespace MvpLauncher
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            VelopackApp.Build().Run();

            App.StartupStopwatch.Start();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}