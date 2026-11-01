using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MvpLauncher
{
    /// <summary>
    /// Matches the dependency IDs found in fabric.mod.json files against installed mods.
    /// Handles Fabric API sub-modules, jar-in-jar modules, common ID aliases and versioned
    /// mod IDs (for example cloth-config2 -> cloth-config).
    /// </summary>
    public sealed class ModDependencyResolver
    {
        private static readonly HashSet<string> BuiltInDependencies =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "minecraft",
                "java",
                "fabricloader",
                "fabric-loader",
                "fabric_loader"
            };

        // The first candidate is usually the Modrinth slug, the other ones are IDs
        // used by the mod itself. Both are useful: the slug is needed for downloads,
        // while the mod ID is needed to recognize an already installed dependency.
        private static readonly Dictionary<string, string[]> Aliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["fabric"] = new[] { "fabric-api" },
                ["cloth-config2"] = new[] { "cloth-config" },
                ["mod-menu"] = new[] { "modmenu" },
                ["pal"] = new[] { "pal", "playerabilitylib" },
                ["playerabilitylib"] = new[] { "pal", "playerabilitylib" },
                ["trinkets-api"] = new[] { "trinkets" },
                ["lithium-api"] = new[] { "lithium" },
                ["sodium-api"] = new[] { "sodium" },
                ["indium-api"] = new[] { "indium" },
                ["emi-api"] = new[] { "emi" },
                ["iris-api"] = new[] { "iris" },
                ["rei-api"] = new[] { "rei", "roughly-enough-items" },
                ["roughly-enough-items"] = new[] { "rei" },
                ["roughly-enough-items-api"] = new[] { "rei", "roughly-enough-items" },
                ["satin"] = new[] { "satin-api" },
                ["satin-api"] = new[] { "satin-api", "satin" },
                ["geckolib3"] = new[] { "geckolib" },
                ["geckolib-api"] = new[] { "geckolib" },
                ["cardinal-components"] = new[] { "cardinal-components-api" },
                ["cca"] = new[] { "cardinal-components-api" },
                ["architectury"] = new[] { "architectury-api", "architectury" },
                ["architectury-api"] = new[] { "architectury-api", "architectury" },
                ["yacl"] = new[] { "yacl", "yet_another_config_lib_v3" },
                ["yet-another-config-lib"] = new[] { "yacl", "yet_another_config_lib_v3" },
                ["yet-another-config-lib-v3"] = new[] { "yacl", "yet_another_config_lib_v3" }
            };

        private static readonly Regex VersionSuffixRegex = new(
            @"^(?<base>.+?)-?v?\d+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FabricApiModuleRegex = new(
            @"^(fabric-api-.+|fabric-.+-v\d+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly List<ModItem> _mods;
        private readonly Dictionary<string, ModItem> _providers;
        private ModItem? _fabricApi;

        public ModDependencyResolver(IEnumerable<ModItem> mods)
        {
            _mods = mods.ToList();
            _providers = new Dictionary<string, ModItem>(StringComparer.OrdinalIgnoreCase);

            // Exact mod IDs take precedence over every generated/alias key.
            foreach (var mod in _mods)
            {
                if (string.IsNullOrWhiteSpace(mod.ModId)) continue;
                _providers[mod.ModId.Trim()] = mod;
            }

            // IDs declared through "provides" are the next most authoritative keys.
            foreach (var mod in _mods)
            {
                foreach (var provide in mod.Provides)
                {
                    if (string.IsNullOrWhiteSpace(provide)) continue;
                    var key = provide.Trim();
                    if (!_providers.ContainsKey(key))
                        _providers[key] = mod;
                }

                if (IsFabricApiMod(mod))
                    _fabricApi = mod;
            }

            // Generated keys are normalized variants and aliases. They only fill gaps,
            // so two mods can never silently overwrite each other's real ID.
            var snapshot = _providers.ToList();
            foreach (var pair in snapshot)
            {
                foreach (var key in GetLookupKeys(pair.Key))
                {
                    if (!_providers.ContainsKey(key))
                        _providers[key] = pair.Value;
                }
            }
        }

        public static bool IsBuiltInDependency(string dependencyId)
        {
            return !string.IsNullOrWhiteSpace(dependencyId) &&
                   BuiltInDependencies.Contains(dependencyId.Trim());
        }

        public static string Normalize(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";

            var builder = new StringBuilder(id.Length);
            bool pendingSeparator = false;

            foreach (var c in id)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (pendingSeparator && builder.Length > 0)
                        builder.Append('-');

                    builder.Append(char.ToLowerInvariant(c));
                    pendingSeparator = false;
                }
                else
                {
                    pendingSeparator = true;
                }
            }

            return builder.ToString();
        }

        public static bool IsFabricApiModule(string? dependencyId)
        {
            var normalized = Normalize(dependencyId);
            if (normalized.Length == 0) return false;

            return normalized == "fabric-api" ||
                   normalized == "fabric" ||
                   FabricApiModuleRegex.IsMatch(normalized);
        }

        /// <summary>
        /// Returns the canonical ID that should be shown/downloaded for a missing
        /// dependency. Fabric API submodules are all satisfied by the Fabric API
        /// project itself, and known aliases are collapsed to one project so the
        /// same dependency is not downloaded twice under two different IDs.
        /// </summary>
        public static string GetMissingDependencyId(string dependencyId)
        {
            if (IsFabricApiModule(dependencyId))
                return "fabric-api";

            var normalized = Normalize(dependencyId);
            if (normalized.Length == 0) return "";

            if (Aliases.TryGetValue(normalized, out var aliases) && aliases.Length > 0)
            {
                var canonical = Normalize(aliases[0]);
                if (canonical.Length > 0)
                    return canonical;
            }

            return dependencyId.Trim();
        }

        /// <summary>
        /// All IDs that can meaningfully match this dependency: normalized ID,
        /// known aliases and version-stripped variants.
        /// </summary>
        public static IEnumerable<string> GetLookupKeys(string? dependencyId)
        {
            var normalized = Normalize(dependencyId);
            if (normalized.Length == 0) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>();
            pending.Enqueue(normalized);

            int guard = 0;
            while (pending.Count > 0 && guard++ < 64)
            {
                var current = pending.Dequeue();
                if (!seen.Add(current)) continue;

                yield return current;

                foreach (var variant in GetSuffixVariants(current))
                {
                    if (!seen.Contains(variant))
                        pending.Enqueue(variant);
                }

                if (!Aliases.TryGetValue(current, out var aliases)) continue;

                foreach (var alias in aliases)
                {
                    var aliasKey = Normalize(alias);
                    if (aliasKey.Length > 0 && !seen.Contains(aliasKey))
                        pending.Enqueue(aliasKey);
                }
            }
        }

        /// <summary>
        /// Candidate values for a Modrinth project lookup. The first entry is the
        /// most likely slug, the rest are fallbacks.
        /// </summary>
        public static IEnumerable<string> GetProjectCandidates(string dependencyId)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (IsFabricApiModule(dependencyId))
            {
                seen.Add("fabric-api");
                yield return "fabric-api";
            }

            if (!string.IsNullOrWhiteSpace(dependencyId))
            {
                var raw = dependencyId.Trim();
                if (seen.Add(raw)) yield return raw;
            }

            foreach (var key in GetLookupKeys(dependencyId))
            {
                if (seen.Add(key)) yield return key;
            }
        }

        public static bool MatchesDependencyName(string dependencyId, params string?[] candidateNames)
        {
            var dependencyKeys = new HashSet<string>(
                GetLookupKeys(dependencyId),
                StringComparer.OrdinalIgnoreCase);

            if (dependencyKeys.Count == 0) return false;

            foreach (var candidate in candidateNames)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                foreach (var key in GetLookupKeys(candidate))
                {
                    if (dependencyKeys.Contains(key))
                        return true;
                }
            }

            return false;
        }

        public ModItem? FindProvider(string dependencyId)
        {
            if (string.IsNullOrWhiteSpace(dependencyId)) return null;
            if (IsBuiltInDependency(dependencyId)) return null;

            var trimmed = dependencyId.Trim();
            if (_providers.TryGetValue(trimmed, out var exact))
                return exact;

            foreach (var key in GetLookupKeys(trimmed))
            {
                if (_providers.TryGetValue(key, out var provider))
                    return provider;
            }

            if (IsFabricApiModule(trimmed) && _fabricApi != null)
                return _fabricApi;

            return null;
        }

        /// <summary>
        /// Rebuilds the direct dependent list and the per-mod missing dependency list.
        /// </summary>
        public void BuildGraph()
        {
            foreach (var mod in _mods)
            {
                mod.Dependents.Clear();
                mod.MissingDependencies.Clear();
            }

            foreach (var mod in _mods)
            {
                var seenDependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var dependencyId in mod.Dependencies)
                {
                    if (IsBuiltInDependency(dependencyId)) continue;

                    var provider = FindProvider(dependencyId);
                    if (provider != null)
                    {
                        if (ReferenceEquals(provider, mod)) continue;

                        if (seenDependents.Add(provider.DisplayName) &&
                            !provider.Dependents.Contains(mod.DisplayName))
                        {
                            provider.Dependents.Add(mod.DisplayName);
                        }

                        continue;
                    }

                    var missingId = GetMissingDependencyId(dependencyId);
                    if (missingId.Length > 0 && seenMissing.Add(missingId))
                        mod.MissingDependencies.Add(missingId);
                }
            }

            foreach (var mod in _mods)
                mod.RefreshDependencyText();
        }

        /// <summary>
        /// Missing dependencies needed by the currently enabled mods, recursively.
        /// IDs are deduplicated and already normalized for downloading.
        /// </summary>
        public HashSet<string> GetMissingDependencies(bool onlyEnabled = true)
        {
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<ModItem>();

            foreach (var mod in _mods)
            {
                if (mod.IsFromCache) continue;
                if (onlyEnabled && !mod.IsEnabled) continue;

                CollectMissing(mod, visited, missing);
            }

            return missing;
        }

        /// <summary>
        /// Enables every installed dependency of <paramref name="mod"/>, recursively.
        /// Already enabled dependencies are still traversed, because their own
        /// dependencies may be disabled or missing.
        /// </summary>
        public void EnableRequiredDependencies(ModItem mod, HashSet<ModItem> visited)
        {
            if (!visited.Add(mod)) return;

            foreach (var dependencyId in mod.Dependencies)
            {
                if (IsBuiltInDependency(dependencyId)) continue;

                var provider = FindProvider(dependencyId);
                if (provider == null || ReferenceEquals(provider, mod)) continue;

                EnableRequiredDependencies(provider, visited);

                if (!provider.IsEnabled)
                    provider.IsEnabled = true;
            }
        }

        private static bool IsFabricApiMod(ModItem mod)
        {
            if (Normalize(mod.ModId) == "fabric-api")
                return true;

            foreach (var provide in mod.Provides)
            {
                var normalized = Normalize(provide);
                if (normalized == "fabric-api" || normalized == "fabric")
                    return true;
            }

            return false;
        }

        private void CollectMissing(
            ModItem mod,
            HashSet<ModItem> visited,
            HashSet<string> missing)
        {
            if (!visited.Add(mod)) return;

            foreach (var dependencyId in mod.Dependencies)
            {
                if (IsBuiltInDependency(dependencyId)) continue;

                var provider = FindProvider(dependencyId);
                if (provider != null)
                {
                    CollectMissing(provider, visited, missing);
                    continue;
                }

                var missingId = GetMissingDependencyId(dependencyId);
                if (missingId.Length > 0)
                    missing.Add(missingId);
            }
        }

        private static IEnumerable<string> GetSuffixVariants(string normalized)
        {
            var match = VersionSuffixRegex.Match(normalized);
            if (match.Success)
            {
                var baseId = match.Groups["base"].Value.TrimEnd('-');
                if (baseId.Length > 0 && !string.Equals(baseId, normalized, StringComparison.OrdinalIgnoreCase))
                    yield return baseId;
            }

            if (normalized.EndsWith("-api", StringComparison.OrdinalIgnoreCase) &&
                normalized.Length > 4)
            {
                yield return normalized[..^4];
            }
        }
    }
}
