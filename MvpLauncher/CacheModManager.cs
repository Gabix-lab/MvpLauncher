using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace MvpLauncher
{
    public class CacheMod
    {
        public string FileName { get; set; } = "";
        public string Url { get; set; } = "";
        public string FallbackUrl { get; set; } = "";
        public string VersionUrl { get; set; } = "";
    }

    public class CacheModManager
    {
        private readonly string _cacheFolder;
        private readonly string _hashFolder;
        private readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        public List<CacheMod> Mods { get; } = new();
        public string CurrentVersion { get; private set; } = "";
        public string CacheFolderPath => _cacheFolder;

        public event EventHandler? VersionChanged;

        public CacheModManager(string cacheFolder)
        {
            _cacheFolder = cacheFolder;
            _hashFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".mvplauncher",
                ".cache-hashes");
        }

        public async Task FetchVersionAsync()
        {
            foreach (var mod in Mods)
            {
                if (string.IsNullOrEmpty(mod.VersionUrl)) continue;
                try
                {
                    var json = await _httpClient.GetStringAsync(mod.VersionUrl);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("version", out var verEl))
                    {
                        var v = verEl.GetString();
                        if (!string.IsNullOrEmpty(v) && v != CurrentVersion)
                        {
                            CurrentVersion = v;
                            VersionChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        public async Task<bool> PrepareAsync(IProgress<string>? progress = null)
        {
            try
            {
                Directory.CreateDirectory(_cacheFolder);
                Directory.CreateDirectory(_hashFolder);
            }
            catch (Exception ex)
            {
                progress?.Report($"HIBA a mappa létrehozásakor: {ex.Message}");
                return false;
            }

            bool anySuccess = false;

            foreach (var mod in Mods)
            {
                string cachePath = Path.Combine(_cacheFolder, mod.FileName);
                string hashPath = Path.Combine(_hashFolder, mod.FileName + ".hash");

                string? remoteHash = null;
                string? remoteVersion = null;

                if (!string.IsNullOrEmpty(mod.VersionUrl))
                {
                    try
                    {
                        progress?.Report($"Verzió ellenőrzése: {mod.FileName}");
                        var json = await _httpClient.GetStringAsync(mod.VersionUrl);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("hash", out var hashEl))
                            remoteHash = hashEl.GetString();
                        if (doc.RootElement.TryGetProperty("version", out var verEl))
                            remoteVersion = verEl.GetString();
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"HIBA verzió lekérés: {ex.Message}");
                    }
                }

                if (!string.IsNullOrEmpty(remoteVersion))
                    CurrentVersion = remoteVersion;

                bool needsDownload = true;

                if (File.Exists(cachePath) && remoteHash != null && File.Exists(hashPath))
                {
                    try
                    {
                        string localHash = File.ReadAllText(hashPath).Trim();
                        if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                        {
                            needsDownload = false;
                            progress?.Report($"Cache használatban: {mod.FileName}");
                            anySuccess = true;
                        }
                    }
                    catch { }
                }

                if (needsDownload)
                {
                    byte[]? bytes = null;
                    string? usedUrl = null;

                    var urlsToTry = new List<string>();
                    if (!string.IsNullOrEmpty(mod.Url)) urlsToTry.Add(mod.Url);
                    if (!string.IsNullOrEmpty(mod.FallbackUrl)) urlsToTry.Add(mod.FallbackUrl);

                    foreach (var url in urlsToTry)
                    {
                        progress?.Report($"Letöltés: {url}");
                        try
                        {
                            var response = await _httpClient.GetAsync(url);
                            if (!response.IsSuccessStatusCode)
                            {
                                progress?.Report($"HIBA: {response.StatusCode} - {url}");
                                continue;
                            }

                            var data = await response.Content.ReadAsByteArrayAsync();

                            if (data.Length < 4 || data[0] != 0x50 || data[1] != 0x4B)
                            {
                                string preview = data.Length > 0
                                    ? System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(150, data.Length))
                                    : "(üres)";
                                progress?.Report($"HIBA: nem jar! Válasz: {preview}");
                                continue;
                            }

                            bytes = data;
                            usedUrl = url;
                            break;
                        }
                        catch (Exception ex)
                        {
                            progress?.Report($"HIBA: {ex.Message}");
                        }
                    }

                    if (bytes == null)
                    {
                        progress?.Report($"HIBA: egyik URL sem működik: {mod.FileName}");
                        continue;
                    }

                    try
                    {
                        File.WriteAllBytes(cachePath, bytes);

                        if (remoteHash != null)
                            File.WriteAllText(hashPath, remoteHash);
                        else
                        {
                            using var sha = SHA256.Create();
                            var localHash = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
                            File.WriteAllText(hashPath, localHash);
                        }

                        progress?.Report($"Letöltve: {mod.FileName} ({bytes.Length} byte) [{usedUrl}]");
                        anySuccess = true;
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"HIBA mentés közben: {ex.Message}");
                        continue;
                    }
                }
            }

            return anySuccess;
        }

        public ModItem? GetCacheModItem()
        {
            if (Mods.Count == 0) return null;

            var mod = Mods[0];
            return new ModItem
            {
                DisplayName = mod.FileName,
                BaseName = mod.FileName,
                ModsFolder = _cacheFolder,
                IsFromCache = true,
                IsEnabled = true,
                IsLocked = true,
                Version = CurrentVersion
            };
        }

        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(_cacheFolder))
                    Directory.Delete(_cacheFolder, true);
            }
            catch { }
        }
    }
}