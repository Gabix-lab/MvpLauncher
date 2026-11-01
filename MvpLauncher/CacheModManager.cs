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
        public string GitHubRepo { get; set; } = "";
    }

    public class CacheModManager
    {
        private readonly string _cacheFolder;
        private readonly string _hashFolder;
        private readonly HttpClient _httpClient = MvpLauncher.Infrastructure.HttpClients.Shared;

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

        private async Task<(string? version, string? url, string? fileName, string? sha256)>
            GetLatestReleaseAsync(string repo, IProgress<string>? progress = null)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{repo}/releases/latest");
                req.Headers.Add("Accept", "application/vnd.github+json");

                using var resp = await _httpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    progress?.Report($"GitHub: {(int)resp.StatusCode}");
                    return (null, null, null, null);
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;

                string? version = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;

                string? url = null, fileName = null, sha256 = null;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

                        url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        fileName = name;

                        if (a.TryGetProperty("digest", out var d))
                        {
                            var digest = d.GetString();
                            if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                                sha256 = digest.Substring(7);
                        }
                        break;
                    }
                }

                return (version, url, fileName, sha256);
            }
            catch (Exception ex)
            {
                progress?.Report($"GitHub HIBA: {ex.Message}");
                return (null, null, null, null);
            }
        }

        private string VersionFile(string name) => Path.Combine(_hashFolder, name + ".version");

        private string? ReadVersion(string name)
        {
            try { var p = VersionFile(name); if (File.Exists(p)) return File.ReadAllText(p).Trim(); }
            catch { }
            return null;
        }

        private void WriteVersion(string name, string v)
        {
            try { Directory.CreateDirectory(_hashFolder); File.WriteAllText(VersionFile(name), v); }
            catch { }
        }

        public async Task FetchVersionAsync()
        {
            foreach (var mod in Mods)
            {
                if (!string.IsNullOrEmpty(mod.GitHubRepo))
                {
                    var (v, _, _, _) = await GetLatestReleaseAsync(mod.GitHubRepo);
                    if (!string.IsNullOrEmpty(v) && v != CurrentVersion)
                    {
                        CurrentVersion = v;
                        VersionChanged?.Invoke(this, EventArgs.Empty);
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(mod.VersionUrl)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(await _httpClient.GetStringAsync(mod.VersionUrl));
                    if (doc.RootElement.TryGetProperty("version", out var el))
                    {
                        var v = el.GetString();
                        if (!string.IsNullOrEmpty(v) && v != CurrentVersion)
                        {
                            CurrentVersion = v;
                            VersionChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                catch { }
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
                progress?.Report($"HIBA: {ex.Message}");
                return false;
            }

            bool ok = false;

            foreach (var mod in Mods)
            {
                string cachePathCheck = Path.Combine(_cacheFolder, mod.FileName);
                string disabledPathCheck = cachePathCheck + ".disabled";
                if (File.Exists(disabledPathCheck))
                {
                    progress?.Report($"Cache OK (kikapcsolva): {mod.FileName}");
                    ok = true;
                    continue;
                }

                bool isGitHub = !string.IsNullOrEmpty(mod.GitHubRepo);

                string cachePath = Path.Combine(_cacheFolder, mod.FileName);
                string hashPath = Path.Combine(_hashFolder, mod.FileName + ".hash");

                string? remoteVersion = null, remoteHash = null, dlUrl = null;

                if (isGitHub)
                {
                    progress?.Report("Új mvp.jar ellenőrzése...");
                    var (v, u, _, sha) = await GetLatestReleaseAsync(mod.GitHubRepo, progress);
                    remoteVersion = v;
                    remoteHash = sha;
                    dlUrl = u;

                    if (string.IsNullOrEmpty(dlUrl))
                    {
                        progress?.Report("HIBA: nincs .jar a legfrissebb release-ben");
                        continue;
                    }
                }
                else if (!string.IsNullOrEmpty(mod.VersionUrl))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(await _httpClient.GetStringAsync(mod.VersionUrl));
                        if (doc.RootElement.TryGetProperty("hash", out var h)) remoteHash = h.GetString();
                        if (doc.RootElement.TryGetProperty("version", out var v)) remoteVersion = v.GetString();
                    }
                    catch (Exception ex) { progress?.Report($"HIBA verzió: {ex.Message}"); }
                }

                if (!string.IsNullOrEmpty(remoteVersion) && CurrentVersion != remoteVersion)
                {
                    CurrentVersion = remoteVersion;
                    VersionChanged?.Invoke(this, EventArgs.Empty);
                }

                bool needDownload = true;
                if (File.Exists(cachePath))
                {
                    bool hashOk = false, verOk = false;
                    if (!string.IsNullOrEmpty(remoteHash) && File.Exists(hashPath))
                    {
                        try
                        {
                            var local = File.ReadAllText(hashPath).Trim();
                            if (string.Equals(local, remoteHash, StringComparison.OrdinalIgnoreCase)) hashOk = true;
                        }
                        catch { }
                    }
                    if (!string.IsNullOrEmpty(remoteVersion))
                    {
                        var cached = ReadVersion(mod.FileName);
                        if (!string.IsNullOrEmpty(cached) &&
                            string.Equals(cached, remoteVersion, StringComparison.OrdinalIgnoreCase)) verOk = true;
                    }

                    if (hashOk || (string.IsNullOrEmpty(remoteHash) && verOk))
                    {
                        needDownload = false;
                        progress?.Report($"Cache OK: {mod.FileName} (v{remoteVersion})");
                        ok = true;
                    }
                }

                if (!needDownload) continue;

                byte[]? bytes = null;
                var urls = new List<string>();
                if (!string.IsNullOrEmpty(dlUrl)) urls.Add(dlUrl);
                if (!string.IsNullOrEmpty(mod.Url) && dlUrl != mod.Url) urls.Add(mod.Url);
                if (!string.IsNullOrEmpty(mod.FallbackUrl)) urls.Add(mod.FallbackUrl);

                foreach (var u in urls)
                {
                    progress?.Report(isGitHub ? "Új mvp.jar letöltése" : $"Letöltés: {u}");
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, u);
                        var resp = await _httpClient.SendAsync(req);
                        if (!resp.IsSuccessStatusCode) { progress?.Report($"HIBA: {resp.StatusCode}"); continue; }

                        var data = await resp.Content.ReadAsByteArrayAsync();
                        if (data.Length < 4 || data[0] != 0x50 || data[1] != 0x4B)
                        {
                            progress?.Report("HIBA: nem jar fájl");
                            continue;
                        }
                        bytes = data;
                        break;
                    }
                    catch (Exception ex) { progress?.Report($"HIBA: {ex.Message}"); }
                }

                if (bytes == null) { progress?.Report("HIBA: letöltés sikertelen"); continue; }

                try
                {
                    File.WriteAllBytes(cachePath, bytes);
                    if (!string.IsNullOrEmpty(remoteHash)) File.WriteAllText(hashPath, remoteHash);
                    else
                    {
                        using var sha = SHA256.Create();
                        File.WriteAllText(hashPath, Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant());
                    }
                    if (!string.IsNullOrEmpty(remoteVersion)) WriteVersion(mod.FileName, remoteVersion);

                    progress?.Report(isGitHub ? "mvp.jar letöltve" : $"Letöltve: {mod.FileName}");
                    ok = true;
                }
                catch (Exception ex) { progress?.Report($"HIBA mentés: {ex.Message}"); }
            }

            return ok;
        }

        public ModItem? GetCacheModItem()
        {
            if (Mods.Count == 0) return null;
            var mod = Mods[0];
            string cachePath = Path.Combine(_cacheFolder, mod.FileName);
            bool isEnabled = !File.Exists(cachePath + ".disabled");
            return new ModItem
            {
                DisplayName = mod.FileName,
                BaseName = mod.FileName,
                ModsFolder = _cacheFolder,
                IsFromCache = true,
                IsEnabled = isEnabled,
                IsLocked = true,
                Version = CurrentVersion
            };
        }

        public void ClearCache()
        {
            try { if (Directory.Exists(_cacheFolder)) Directory.Delete(_cacheFolder, true); }
            catch { }
        }
    }
}