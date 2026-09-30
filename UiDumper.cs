using System.Collections.Concurrent;
using MessagePack;

namespace Chaiser
{
    /// <summary>
    /// Bulk dump of UI textures by container folder (e.g. every Texture2D under
    /// ".../ui/sprites/itemicon/"), for Enka's EF icon set.
    ///
    /// Endfield UI icons are standalone textures (one Texture2D + one full-rect Sprite per icon,
    /// no atlases), so decoding the Texture2D is the whole job. Everything is resolved from the
    /// AssetMap alone: each entry names its container file + offset, and a streamed texture's
    /// .resS is a sibling node in that same container - so no CABMap is needed here, unlike the
    /// building pipeline which follows cross-container dependencies.
    ///
    /// Entries are grouped by container so each is opened once, and containers run in parallel.
    /// </summary>
    public static class UiDumper
    {
        public static AssetMap LoadMap(string mapPath)
        {
            using var stream = File.OpenRead(mapPath);
            return MessagePackSerializer.Deserialize<AssetMap>(stream,
                MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
        }

        static string Dir(AssetEntry e) =>
            e.Container == null ? null : Path.GetDirectoryName(e.Container)?.Replace('\\', '/');

        /// <summary>The container's last directory, e.g. "itemicon" for ".../sprites/itemicon/x.png".</summary>
        public static string Folder(AssetEntry e)
        {
            var dir = Dir(e);
            return dir == null ? null : dir[(dir.LastIndexOf('/') + 1)..];
        }

        /// <summary>
        /// A folder spec matches a container directory by suffix, so "itemicon" takes any
        /// ".../itemicon" while "gameplay/ui/sprites/charinfo" pins one of several "charinfo" dirs.
        /// A trailing "/*" takes the direct subdirectories instead: "skillicons/avatar/*" is every
        /// ".../skillicons/avatar/&lt;charid&gt;".
        /// </summary>
        static bool Matches(AssetEntry e, IEnumerable<string> specs)
        {
            var dir = Dir(e);
            return dir != null && specs.Any(spec => spec.EndsWith("/*")
                ? dir.LastIndexOf('/') is var slash and > 0 && dir[..slash].EndsWith("/" + spec[..^2].Trim('/'), StringComparison.OrdinalIgnoreCase)
                : dir.EndsWith("/" + spec.Trim('/'), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Texture2D counts per container directory whose path contains <paramref name="pathContains"/>.</summary>
        public static void List(AssetMap map, string pathContains)
        {
            var groups = map.AssetEntries
                .Where(e => e.Type == ClassIDType.Texture2D && e.Container != null
                         && e.Container.Contains(pathContains, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => Path.GetDirectoryName(e.Container)!.Replace('\\', '/'))
                .OrderBy(g => g.Key);
            foreach (var g in groups)
            {
                Console.WriteLine($"{g.Select(e => e.Name).Distinct().Count(),6}  {g.Key}");
            }
        }

        /// <summary>
        /// Dumps every Texture2D whose container directory ends with one of <paramref name="folders"/>
        /// to outDir/&lt;last folder segment&gt;/&lt;name&gt;.png. Returns the number of failures.
        /// </summary>
        public static int Dump(AssetMap map, string outDir, ICollection<string> folders, string pathContains, bool skipExisting, bool mirror = false, GameProfile profile = null)
        {
            Mirror = mirror;
            var entries = map.AssetEntries
                .Where(e => e.Type == ClassIDType.Texture2D && e.Container != null
                         && e.Container.Contains(pathContains, StringComparison.OrdinalIgnoreCase)
                         && Matches(e, folders))
                // Hotfixes land in Endfield_Data/Persistent/VFS and shadow StreamingAssets copies
                // in-game, so when a map covers both, keep the Persistent one.
                .OrderBy(e => e.Source.Contains(@"\Persistent\", StringComparison.OrdinalIgnoreCase)
                           || e.Source.Contains("/Persistent/", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                // One texture per output file (the path is the real collision: --mirror keeps
                // same-named dirs such as avatarroundicon/avatar and avataricon/avatar apart).
                .DistinctBy(e => OutPath(outDir, e).ToLowerInvariant())
                .Where(e => !(skipExisting && File.Exists(OutPath(outDir, e))))
                .ToList();
            Console.Write($"{entries.Count} textures -> {outDir}: ");
            return WriteAll(entries, e => OutPath(outDir, e), profile);
        }

        /// <summary>
        /// Dumps exactly the textures an assets.json lists (AIC: scripts/collect-assets.ts):
        /// { folder: { source: [container dirs], names: [...] } } -> outRoot/&lt;folder&gt;/&lt;name&gt;.png.
        /// Sources are tried in order; a name resolved only from a later one is reported, a name in
        /// none is an error, and so are two same-named textures in one dir. Returns the error count.
        /// </summary>
        public static int DumpAssets(AssetMap map, string assetsJson, string outRoot, GameProfile profile = null)
        {
            var spec = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(assetsJson));
            // (dir, name) -> texture; the Persistent (hotfix) copy shadows StreamingAssets like in-game.
            var byDirName = new Dictionary<(string, string), AssetEntry>();
            var duplicates = new List<string>();
            foreach (var e in map.AssetEntries.Where(e => e.Type == ClassIDType.Texture2D && e.Container != null)
                         .OrderBy(e => IsPersistent(e) ? 0 : 1))
            {
                var dir = Dir(e);
                if (dir == null || !dir.StartsWith("assets/beyond/", StringComparison.OrdinalIgnoreCase)) continue;
                var key = (dir["assets/beyond/".Length..].ToLowerInvariant(), e.Name.ToLowerInvariant());
                if (!byDirName.TryAdd(key, e) && IsPersistent(byDirName[key]) == IsPersistent(e))
                    duplicates.Add($"{key.Item1}/{e.Name}");
            }

            var errors = 0;
            var targets = new Dictionary<AssetEntry, string>();
            foreach (var (folder, def) in spec)
            {
                var sources = def["source"]?.ToObject<string[]>() ?? Array.Empty<string>();
                var custom = (def["custom"]?.ToObject<string[]>() ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var names = (def["names"]?.ToObject<string[]>() ?? Array.Empty<string>()).Where(n => !custom.Contains(n)).ToArray(); // hand-made: never dumped
                if (sources.Length == 0 || names.Length == 0) continue; // rendered / manual folders
                var fallbacks = new List<string>();
                var missing = new List<string>();
                foreach (var name in names)
                {
                    var hit = sources.Select((src, i) => (i, byDirName.GetValueOrDefault((src.ToLowerInvariant(), name.ToLowerInvariant()))))
                                     .FirstOrDefault(x => x.Item2 != null);
                    if (hit.Item2 == null) { missing.Add(name); continue; }
                    if (hit.i > 0) fallbacks.Add($"{name} (from {sources[hit.i]})");
                    targets[hit.Item2] = Path.Combine(outRoot, folder, $"{name}.png");
                }
                var dups = duplicates.Where(d => sources.Any(src => d.StartsWith(src + "/", StringComparison.OrdinalIgnoreCase))
                                                 && names.Contains(d[(d.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)).ToList();
                Console.WriteLine($"{folder}: {names.Length - missing.Count}/{names.Length} found");
                foreach (var f in fallbacks) Console.WriteLine($"  FALLBACK {folder}/{f}");
                foreach (var m in missing) Console.WriteLine($"  MISSING  {folder}/{m} (in none of: {string.Join(", ", sources)})");
                foreach (var d in dups) Console.WriteLine($"  DUPLICATE {d} (several textures with this name in one dir; took the first)");
                errors += missing.Count + dups.Count;
            }

            Console.Write($"{targets.Count} textures -> {outRoot}: ");
            return errors + WriteAll(targets.Keys.ToList(), e => targets[e], profile);
        }

        static bool IsPersistent(AssetEntry e) =>
            e.Source.Contains(@"\Persistent\", StringComparison.OrdinalIgnoreCase) || e.Source.Contains("/Persistent/", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Decodes and writes entries: the chain from CONTAINER (grouped by container, containers in
        /// parallel) down to PNG. Returns failures.
        /// </summary>
        static int WriteAll(List<AssetEntry> entries, Func<AssetEntry, string> outPath, GameProfile profile = null)
        {
            profile ??= GameProfile.Endfield;
            var containers = entries.GroupBy(e => (e.Source, e.Offset))
                .Select(g => new ContainerRef(new RawFile(g.Key.Source, Path.GetFileName(g.Key.Source)), g.Key.Offset,
                                              new ConcurrentDictionary<long, AssetEntry>(g.ToDictionary(e => e.PathID))))
                .ToList();
            Console.WriteLine($"{containers.Count} containers");
            foreach (var dir in entries.Select(e => Path.GetDirectoryName(outPath(e))).Distinct())
            {
                Directory.CreateDirectory(dir);
            }

            var chain = new Chain(new OpenContainer(profile.Format), new OpenNodes(), new OpenAssetsFile(), new SelectObjects(), new DecodeTexture(), new WritePng(outPath));
            var session = new Session(profile);
            var results = chain.Run<ContainerRef, Written>(session, containers);

            // Failures of an object name its entry; anything else not written failed above it
            // (its container) or was never found.
            var failed = session.Failures.Where(f => f.Item is ObjectItem).ToDictionary(f => ((ObjectItem)f.Item).Entry, f => f.Message, ReferenceEqualityComparer.Instance);
            var written = results.Select(r => r.Entry).ToHashSet(ReferenceEqualityComparer.Instance);
            var why = session.Failures.Count > failed.Count ? "not reached (errors above)" : "PathID not found in its container";
            var failures = entries.Where(e => !written.Contains(e))
                .Select(e => $"{Folder(e)}/{e.Name}: {(failed.TryGetValue(e, out var message) ? message : why)}").ToList();

            foreach (var failure in failures.OrderBy(f => f)) Console.WriteLine($"  FAILED {failure}");
            Console.WriteLine($"Wrote {results.Count}, failed {failures.Count}.");
            return failures.Count;
        }

        // --mirror: outDir/<full container dir>/<name>.png (the layout AnimeStudio's
        // ByContainer export uses), else outDir/<last folder>/<name>.png
        static bool Mirror;

        static string OutPath(string outDir, AssetEntry e)
        {
            var safeName = string.Join("_", e.Name.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(outDir, Mirror ? Dir(e) : Folder(e), $"{safeName}.png");
        }
    }
}
