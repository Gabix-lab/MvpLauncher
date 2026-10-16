using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MvpLauncher
{
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string Url { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    public class UpdateService
    {
        public const string CurrentVersion = "1.0.0";
        private const string VersionUrl = "https://mvp-backend-bods.onrender.com/launcher/version";

        private readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        public async Task<UpdateInfo?> CheckAsync()
        {
            try
            {
                var json = await _httpClient.GetStringAsync(VersionUrl);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("version", out var ver) &&
                    root.TryGetProperty("url", out var url))
                {
                    var v = ver.GetString() ?? "";
                    var u = url.GetString() ?? "";
                    var n = root.TryGetProperty("notes", out var notes) ? notes.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(v) && v != CurrentVersion && !string.IsNullOrEmpty(u))
                    {
                        return new UpdateInfo { Version = v, Url = u, Notes = n };
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        public async Task<string?> DownloadAsync(string url, IProgress<int>? progress = null)
        {
            try
            {
                var tempFile = Path.Combine(Path.GetTempPath(), "MvpLauncher_update.zip");

                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = File.Create(tempFile);

                var buffer = new byte[81920];
                long read = 0;
                int bytes;

                while ((bytes = await stream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytes));
                    read += bytes;

                    if (total > 0)
                    {
                        progress?.Report((int)(read * 100 / total));
                    }
                }

                return tempFile;
            }
            catch
            {
                return null;
            }
        }

        public void ApplyAndRestart(string zipPath)
        {
            try
            {
                var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(currentExe))
                {
                    throw new Exception("Nem található a jelenlegi exe.");
                }

                var currentDir = Path.GetDirectoryName(currentExe);
                if (string.IsNullOrEmpty(currentDir))
                {
                    throw new Exception("Nem található a jelenlegi mappa.");
                }

                var extractDir = Path.Combine(Path.GetTempPath(), "MvpLauncher_extract");

                if (Directory.Exists(extractDir))
                {
                    try { Directory.Delete(extractDir, true); } catch { }
                }

                Directory.CreateDirectory(extractDir);

                ZipFile.ExtractToDirectory(zipPath, extractDir);

                var batPath = Path.Combine(Path.GetTempPath(), "MvpLauncher_update.bat");

                var batContent =
                    "@echo off\r\n" +
                    "timeout /t 2 /nobreak >nul\r\n" +
                    $"xcopy /Y /E /I \"{extractDir}\\*\" \"{currentDir}\\\\\" >nul\r\n" +
                    $"start \"\" \"{currentExe}\"\r\n" +
                    $"rmdir /S /Q \"{extractDir}\"\r\n" +
                    $"del \"{zipPath}\"\r\n" +
                    "del \"%~f0\"\r\n";

                File.WriteAllText(batPath, batContent);

                Process.Start(new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                });

                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Nem sikerült a frissítés:\n{ex.Message}",
                    "Hiba",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }
}