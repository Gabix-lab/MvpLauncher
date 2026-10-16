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

    public class ModrinthVersion
    {
        public string Id { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string Name { get; set; } = "";
        public string VersionNumber { get; set; } = "";
        public string GameVersion { get; set; } = "";
        public string Loader { get; set; } = "";
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
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
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

                var response = await _httpClient.GetStringAsync(url);
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

                var response = await _httpClient.GetStringAsync(url);
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
                var primaryFile = version.Files.Find(f => f.Primary)
                                  ?? version.Files[0];

                if (string.IsNullOrEmpty(primaryFile.Url)) return null;

                string destPath = Path.Combine(targetFolder, primaryFile.Filename);

                progress?.Report($"Letöltés: {primaryFile.Filename}");

                var bytes = await _httpClient.GetByteArrayAsync(primaryFile.Url);

                if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
                {
                    progress?.Report($"HIBA: nem jar fájl: {primaryFile.Filename}");
                    return null;
                }

                Directory.CreateDirectory(targetFolder);
                File.WriteAllBytes(destPath, bytes);

                progress?.Report($"Letöltve: {primaryFile.Filename} ({bytes.Length} byte)");
                return destPath;
            }
            catch (Exception ex)
            {
                progress?.Report($"HIBA letöltés közben: {ex.Message}");
                return null;
            }
        }
    }
}