using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace MvpLauncher
{
    public static class AutoModInstaller
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private const string UserAgent = "MvpLauncher/1.0.12";

        public static async Task EnsureRequiredModsAsync(string modsFolder, IProgress<string>? progress = null)
        {
            try { Directory.CreateDirectory(modsFolder); } catch { return; }

            await EnsureModAsync(modsFolder, "fabric-api", "Fabric API", progress);
            await EnsureModAsync(modsFolder, "satin-api", "Satin API", progress);
        }

        private static bool HasValidFile(string modsFolder, string slug)
        {
            try
            {
                var files = Directory.GetFiles(modsFolder, "*.jar")
                    .Concat(Directory.GetFiles(modsFolder, "*.jar.disabled"));

                foreach (var f in files)
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    if (!name.Contains(slug.ToLowerInvariant())) continue;

                    try
                    {
                        var info = new FileInfo(f);
                        if (info.Length > 1024) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private static async Task EnsureModAsync(string modsFolder, string slug, string displayName, IProgress<string>? progress)
        {
            try
            {
                if (HasValidFile(modsFolder, slug))
                {
                    progress?.Report($"{displayName}: már telepítve");
                    return;
                }

                progress?.Report($"{displayName}: keresés...");

                string? projectId = await TryGetProjectIdAsync(slug, displayName);

                if (string.IsNullOrEmpty(projectId))
                {
                    progress?.Report($"{displayName}: nem található a Modrinthon");
                    Debug.WriteLine($"[AutoModInstaller] {displayName}: project nem található (slug: {slug})");
                    return;
                }

                progress?.Report($"{displayName}: verzió keresése...");

                var (downloadUrl, filename) = await TryGetVersionDownloadAsync(projectId, displayName);

                if (string.IsNullOrEmpty(downloadUrl) || string.IsNullOrEmpty(filename))
                {
                    progress?.Report($"{displayName}: nincs 1.19.4 Fabric verzió");
                    Debug.WriteLine($"[AutoModInstaller] {displayName}: nincs megfelelő verzió");
                    return;
                }

                progress?.Report($"{displayName}: letöltés ({filename})...");

                byte[] bytes;
                using (var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
                {
                    req.Headers.Add("User-Agent", UserAgent);
                    using var resp = await _http.SendAsync(req);
                    resp.EnsureSuccessStatusCode();
                    bytes = await resp.Content.ReadAsByteArrayAsync();
                }

                if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
                {
                    progress?.Report($"{displayName}: a letöltött fájl nem érvényes jar");
                    Debug.WriteLine($"[AutoModInstaller] {displayName}: nem jar fájl ({bytes.Length} byte)");
                    return;
                }

                string destPath = Path.Combine(modsFolder, filename);
                File.WriteAllBytes(destPath, bytes);

                progress?.Report($"{displayName}: letöltve ({bytes.Length / 1024} KB)");
                Debug.WriteLine($"[AutoModInstaller] {displayName}: OK - {destPath}");
            }
            catch (Exception ex)
            {
                progress?.Report($"{displayName}: HIBA - {ex.Message}");
                Debug.WriteLine($"[AutoModInstaller] {displayName} HIBA: {ex}");
            }
        }

        private static async Task<string?> TryGetProjectIdAsync(string slug, string displayName)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.modrinth.com/v2/project/{slug}");
                req.Headers.Add("User-Agent", UserAgent);
                using var resp = await _http.SendAsync(req);

                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            Debug.WriteLine($"[AutoModInstaller] {slug}: direkt slug OK -> {id}");
                            return id;
                        }
                    }
                }
                else
                {
                    Debug.WriteLine($"[AutoModInstaller] {slug}: direkt slug {resp.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoModInstaller] {slug}: direkt slug hiba - {ex.Message}");
            }

            try
            {
                string url = $"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(displayName)}&limit=10&facets=%5B%5B%22project_type%3Amod%22%5D%5D";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", UserAgent);
                using var resp = await _http.SendAsync(req);

                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);

                    if (doc.RootElement.TryGetProperty("hits", out var hits))
                    {
                        foreach (var hit in hits.EnumerateArray())
                        {
                            var title = hit.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                            var hitSlug = hit.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "";

                            bool match = title.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                                         hitSlug.Equals(slug, StringComparison.OrdinalIgnoreCase) ||
                                         hitSlug.StartsWith(slug + "-", StringComparison.OrdinalIgnoreCase);

                            if (match)
                            {
                                var pid = hit.TryGetProperty("project_id", out var p) ? p.GetString() : null;
                                if (!string.IsNullOrEmpty(pid))
                                {
                                    Debug.WriteLine($"[AutoModInstaller] {displayName}: keresés -> {pid} ({title})");
                                    return pid;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoModInstaller] {displayName}: keresés hiba - {ex.Message}");
            }

            return null;
        }

        private static async Task<(string? url, string? filename)> TryGetVersionDownloadAsync(string projectId, string displayName)
        {
            try
            {
                string url = $"https://api.modrinth.com/v2/project/{projectId}/version";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", UserAgent);
                using var resp = await _http.SendAsync(req);

                if (!resp.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[AutoModInstaller] {displayName}: version API {resp.StatusCode}");
                    return (null, null);
                }

                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return (null, null);
                }

                int count = doc.RootElement.GetArrayLength();
                Debug.WriteLine($"[AutoModInstaller] {displayName}: {count} verzió elérhető");

                foreach (var ver in doc.RootElement.EnumerateArray())
                {
                    bool okGame = false;
                    if (ver.TryGetProperty("game_versions", out var gv))
                    {
                        foreach (var v in gv.EnumerateArray())
                        {
                            if (v.GetString() == "1.19.4") { okGame = true; break; }
                        }
                    }
                    if (!okGame) continue;

                    bool okLoader = false;
                    if (ver.TryGetProperty("loaders", out var ld))
                    {
                        foreach (var l in ld.EnumerateArray())
                        {
                            if (l.GetString() == "fabric") { okLoader = true; break; }
                        }
                    }
                    if (!okLoader) continue;

                    string? versionNumber = ver.TryGetProperty("version_number", out var vn)
                        ? vn.GetString() : "?";

                    if (ver.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                    {
                        string? url2 = null;
                        string? fn2 = null;

                        foreach (var file in files.EnumerateArray())
                        {
                            if (file.TryGetProperty("primary", out var p) && p.GetBoolean())
                            {
                                url2 = file.GetProperty("url").GetString();
                                fn2 = file.GetProperty("filename").GetString();
                                break;
                            }
                        }

                        if (string.IsNullOrEmpty(url2))
                        {
                            var f0 = files[0];
                            url2 = f0.GetProperty("url").GetString();
                            fn2 = f0.GetProperty("filename").GetString();
                        }

                        if (!string.IsNullOrEmpty(url2) && !string.IsNullOrEmpty(fn2))
                        {
                            Debug.WriteLine($"[AutoModInstaller] {displayName}: kiválasztva {versionNumber} -> {fn2}");
                            return (url2, fn2);
                        }
                    }
                }

                Debug.WriteLine($"[AutoModInstaller] {displayName}: nincs 1.19.4 fabric verzió a listában");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoModInstaller] {displayName}: version feldolgozás hiba - {ex.Message}");
            }

            return (null, null);
        }
    }
}