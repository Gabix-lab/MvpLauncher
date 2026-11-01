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
        public List<string> Provides { get; } = new();
    }

    public static class FabricMetadataReader
    {
        private const int MaxNestedJarDepth = 3;

        private static readonly HashSet<string> BuiltInDependencies =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "minecraft",
                "java",
                "fabricloader",
                "fabric-loader",
                "fabric_loader"
            };

        public static FabricModInfo? Read(string jarPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(jarPath);
                return ReadFromArchive(archive, 0);
            }
            catch
            {
                return null;
            }
        }

        private static FabricModInfo? ReadFromArchive(ZipArchive archive, int depth)
        {
            try
            {
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

                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    info.Id = idEl.GetString() ?? "";

                if (root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    info.Name = nameEl.GetString() ?? "";

                if (root.TryGetProperty("version", out var verEl) && verEl.ValueKind == JsonValueKind.String)
                    info.Version = verEl.GetString() ?? "";

                if (root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
                    info.Description = descEl.GetString() ?? "";

                if (root.TryGetProperty("authors", out var authEl) && authEl.ValueKind == JsonValueKind.Array)
                {
                    var authors = new List<string>();
                    foreach (var author in authEl.EnumerateArray())
                    {
                        if (author.ValueKind == JsonValueKind.String)
                        {
                            var name = author.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) authors.Add(name!);
                        }
                        else if (author.ValueKind == JsonValueKind.Object &&
                                 author.TryGetProperty("name", out var nestedName) &&
                                 nestedName.ValueKind == JsonValueKind.String)
                        {
                            var name = nestedName.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) authors.Add(name!);
                        }
                    }

                    if (authors.Count > 0) info.Author = string.Join(", ", authors);
                }

                ReadIcon(archive, root, info);

                if (root.TryGetProperty("depends", out var depEl))
                    ReadDependencyIds(depEl, info.Depends);

                if (root.TryGetProperty("provides", out var provEl) && provEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var provided in provEl.EnumerateArray())
                    {
                        if (provided.ValueKind != JsonValueKind.String) continue;

                        var providedId = provided.GetString();
                        if (!string.IsNullOrWhiteSpace(providedId))
                            info.Provides.Add(providedId!);
                    }
                }

                // Fabric's jar-in-jar (JiJ) modules are loaded together with the parent mod.
                // Their IDs satisfy dependencies too, and their own required dependencies
                // must be present in the game as well.
                if (depth < MaxNestedJarDepth &&
                    root.TryGetProperty("jars", out var jarsEl) &&
                    jarsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var jarEl in jarsEl.EnumerateArray())
                    {
                        if (jarEl.ValueKind != JsonValueKind.Object) continue;
                        if (!jarEl.TryGetProperty("file", out var fileEl) ||
                            fileEl.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var nestedPath = fileEl.GetString();
                        if (string.IsNullOrWhiteSpace(nestedPath)) continue;

                        ReadNestedJar(archive, nestedPath!, depth + 1, info);
                    }
                }

                Normalize(info);
                return info;
            }
            catch
            {
                return null;
            }
        }

        private static void ReadIcon(ZipArchive archive, JsonElement root, FabricModInfo info)
        {
            try
            {
                if (!root.TryGetProperty("icon", out var iconEl) ||
                    iconEl.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                var iconPath = iconEl.GetString();
                if (string.IsNullOrWhiteSpace(iconPath)) return;

                var iconEntry = archive.GetEntry(iconPath);
                if (iconEntry == null) return;

                using var iconStream = iconEntry.Open();
                using var memory = new MemoryStream();
                iconStream.CopyTo(memory);

                var extension = Path.GetExtension(iconPath).ToLowerInvariant();
                if (extension == ".jpg") extension = ".jpeg";

                var mime = extension switch
                {
                    ".png" => "image/png",
                    ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    _ => "image/png"
                };

                info.IconUrl = $"data:{mime};base64,{Convert.ToBase64String(memory.ToArray())}";
            }
            catch
            {
                // An invalid icon must not prevent reading the rest of the mod.
            }
        }

        private static void ReadNestedJar(
            ZipArchive parentArchive,
            string nestedPath,
            int depth,
            FabricModInfo parentInfo)
        {
            try
            {
                var nestedEntry = parentArchive.GetEntry(nestedPath);
                if (nestedEntry == null) return;

                using var nestedStream = nestedEntry.Open();
                using var memory = new MemoryStream();
                nestedStream.CopyTo(memory);
                memory.Position = 0;

                using var nestedArchive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
                var nestedInfo = ReadFromArchive(nestedArchive, depth);
                if (nestedInfo == null) return;

                if (!string.IsNullOrWhiteSpace(nestedInfo.Id))
                    parentInfo.Provides.Add(nestedInfo.Id);

                foreach (var provided in nestedInfo.Provides)
                    parentInfo.Provides.Add(provided);

                foreach (var dependency in nestedInfo.Depends)
                    parentInfo.Depends.Add(dependency);
            }
            catch
            {
                // A broken nested jar must not prevent reading the parent mod.
            }
        }

        private static void ReadDependencyIds(JsonElement dependencyElement, List<string> target)
        {
            if (dependencyElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in dependencyElement.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(property.Name))
                        target.Add(property.Name);
                }

                return;
            }

            // Quilt-style metadata is not used by this launcher, but some hybrid mods
            // ship an array-shaped depends block. Accepting it makes detection more robust.
            if (dependencyElement.ValueKind != JsonValueKind.Array) return;

            foreach (var dependency in dependencyElement.EnumerateArray())
            {
                if (dependency.ValueKind == JsonValueKind.String)
                {
                    var id = dependency.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) target.Add(id!);
                }
                else if (dependency.ValueKind == JsonValueKind.Object &&
                         dependency.TryGetProperty("id", out var idEl) &&
                         idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) target.Add(id!);
                }
            }
        }

        private static void Normalize(FabricModInfo info)
        {
            var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(info.Id))
                provided.Add(info.Id);

            foreach (var provide in info.Provides)
                provided.Add(provide);

            info.Provides.Clear();
            foreach (var provide in provided)
                info.Provides.Add(provide);

            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in info.Depends)
            {
                if (string.IsNullOrWhiteSpace(dependency)) continue;
                if (BuiltInDependencies.Contains(dependency)) continue;
                if (provided.Contains(dependency)) continue;

                dependencies.Add(dependency);
            }

            info.Depends.Clear();
            foreach (var dependency in dependencies)
                info.Depends.Add(dependency);
        }
    }
}
