using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MvpLauncher
{
    public class ModrinthHit
    {
        public string ProjectId { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public string Author { get; set; } = "";
        public string ProjectType { get; set; } = "";
        public int Downloads { get; set; }
    }

    public class ModrinthProject
    {
        public string Id { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public string ProjectType { get; set; } = "";
    }

    public class ModrinthVersion
    {
        public string Id { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string Name { get; set; } = "";
        public string VersionNumber { get; set; } = "";
        public string GameVersion { get; set; } = "";
        public string Loader { get; set; } = "";
        public DateTimeOffset DatePublished { get; set; }
        public List<ModrinthFile> Files { get; set; } = new();
    }

    public class ModrinthFile
    {
        public string Url { get; set; } = "";
        public string Filename { get; set; } = "";
        public bool Primary { get; set; }
    }

    public class ModrinthService
    {
        private const string ApiBase = "https://api.modrinth.com/v2";
        private const string UserAgent = "MvpLauncher/1.0.0 (contact@example.com)";

        private readonly HttpClient _httpClient;

        public ModrinthService()
        {
            _httpClient = MvpLauncher.Infrastructure.HttpClients.Shared;
        }

        private static async Task<string> GetStringWithRetryAsync(HttpClient http, string url)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return await http.GetStringAsync(url);
                }
                catch (HttpRequestException) when (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
                }
            }
            return await http.GetStringAsync(url);
        }

        /// <summary>
        /// Looks up an exact project by its Modrinth ID or slug. Unlike search this
        /// does not return a "close enough" project, which makes dependency
        /// resolution predictable.
        /// </summary>
        public async Task<ModrinthProject?> GetProjectAsync(string idOrSlug)
        {
            if (string.IsNullOrWhiteSpace(idOrSlug)) return null;

            try
            {
                string url = $"{ApiBase}/project/{Uri.EscapeDataString(idOrSlug.Trim())}";
                using var resp = await _httpClient.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var project = new ModrinthProject
                {
                    Id = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Slug = root.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                    Title = root.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    Description = root.TryGetProperty("description", out var description)
                        ? description.GetString() ?? ""
                        : "",
                    IconUrl = root.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? "" : "",
                    ProjectType = root.TryGetProperty("project_type", out var projectType)
                        ? projectType.GetString() ?? ""
                        : ""
                };

                return string.IsNullOrWhiteSpace(project.Id) && string.IsNullOrWhiteSpace(project.Slug)
                    ? null
                    : project;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Modrinth] Projekt lekérés hiba ({idOrSlug}): {ex.Message}");
                return null;
            }
        }

        public async Task<List<ModrinthHit>> SearchAsync(
            string query,
            string gameVersion = "1.19.4",
            string loader = "fabric",
            int limit = 20)
        {
            var results = new List<ModrinthHit>();

            try
            {
                var facets = $"[[" +
                    $"\"categories:{loader}\"," +
                    $"\"versions:{gameVersion}\"," +
                    $"\"project_type:mod\"" +
                    $"]]";

                string url = $"{ApiBase}/search?query={Uri.EscapeDataString(query)}" +
                             $"&limit={limit}" +
                             $"&index=relevance" +
                             $"&facets={Uri.EscapeDataString(facets)}";

                var response = await GetStringWithRetryAsync(_httpClient, url);
                using var doc = JsonDocument.Parse(response);

                if (!doc.RootElement.TryGetProperty("hits", out var hits)) return results;

                foreach (var hit in hits.EnumerateArray())
                {
                    results.Add(new ModrinthHit
                    {
                        ProjectId = hit.TryGetProperty("project_id", out var pid) ? pid.GetString() ?? "" : "",
                        Slug = hit.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                        Title = hit.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                        Description = hit.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                        IconUrl = hit.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? "" : "",
                        Author = hit.TryGetProperty("author", out var author) ? author.GetString() ?? "" : "",
                        ProjectType = hit.TryGetProperty("project_type", out var ptype) ? ptype.GetString() ?? "" : "",
                        Downloads = hit.TryGetProperty("downloads", out var dl) ? dl.GetInt32() : 0
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Modrinth] Keresés hiba: {ex.Message}");
            }

            return results;
        }

        public async Task<List<ModrinthVersion>> GetVersionsAsync(
            string projectId,
            string gameVersion = "1.19.4",
            string loader = "fabric")
        {
            var versions = new List<ModrinthVersion>();

            try
            {
                string url = $"{ApiBase}/project/{projectId}/version" +
                             $"?game_versions=[\"{gameVersion}\"]" +
                             $"&loaders=[\"{loader}\"]";

                var response = await GetStringWithRetryAsync(_httpClient, url);
                using var doc = JsonDocument.Parse(response);

                foreach (var ver in doc.RootElement.EnumerateArray())
                {
                    var version = new ModrinthVersion
                    {
                        Id = ver.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        ProjectId = ver.TryGetProperty("project_id", out var pid) ? pid.GetString() ?? "" : "",
                        Name = ver.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        VersionNumber = ver.TryGetProperty("version_number", out var vn) ? vn.GetString() ?? "" : ""
                    };

                    if (ver.TryGetProperty("date_published", out var dateEl) &&
                        dateEl.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(dateEl.GetString(), out var published))
                    {
                        version.DatePublished = published;
                    }

                    if (ver.TryGetProperty("game_versions", out var gv) && gv.GetArrayLength() > 0)
                        version.GameVersion = gv[0].GetString() ?? "";

                    if (ver.TryGetProperty("loaders", out var ld) && ld.GetArrayLength() > 0)
                        version.Loader = ld[0].GetString() ?? "";

                    if (ver.TryGetProperty("files", out var files))
                    {
                        foreach (var file in files.EnumerateArray())
                        {
                            version.Files.Add(new ModrinthFile
                            {
                                Url = file.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                                Filename = file.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "",
                                Primary = file.TryGetProperty("primary", out var prim) && prim.GetBoolean()
                            });
                        }
                    }

                    versions.Add(version);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Modrinth] Verzió lekérés hiba: {ex.Message}");
            }

            return versions;
        }

        public async Task<string?> DownloadModAsync(
            ModrinthVersion version,
            string targetFolder,
            IProgress<string>? progress = null)
        {
            try
            {
                if (version.Files == null || version.Files.Count == 0)
                {
                    progress?.Report("HIBA: a verziónak nincs letölthető fájlja");
                    return null;
                }

                var primaryFile = version.Files.Find(f => f.Primary) ?? version.Files[0];
                if (string.IsNullOrEmpty(primaryFile.Url)) return null;

                var fileName = Path.GetFileName(primaryFile.Filename);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    progress?.Report("HIBA: érvénytelen fájlnév");
                    return null;
                }

                string destPath = Path.Combine(targetFolder, fileName);
                progress?.Report($"Letöltés: {fileName}");

                using var resp = await _httpClient.GetAsync(primaryFile.Url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                Directory.CreateDirectory(targetFolder);
                await using (var src = await resp.Content.ReadAsStreamAsync())
                await using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }

                var fi = new FileInfo(destPath);
                if (fi.Length < 4)
                {
                    try { File.Delete(destPath); } catch { }
                    progress?.Report($"HIBA: a letöltött fájl üres: {fileName}");
                    return null;
                }

                // A Modrinth file is a jar (zip) file. Reject HTML error pages and
                // other invalid downloads before they end up in the mods folder.
                var header = new byte[4];
                using (var check = File.OpenRead(destPath))
                {
                    if (check.Read(header, 0, header.Length) < 4 ||
                        header[0] != 0x50 || header[1] != 0x4B)
                    {
                        try { File.Delete(destPath); } catch { }
                        progress?.Report($"HIBA: nem érvényes jar fájl: {fileName}");
                        return null;
                    }
                }

                progress?.Report($"Letöltve: {fileName}");
                return destPath;
            }
            catch (Exception ex)
            {
                progress?.Report($"HIBA: {ex.Message}");
                return null;
            }
        }
    }
}