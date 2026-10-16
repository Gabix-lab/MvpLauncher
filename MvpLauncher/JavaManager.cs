using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MvpLauncher
{
    public static class JavaManager
    {
        private const string AdoptiumApiUrl =
            "https://api.adoptium.net/v3/assets/latest/17/hotspot" +
            "?architecture=x64&image_type=jre&os=windows&vendor=eclipse&heap_size=normal";

        public static string GetBundledJavaFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".mvplauncher",
                "java");
        }

        public static string? FindJava()
        {
            try
            {
                var bundled = GetBundledJavaFolder();
                if (Directory.Exists(bundled))
                {
                    var javaExe = FindJavaInFolder(bundled);
                    if (javaExe != null) return javaExe;
                }
            }
            catch { }

            try
            {
                var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (!string.IsNullOrEmpty(javaHome))
                {
                    var path = Path.Combine(javaHome, "bin", "java.exe");
                    if (File.Exists(path)) return path;
                }
            }
            catch { }

            try
            {
                var pathVar = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathVar))
                {
                    foreach (var dir in pathVar.Split(';'))
                    {
                        try
                        {
                            var candidate = Path.Combine(dir.Trim(), "java.exe");
                            if (File.Exists(candidate)) return candidate;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            var commonPaths = new[]
            {
                @"C:\Program Files\Java",
                @"C:\Program Files\Eclipse Adoptium",
                @"C:\Program Files\Microsoft\jdk",
                @"C:\Program Files\Amazon Corretto",
                @"C:\Program Files\Zulu",
                @"C:\Program Files (x86)\Java",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Java")
            };

            foreach (var basePath in commonPaths)
            {
                try
                {
                    if (!Directory.Exists(basePath)) continue;
                    var javaExe = FindJavaInFolder(basePath);
                    if (javaExe != null) return javaExe;
                }
                catch { }
            }

            return null;
        }

        private static string? FindJavaInFolder(string folder)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(folder))
                {
                    var javaExe = Path.Combine(dir, "bin", "java.exe");
                    if (File.Exists(javaExe)) return javaExe;

                    var nested = FindJavaInFolder(dir);
                    if (nested != null) return nested;
                }
            }
            catch { }

            return null;
        }

        public static async Task<string?> DownloadAndInstallAsync(IProgress<int>? progress = null)
        {
            try
            {
                var targetFolder = GetBundledJavaFolder();

                if (Directory.Exists(targetFolder))
                {
                    try { Directory.Delete(targetFolder, true); } catch { }
                }

                Directory.CreateDirectory(targetFolder);

                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(15)
                };

                var json = await httpClient.GetStringAsync(AdoptiumApiUrl);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.GetArrayLength() == 0)
                {
                    throw new Exception("Nem található Java letöltési link.");
                }

                var first = doc.RootElement[0];
                var binaryLink = first
                    .GetProperty("binary")
                    .GetProperty("package")
                    .GetProperty("link")
                    .GetString();

                if (string.IsNullOrEmpty(binaryLink))
                {
                    throw new Exception("Érvénytelen letöltési link.");
                }

                var tempZip = Path.Combine(Path.GetTempPath(), "mvplauncher-jre.zip");

                using (var response = await httpClient.GetAsync(binaryLink, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? 0;

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = File.Create(tempZip);

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
                }

                progress?.Report(100);

                var extractTemp = Path.Combine(Path.GetTempPath(), "mvplauncher-jre-extract");
                if (Directory.Exists(extractTemp))
                {
                    try { Directory.Delete(extractTemp, true); } catch { }
                }

                Directory.CreateDirectory(extractTemp);

                ZipFile.ExtractToDirectory(tempZip, extractTemp);

                var subDirs = Directory.GetDirectories(extractTemp);
                if (subDirs.Length > 0)
                {
                    var inner = subDirs[0];
                    foreach (var item in Directory.GetFileSystemEntries(inner))
                    {
                        var destName = Path.GetFileName(item);
                        var destPath = Path.Combine(targetFolder, destName);

                        if (Directory.Exists(item))
                        {
                            CopyDirectory(item, destPath);
                        }
                        else
                        {
                            File.Copy(item, destPath, true);
                        }
                    }
                }
                else
                {
                    foreach (var item in Directory.GetFileSystemEntries(extractTemp))
                    {
                        var destName = Path.GetFileName(item);
                        var destPath = Path.Combine(targetFolder, destName);

                        if (Directory.Exists(item))
                        {
                            CopyDirectory(item, destPath);
                        }
                        else
                        {
                            File.Copy(item, destPath, true);
                        }
                    }
                }

                try { File.Delete(tempZip); } catch { }
                try { Directory.Delete(extractTemp, true); } catch { }

                var javaExe = Path.Combine(targetFolder, "bin", "java.exe");

                if (File.Exists(javaExe))
                {
                    return javaExe;
                }

                return FindJavaInFolder(targetFolder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JavaManager] HIBA: {ex.Message}");
                return null;
            }
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (var file in Directory.GetFiles(source))
            {
                var target = Path.Combine(dest, Path.GetFileName(file));
                File.Copy(file, target, true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                var target = Path.Combine(dest, Path.GetFileName(dir));
                CopyDirectory(dir, target);
            }
        }
    }
}