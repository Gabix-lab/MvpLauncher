using System.Collections.Generic;
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