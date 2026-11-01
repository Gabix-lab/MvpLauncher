using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MvpLauncher
{
    public partial class LoadingWindow : Window
    {
        private bool _startupComplete;
        private bool _userCancelled;
        private bool _closingAnimationStarted;

        private const double CircleRadius = 60;
        private const double CircleCenter = 80;

        public static readonly DependencyProperty ProgressProperty =
            DependencyProperty.Register(
                nameof(Progress),
                typeof(double),
                typeof(LoadingWindow),
                new PropertyMetadata(0.0, OnProgressChanged));

        public double Progress
        {
            get => (double)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LoadingWindow w)
            {
                w.UpdateCircularProgress((double)e.NewValue);
            }
        }

        public LoadingWindow()
        {
            InitializeComponent();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null && lblVersion != null)
                lblVersion.Text = $"v{version.ToString(3)}";

            Opacity = 0;

            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        public bool UserCancelled => _userCancelled;

        public void MarkStartupComplete() => _startupComplete = true;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            if (glowRing != null)
            {
                var pulse = new DoubleAnimation
                {
                    From = 0.2,
                    To = 0.5,
                    Duration = TimeSpan.FromSeconds(1.8),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                glowRing.BeginAnimation(OpacityProperty, pulse);
            }

            UpdateCircularProgress(0);
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_startupComplete || _closingAnimationStarted) return;

            _userCancelled = true;
            Application.Current?.Shutdown(0);
        }

        public async Task FadeOutAndCloseAsync()
        {
            _closingAnimationStarted = true;

            var tcs = new TaskCompletionSource();
            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (s, e) => tcs.TrySetResult();

            BeginAnimation(OpacityProperty, fade);
            await Task.WhenAny(tcs.Task, Task.Delay(300));

            _startupComplete = true;
            Close();
        }

        private void UpdateCircularProgress(double percentage)
        {
            if (double.IsNaN(percentage) || percentage < 0) percentage = 0;
            if (percentage > 100) percentage = 100;

            if (pathProgress == null) return;

            if (percentage < 0.5)
            {
                pathProgress.Data = null;
                return;
            }

            double angle = (percentage / 100.0) * 2 * Math.PI;
            double x = CircleCenter + CircleRadius * Math.Sin(angle);
            double y = CircleCenter - CircleRadius * Math.Cos(angle);

            var figure = new PathFigure
            {
                StartPoint = new Point(CircleCenter, CircleCenter - CircleRadius),
                IsClosed = false,
                IsFilled = false
            };

            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(x, y),
                Size = new Size(CircleRadius, CircleRadius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = percentage > 50
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            pathProgress.Data = geometry;
        }

        private void SetStatus(string status, double progress)
        {
            try
            {
                if (Dispatcher.CheckAccess())
                    ApplyStatus(status, progress);
                else
                    Dispatcher.InvokeAsync(() => ApplyStatus(status, progress));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loading] SetStatus hiba: {ex.Message}");
            }
        }

        private void ApplyStatus(string status, double progress)
        {
            if (lblStatus != null) lblStatus.Text = status;

            BeginAnimation(ProgressProperty, new DoubleAnimation
            {
                From = Progress,
                To = progress,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private void SetDetail(string detail)
        {
            try
            {
                if (Dispatcher.CheckAccess())
                    lblDetail.Text = detail ?? "";
                else
                    Dispatcher.InvokeAsync(() => lblDetail.Text = detail ?? "");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loading] SetDetail hiba: {ex.Message}");
            }
        }

        private static string GetModsFolderPath()
        {
            string basePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".mvplauncher");

            try
            {
                var mcPath = new CmlLib.Core.MinecraftPath(basePath);
                string modsFolder = Path.Combine(mcPath.BasePath, "mods");
                Directory.CreateDirectory(modsFolder);
                return modsFolder;
            }
            catch
            {
                return Path.Combine(basePath, "mods");
            }
        }

        public async Task RunStartupAsync()
        {
            try
            {
                await Task.Delay(150);
                if (_userCancelled) return;

                SetStatus("Beállítások betöltése...", 10);
                SetDetail("Konfigurációk olvasása...");
                await Task.Yield();
                if (_userCancelled) return;

                SetStatus("Java ellenőrzése...", 30);
                try
                {
                    var javaPath = await JavaManager.FindJavaAsync();
                    SetDetail(javaPath != null
                        ? "Java megtalálva"
                        : "Java nem található – automatikus telepítés szükséges lehet");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Loading] Java keresési hiba: {ex.Message}");
                    SetDetail("Java ellenőrzés sikertelen");
                }
                await Task.Yield();
                if (_userCancelled) return;

                SetStatus("Modok szinkronizálása...", 60);
                SetDetail("Szükséges fájlok ellenőrzése...");

                string modsFolder = GetModsFolderPath();
                var progress = new Progress<string>(SetDetail);

                try
                {
                    await AutoModInstaller.EnsureRequiredModsAsync(modsFolder, progress);
                    SetDetail("Minden mod a helyén van");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Loading] AutoMod hiba: {ex.Message}");
                    SetDetail("Mod szinkronizálás sikertelen – az indítás folytatódik");
                }

                if (_userCancelled) return;

                SetStatus("Előkészítés...", 90);
                SetDetail("Felhasználói felület betöltése...");
                await Task.Delay(120);
                if (_userCancelled) return;

                SetStatus("Készen áll", 100);
                SetDetail("");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loading] RunStartupAsync hiba: {ex}");
            }
        }
    }
}