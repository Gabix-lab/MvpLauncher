using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MvpLauncher
{
    public class FabricModInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Author { get; set; } = "Unknown";
        public string Description { get; set; } = "No description available.";
        public string IconUrl { get; set; } = "";
        public List<string> Depends { get; } = new();
    }

    public static class FabricMetadataReader
    {
        public static FabricModInfo? Read(string jarPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(jarPath);
                var entry = archive.GetEntry("fabric.mod.json");
                if (entry == null) return null;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();

                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                var root = doc.RootElement;
                var info = new FabricModInfo();

                if (root.TryGetProperty("id", out var idEl))
                    info.Id = idEl.GetString() ?? "";

                if (root.TryGetProperty("name", out var nameEl))
                    info.Name = nameEl.GetString() ?? "";

                if (root.TryGetProperty("version", out var verEl))
                    info.Version = verEl.GetString() ?? "";

                if (root.TryGetProperty("description", out var descEl))
                    info.Description = descEl.GetString() ?? "";

                if (root.TryGetProperty("authors", out var authEl) && authEl.ValueKind == JsonValueKind.Array)
                {
                    var authors = new List<string>();
                    foreach (var a in authEl.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String) authors.Add(a.GetString() ?? "");
                        else if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var n)) authors.Add(n.GetString() ?? "");
                    }
                    if (authors.Count > 0) info.Author = string.Join(", ", authors);
                }

                if (root.TryGetProperty("icon", out var iconEl) && iconEl.ValueKind == JsonValueKind.String)
                {
                    var iconPath = iconEl.GetString();
                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        var iconEntry = archive.GetEntry(iconPath);
                        if (iconEntry != null)
                        {
                            using var iconStream = iconEntry.Open();
                            using var ms = new MemoryStream();
                            iconStream.CopyTo(ms);
                            var ext = Path.GetExtension(iconPath).ToLowerInvariant();
                            if (ext == ".jpg") ext = ".jpeg";
                            var mime = ext switch { ".png" => "image/png", ".jpeg" => "image/jpeg", ".gif" => "image/gif", _ => "image/png" };
                            info.IconUrl = $"data:{mime};base64,{Convert.ToBase64String(ms.ToArray())}";
                        }
                    }
                }

                if (root.TryGetProperty("depends", out var depEl) && depEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in depEl.EnumerateObject())
                    {
                        var depId = prop.Name;
                        if (depId == "minecraft" || depId == "java" || depId == "fabricloader") continue;
                        info.Depends.Add(depId);
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
        }
    }
}