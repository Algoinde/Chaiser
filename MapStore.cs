using System.Diagnostics;
using System.Security.Cryptography;
using MessagePack;
using Newtonsoft.Json;

namespace Chaiser
{
    /// <summary>
    /// Maps are a cache of the client, not an input: keyed by a fingerprint of files that change
    /// with every update and hotfix (the profile's FingerprintFiles; for Endfield its resource
    /// indexes), built on first use, reused afterwards.
    ///
    ///   maps/&lt;fingerprint&gt;.assetmap  - AnimeStudio AssetMap (MessagePack), same contract as before
    ///   maps/&lt;fingerprint&gt;.cabmap    - AnimeStudio CABMap (binary), base folder = client root
    ///   maps/&lt;fingerprint&gt;.json      - when/what it was built from
    /// </summary>
    public static class MapStore
    {
        public static string Fingerprint(string clientRoot, GameProfile profile = null)
        {
            profile ??= GameProfile.Endfield;
            using var sha = SHA1.Create();
            var found = 0;
            foreach (var path in profile.FingerprintFiles(clientRoot).Where(File.Exists))
            {
                var name = System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(clientRoot, path));
                sha.TransformBlock(name, 0, name.Length, null, 0);
                var length = new FileInfo(path).Length;
                var data = length > 4 << 20 ? BitConverter.GetBytes(length) : File.ReadAllBytes(path);
                sha.TransformBlock(data, 0, data.Length, null, 0);
                found++;
            }
            if (found == 0) throw new FileNotFoundException($"No fingerprint files under {clientRoot} - not a {profile.Name} client?");
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!)[..12].ToLowerInvariant();
        }

        /// <summary>The project's maps/ folder (next to the .csproj), else ./maps.</summary>
        public static string DefaultMapsDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (dir.EnumerateFiles("*.csproj").Any()) return Path.Combine(dir.FullName, "maps");
            }
            return Path.GetFullPath("maps");
        }

        public static (string AssetMap, string CabMap) Ensure(string clientRoot, string mapsDir = null, bool rebuild = false, GameProfile profile = null)
        {
            profile ??= GameProfile.Endfield;
            mapsDir ??= DefaultMapsDir();
            var fingerprint = Fingerprint(clientRoot, profile);
            var assetMap = Path.Combine(mapsDir, $"{fingerprint}.assetmap");
            var cabMap = Path.Combine(mapsDir, $"{fingerprint}.cabmap");
            if (!rebuild && File.Exists(assetMap) && File.Exists(cabMap))
            {
                return (assetMap, cabMap);
            }

            Directory.CreateDirectory(mapsDir);
            Console.Error.WriteLine($"Building maps for client {fingerprint} ({clientRoot})...");
            var watch = Stopwatch.StartNew();
            var stats = MapBuilder.Build(profile, clientRoot, assetMap, cabMap);
            File.WriteAllText(Path.Combine(mapsDir, $"{fingerprint}.json"), JsonConvert.SerializeObject(new
            {
                fingerprint,
                game = profile.Name,
                client = clientRoot,
                builtAt = DateTime.Now.ToString("s"),
                seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
                stats.Files,
                stats.Containers,
                stats.Cabs,
                stats.Assets,
                stats.CabCollisions,
                stats.Errors,
                indexes = profile.FingerprintFiles(clientRoot).Where(File.Exists)
                    .ToDictionary(p => Path.GetRelativePath(clientRoot, p), p => File.GetLastWriteTime(p).ToString("s")),
            }, Formatting.Indented));
            Console.Error.WriteLine($"Maps built in {watch.Elapsed.TotalSeconds:0.0}s: {stats.Assets} assets, {stats.Cabs} CABs " +
                                    $"from {stats.Files} files ({stats.Errors} errors) -> {mapsDir}");
            return (assetMap, cabMap);
        }
    }

    public record MapStats(int Files, int Containers, int Cabs, int Assets, int CabCollisions, int Errors);

    /// <summary>What one container contributes to the maps.</summary>
    public sealed record ContainerScan(List<AssetEntry> Assets, List<(string Cab, CabMapEntry Entry)> Cabs);

    /// <summary>
    /// Native AssetMap + CABMap builder: the chain [SplitContainers(format) -> ScanContainer] over
    /// every archive file, files and containers in parallel. Compared with AnimeStudio's builder it
    /// skips what the maps never use - no per-object content hashing, no full object
    /// instantiation, no type-tree walk for names.
    /// </summary>
    public static class MapBuilder
    {
        static Chain MapChain(GameProfile profile) =>
            new(new SplitContainers(profile.Format) { FanOut = true }, new ScanContainer());

        static List<RawFile> RawFiles(IEnumerable<string> files, string clientRoot) =>
            files.Select(f => new RawFile(f, Path.GetRelativePath(clientRoot, f))).ToList();

        /// <summary>Scan one archive file with stack traces (debugging a map build).</summary>
        public static void Probe(string file, string clientRoot, GameProfile profile = null)
        {
            var session = new Session(profile) { Verbose = true };
            var chain = MapChain(session.Profile);
            var scans = chain.Run<RawFile, ContainerScan>(session, RawFiles([Path.GetFullPath(file)], clientRoot));
            var assets = scans.SelectMany(s => s.Assets).ToList();
            Console.WriteLine($"{chain.Produced<Container>(session)} containers, {scans.Sum(s => s.Cabs.Count)} CABs, {assets.Count} assets, {session.Failures.Count} errors");
            foreach (var g in assets.GroupBy(a => a.Type).OrderByDescending(g => g.Count()).Take(8))
                Console.WriteLine($"  {g.Key}: {g.Count()}  e.g. {g.First().Name} | {g.First().Container}");
        }

        public static MapStats Build(GameProfile profile, string clientRoot, string assetMapPath, string cabMapPath)
        {
            var files = RawFiles(profile.ArchiveFiles(clientRoot), clientRoot);
            var session = new Session(profile);
            var chain = MapChain(profile);
            var slowest = new SortedSet<(TimeSpan, string)>();
            var done = 0;
            var wall = Stopwatch.StartNew();
            // Largest first: a few huge files would otherwise run alone at the end on one core.
            var scans = chain.Run<RawFile, ContainerScan>(session, files, f => new FileInfo(f.Path).Length, (file, took) =>
            {
                lock (slowest) { slowest.Add((took, file.Path)); if (slowest.Count > 5) slowest.Remove(slowest.Min); }
                var n = Interlocked.Increment(ref done);
                if (n % 100 == 0 || n == files.Count) Console.Error.WriteLine($"  [{n}/{files.Count}]");
            });

            var loose = profile.LooseFiles(clientRoot).Select(ScanLooseFile).Where(r => r != null).ToList();

            var assets = new List<AssetEntry>(scans.Sum(r => r.Assets.Count) + loose.Sum(r => r.Count));
            foreach (var r in loose) assets.AddRange(r);
            var cabs = new Dictionary<string, CabMapEntry>(StringComparer.OrdinalIgnoreCase);
            var collisions = 0;
            foreach (var scan in scans)
            {
                assets.AddRange(scan.Assets);
                foreach (var (cab, entry) in scan.Cabs)
                {
                    if (!cabs.TryAdd(cab, entry)) collisions++;
                }
            }

            var cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
            Console.Error.WriteLine($"  scan wall {wall.Elapsed.TotalSeconds:0.0}s, cpu {cpu:0}s; inflate {(double)BlockArchive.TicksInflate / Stopwatch.Frequency:0.0}s " +
                                    $"for {BlockArchive.BlocksInflated} blocks / {BlockArchive.BytesInflated / 1048576} MB");
            foreach (var (t, f) in slowest.Reverse()) Console.Error.WriteLine($"    {t.TotalSeconds:0.0}s {new FileInfo(f).Length / 1048576}MB {Path.GetFileName(f)}");
            var tw = Stopwatch.StartNew();
            WriteAssetMap(assetMapPath, profile.GameType, assets);
            WriteCabMap(cabMapPath, clientRoot, cabs);
            Console.Error.WriteLine($"  writing maps {tw.Elapsed.TotalSeconds:0.0}s");
            return new MapStats(files.Count, chain.Produced<Container>(session), cabs.Count, assets.Count, collisions, session.Failures.Count);
        }

        /// <summary>A plain SerializedFile outside the archives (resources.assets, level0, ...), else null.</summary>
        static List<AssetEntry> ScanLooseFile(string file)
        {
            try
            {
                using var reader = new FileReader(file);
                if (reader.FileType != FileType.AssetsFile) return null;
                var sf = new SerializedFile(reader);
                var assets = new List<AssetEntry>();
                foreach (var info in sf.m_Objects)
                {
                    if (!Enum.IsDefined(typeof(ClassIDType), info.classID)) continue;
                    var type = (ClassIDType)info.classID;
                    var name = ScanContainer.ReadName(reader, sf, info, type);
                    if (name == null) continue;
                    assets.Add(new AssetEntry { Name = name, Container = "", Source = file, PathID = info.m_PathID, Type = type, Offset = 0 });
                }
                return assets;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  {Path.GetFileName(file)}: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        public static void DumpTypeTree(string file, ClassIDType type)
        {
            using var reader = new FileReader(file);
            var vfs = new VFSFile(reader, file);
            foreach (var node in vfs.Nodes)
            {
                using var nodeReader = new FileReader(node.path, vfs.OpenNodeLazy(node));
                if (nodeReader.FileType != FileType.AssetsFile) continue;
                var sf = new SerializedFile(nodeReader);
                var info = sf.m_Objects.FirstOrDefault(o => o.classID == (int)type);
                if (info == null) continue;
                Console.WriteLine($"{node.path} unity {sf.unityVersion} objects {sf.m_Objects.Count} {type} size {info.byteSize}");
                var tree = info.serializedType?.m_Type;
                if (tree == null) { Console.WriteLine("  (no type tree)"); return; }
                foreach (var n in tree.m_Nodes.Take(60))
                    Console.WriteLine($"  {new string(' ', n.m_Level * 2)}{n.m_Type} {n.m_Name}  size={n.m_ByteSize} flags=0x{n.m_MetaFlag:X}");
                return;
            }
        }

        static void WriteAssetMap(string path, GameType gameType, List<AssetEntry> assets)
        {
            var map = new AssetMap { GameType = gameType, AssetEntries = assets };
            var tmp = path + ".part";
            using (var stream = File.Create(tmp))
            {
                MessagePackSerializer.Serialize(stream, map,
                    MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
            }
            File.Move(tmp, path, overwrite: true);
        }

        static void WriteCabMap(string path, string baseFolder, Dictionary<string, CabMapEntry> cabs)
        {
            var tmp = path + ".part";
            using (var stream = File.Create(tmp))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(baseFolder);
                writer.Write(cabs.Count);
                foreach (var (cab, entry) in cabs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    writer.Write(cab);
                    writer.Write(entry.Path);
                    writer.Write(entry.Offset);
                    writer.Write(entry.Dependencies.Count);
                    foreach (var dep in entry.Dependencies) writer.Write(dep);
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>
    /// Map terminal: names + container paths of one archive's objects, and its CABs. Works on the
    /// whole container at once because AssetBundle preload PPtrs point into sibling CABs.
    /// </summary>
    public sealed class ScanContainer : Stage<Container, ContainerScan>
    {
        // Types whose first serialized field is m_Name (NamedObject).
        static readonly HashSet<ClassIDType> NamedTypes = new()
        {
            ClassIDType.Texture2D, ClassIDType.Sprite, ClassIDType.Mesh, ClassIDType.Material, ClassIDType.TextAsset,
            ClassIDType.AnimationClip, ClassIDType.AudioClip, ClassIDType.Font, ClassIDType.VideoClip, ClassIDType.Shader,
        };

        // Only the AssetBundle tree is read (to detect Endfield's extra m_PathFlags field).
        static readonly Func<int, bool> KeepTypeTree = classId => classId == (int)ClassIDType.AssetBundle;

        public override IEnumerable<ContainerScan> Run(Container container, Worker worker)
        {
            yield return Scan(container, worker);
        }

        static ContainerScan Scan(Container container, Worker worker)
        {
            var assets = new List<AssetEntry>();
            var cabs = new List<(string, CabMapEntry)>();
            // Objects of every CAB in this container, so AssetBundle preload PPtrs into sibling
            // CABs resolve too.
            var byCab = new Dictionary<string, Dictionary<long, AssetEntry>>(StringComparer.OrdinalIgnoreCase);
            var bundles = new List<(SerializedFile File, List<(int FileID, long PathID)> Preload, List<(string Path, int Index, int Size)> Containers)>();

            foreach (var node in container.Archive.Nodes)
            {
                if (OpenNodes.IsResource(node.path)) continue;
                // Lazy: only the blocks holding metadata and each object's first bytes get inflated.
                using var file = OpenAssetsFile.Open(new NodeItem(container, node), worker, KeepTypeTree);
                if (file == null) continue;
                var (sf, reader) = (file.File, file.Reader);
                cabs.Add((sf.fileName, new CabMapEntry(container.File.Relative, container.Offset, sf.m_Externals.Select(x => x.fileName).ToList())));

                var local = new Dictionary<long, AssetEntry>();
                byCab[sf.fileName] = local;
                foreach (var info in sf.m_Objects)
                {
                    if (!Enum.IsDefined(typeof(ClassIDType), info.classID)) continue;
                    var type = (ClassIDType)info.classID;
                    if (type == ClassIDType.AssetBundle)
                    {
                        bundles.Add(ReadAssetBundle(reader, sf, info, worker.Session.Dialect));
                        continue;
                    }
                    var name = ReadName(reader, sf, info, type);
                    if (name == null) continue;
                    var entry = new AssetEntry
                    {
                        Name = name, Container = "", Source = container.File.Path, PathID = info.m_PathID, Type = type, Offset = container.Offset,
                    };
                    local[info.m_PathID] = entry;
                    assets.Add(entry);
                }
            }

            // Same semantics as AnimeStudio: every object in a container's preload range gets
            // that container path, later containers overwriting earlier ones.
            foreach (var (sf, preload, containerList) in bundles)
            {
                foreach (var (path, index, size) in containerList)
                {
                    for (var k = index; k < index + size && k < preload.Count; k++)
                    {
                        var (fileId, pathId) = preload[k];
                        var cab = fileId == 0 ? sf.fileName
                            : fileId - 1 < sf.m_Externals.Count ? sf.m_Externals[fileId - 1].fileName : null;
                        if (cab != null && byCab.TryGetValue(cab, out var objects) && objects.TryGetValue(pathId, out var entry))
                        {
                            entry.Container = path;
                        }
                    }
                }
            }
            return new ContainerScan(assets, cabs);
        }

        public static string ReadName(FileReader reader, SerializedFile sf, ObjectInfo info, ClassIDType type)
        {
            var r = new ObjectReader(reader, sf, info);
            r.Reset();
            try
            {
                if (NamedTypes.Contains(type))
                {
                    return r.ReadAlignedString();
                }
                if (type == ClassIDType.MonoBehaviour)
                {
                    // m_GameObject PPtr, m_Enabled (+align), m_Script PPtr, m_Name
                    r.ReadInt32(); r.ReadInt64();
                    r.ReadByte(); r.AlignStream();
                    r.ReadInt32(); r.ReadInt64();
                    var name = r.ReadAlignedString();
                    return string.IsNullOrWhiteSpace(name) ? type.ToString() : name;
                }
            }
            catch (EndOfStreamException)
            {
                return type.ToString();
            }
            return null; // not in the map (AnimeStudio's "minimal" set)
        }

        static (SerializedFile, List<(int, long)>, List<(string, int, int)>) ReadAssetBundle(FileReader reader, SerializedFile sf, ObjectInfo info, Dialect dialect)
        {
            var r = new ObjectReader(reader, sf, info);
            r.Reset();
            r.ReadAlignedString(); // m_Name
            // Endfield's AssetBundle has an extra int m_PathFlags after m_Name (not in stock Unity);
            // trust the file's own type tree when it has one, else the game.
            var nodes = info.serializedType?.m_Type?.m_Nodes;
            if (nodes?.Any(n => n.m_Level == 1 && n.m_Name == "m_PathFlags") ?? dialect.Game == "ef") r.ReadInt32();
            var preloadCount = Count(r, 12);
            var preload = new List<(int, long)>(preloadCount);
            for (var i = 0; i < preloadCount; i++)
            {
                preload.Add((r.ReadInt32(), r.ReadInt64()));
            }
            var containerCount = Count(r, 28);
            var containers = new List<(string, int, int)>(containerCount);
            for (var i = 0; i < containerCount; i++)
            {
                var path = r.ReadAlignedString();
                var preloadIndex = r.ReadInt32();
                var preloadSize = r.ReadInt32();
                r.ReadInt32(); r.ReadInt64(); // asset PPtr
                containers.Add((path, preloadIndex, preloadSize));
            }
            return (sf, preload, containers);
        }

        /// <summary>An array length that must fit in what's left of the object (min bytes per item).</summary>
        static int Count(ObjectReader r, int minItemSize)
        {
            var count = r.ReadInt32();
            if (count < 0 || (long)count * minItemSize > r.BytesLeft())
                throw new InvalidDataException($"implausible array length {count} at +{r.Position - r.byteStart}");
            return count;
        }
    }
}
