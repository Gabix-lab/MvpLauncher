using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MvpLauncher
{
    public static class JavaManager
    {
        public const int RequiredMajorVersion = 17;

        private static readonly string[] AdoptiumApis =
        {
            "https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse&heap_size=normal",
            "https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse&heap_size=normal",
            "https://api.adoptium.net/v3/assets/latest/21/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse&heap_size=normal",
        };

        private static readonly string[] DirectZipUrls =
        {
            "https://corretto.aws/downloads/latest/amazon-corretto-17-x64-windows-jdk.zip",
            "https://aka.ms/download-jdk/microsoft-jdk-17-windows-x64.zip",
        };

        private static readonly object _installLock = new();
        private static Task<string?>? _installTask;
        private static string? _cachedPath;
        private static string? _cachedPathFromDisk;

        public static event EventHandler<string?>? JavaReady;
        public static event EventHandler<int>? InstallProgress;
        public static event EventHandler<bool>? InstallStateChanged;

        public static bool IsInstalling
        {
            get
            {
                lock (_installLock) { return _installTask != null && !_installTask.IsCompleted; }
            }
        }

        public static string GetBundledJavaFolder() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".mvplauncher",
                "java");

        private static string GetCacheFilePath() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".mvplauncher",
                "java-path.cache");

        private static string? TryReadCachedPath()
        {
            try
            {
                if (_cachedPathFromDisk != null) return _cachedPathFromDisk;
                var file = GetCacheFilePath();
                if (File.Exists(file))
                {
                    var path = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        var ver = GetJavaMajorVersion(path);
                        if (ver.HasValue && ver.Value >= RequiredMajorVersion)
                        {
                            _cachedPathFromDisk = path;
                            return path;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static void CachePath(string path)
        {
            _cachedPath = path;
            _cachedPathFromDisk = path;
            try
            {
                var file = GetCacheFilePath();
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(file, path);
            }
            catch { }
        }

        public static int? GetJavaMajorVersion(string javaPath)
        {
            try
            {
                if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath)) return null;
                var psi = new ProcessStartInfo
                {
                    FileName = javaPath,
                    Arguments = "-version",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                if (!proc.WaitForExit(5000))
                {
                    try { proc.Kill(); } catch { }
                    return null;
                }
                string output = proc.StandardError.ReadToEnd();
                if (string.IsNullOrEmpty(output)) output = proc.StandardOutput.ReadToEnd();
                if (string.IsNullOrEmpty(output)) return null;
                var match = Regex.Match(output, @"version\s+""?(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int major))
                    return major;
            }
            catch { }
            return null;
        }

        public static bool IsVersionSufficient(string javaPath, int minMajorVersion = RequiredMajorVersion)
        {
            var ver = GetJavaMajorVersion(javaPath);
            return ver.HasValue && ver.Value >= minMajorVersion;
        }

        public static string? FindJava(int minMajorVersion = RequiredMajorVersion)
        {
            var cached = _cachedPath ?? TryReadCachedPath();
            if (!string.IsNullOrEmpty(cached) && File.Exists(cached) && IsVersionSufficient(cached, minMajorVersion))
                return cached;

            try
            {
                var bundled = GetBundledJavaFolder();
                if (Directory.Exists(bundled))
                {
                    var found = FindJavaInFolder(bundled, minMajorVersion);
                    if (found != null) { CachePath(found); return found; }
                }
            }
            catch { }

            try
            {
                var home = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (!string.IsNullOrEmpty(home))
                {
                    var path = Path.Combine(home, "bin", "java.exe");
                    if (File.Exists(path) && IsVersionSufficient(path, minMajorVersion)) { CachePath(path); return path; }
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
                            if (File.Exists(candidate) && IsVersionSufficient(candidate, minMajorVersion)) { CachePath(candidate); return candidate; }
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
                @"C:\Program Files\BellSoft",
                @"C:\Program Files\Semeru",
                @"C:\Program Files\OpenJDK",
                @"C:\Program Files (x86)\Java",
                @"C:\Program Files\Common Files\Oracle\Java",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"),
            };
            foreach (var basePath in commonPaths)
            {
                try
                {
                    if (!Directory.Exists(basePath)) continue;
                    var found = FindJavaInFolder(basePath, minMajorVersion);
                    if (found != null) { CachePath(found); return found; }
                }
                catch { }
            }

            return null;
        }

        private static string? FindJavaInFolder(string folder, int minMajorVersion)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(folder))
                {
                    var exe = Path.Combine(dir, "bin", "java.exe");
                    if (File.Exists(exe) && IsVersionSufficient(exe, minMajorVersion)) return exe;
                    var nested = FindJavaInFolder(dir, minMajorVersion);
                    if (nested != null) return nested;
                }
            }
            catch { }
            return null;
        }

        public static Task<string?> EnsureJavaAsync()
        {
            lock (_installLock)
            {
                if (_installTask != null && !_installTask.IsCompleted)
                    return _installTask;

                var found = FindJava();
                if (found != null)
                {
                    JavaReady?.Invoke(null, found);
                    return Task.FromResult<string?>(found);
                }

                InstallStateChanged?.Invoke(null, true);
                _installTask = Task.Run(async () =>
                {
                    try { return await DoInstallAsync(); }
                    catch (Exception ex) { Debug.WriteLine($"[JavaManager] Install error: {ex}"); return null; }
                    finally
                    {
                        InstallStateChanged?.Invoke(null, false);
                        JavaReady?.Invoke(null, _cachedPath);
                    }
                });
                return _installTask;
            }
        }

        public static void StartBackgroundInstallIfNeeded()
        {
            try
            {
                if (!string.IsNullOrEmpty(_cachedPath) && File.Exists(_cachedPath)) return;
                var found = FindJava();
                if (found != null) { _cachedPath = found; return; }

                _ = Task.Run(async () => { try { await EnsureJavaAsync(); } catch { } });
            }
            catch { }
        }

        private static async Task<string?> DoInstallAsync()
        {
            var target = GetBundledJavaFolder();

            foreach (var apiUrl in AdoptiumApis)
            {
                var r = await TryAdoptiumAsync(apiUrl, target);
                if (r != null) { CachePath(r); return r; }
            }

            foreach (var url in DirectZipUrls)
            {
                var r = await TryDirectZipAsync(url, target);
                if (r != null) { CachePath(r); return r; }
            }

            return null;
        }

        private static async Task<string?> TryAdoptiumAsync(string apiUrl, string target)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var json = await http.GetStringAsync(apiUrl);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
                var link = doc.RootElement[0].GetProperty("binary").GetProperty("package").GetProperty("link").GetString();
                if (string.IsNullOrEmpty(link)) return null;
                return await DownloadAndExtractAsync(http, link, target);
            }
            catch (Exception ex) { Debug.WriteLine($"[JavaManager] Adoptium failed: {ex.Message}"); return null; }
        }

        private static async Task<string?> TryDirectZipAsync(string url, string target)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                return await DownloadAndExtractAsync(http, url, target);
            }
            catch (Exception ex) { Debug.WriteLine($"[JavaManager] Direct failed: {ex.Message}"); return null; }
        }

        private static async Task<string?> DownloadAndExtractAsync(HttpClient http, string url, string target)
        {
            string? tempZip = null, extractTemp = null;
            try
            {
                if (Directory.Exists(target)) { try { Directory.Delete(target, true); } catch { } }
                Directory.CreateDirectory(target);

                tempZip = Path.Combine(Path.GetTempPath(), $"mvp-jre-{Guid.NewGuid()}.zip");

                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? 0;
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var fs = File.Create(tempZip);

                    var buffer = new byte[81920];
                    long read = 0;
                    int bytes;
                    while ((bytes = await stream.ReadAsync(buffer)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, bytes));
                        read += bytes;
                        if (total > 0) InstallProgress?.Invoke(null, (int)(read * 100 / total));
                    }
                }
                InstallProgress?.Invoke(null, 100);

                extractTemp = Path.Combine(Path.GetTempPath(), $"mvp-jre-x-{Guid.NewGuid()}");
                Directory.CreateDirectory(extractTemp);
                ZipFile.ExtractToDirectory(tempZip, extractTemp);

                string source = extractTemp;
                if (!File.Exists(Path.Combine(source, "bin", "java.exe")))
                {
                    foreach (var sub in Directory.GetDirectories(extractTemp))
                    {
                        if (File.Exists(Path.Combine(sub, "bin", "java.exe"))) { source = sub; break; }
                    }
                }

                foreach (var item in Directory.GetFileSystemEntries(source))
                {
                    var dest = Path.Combine(target, Path.GetFileName(item));
                    if (Directory.Exists(item)) CopyDirectory(item, dest);
                    else File.Copy(item, dest, true);
                }

                var javaExe = Path.Combine(target, "bin", "java.exe");
                if (!File.Exists(javaExe))
                {
                    var found = FindJavaInFolder(target, RequiredMajorVersion);
                    if (found != null) javaExe = found;
                }

                if (File.Exists(javaExe) && IsVersionSufficient(javaExe, RequiredMajorVersion))
                {
                    AddToUserPath(Path.GetDirectoryName(javaExe)!);
                    return javaExe;
                }
                return null;
            }
            catch (Exception ex) { Debug.WriteLine($"[JavaManager] Download/extract failed: {ex.Message}"); return null; }
            finally
            {
                if (tempZip != null) try { File.Delete(tempZip); } catch { }
                if (extractTemp != null) try { Directory.Delete(extractTemp, true); } catch { }
            }
        }

        private static void AddToUserPath(string binPath)
        {
            try
            {
                var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
                var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                if (!parts.Any(p => string.Equals(p, binPath, StringComparison.OrdinalIgnoreCase)))
                {
                    parts.Add(binPath);
                    Environment.SetEnvironmentVariable("PATH", string.Join(";", parts), EnvironmentVariableTarget.User);
                }
            }
            catch { }
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}