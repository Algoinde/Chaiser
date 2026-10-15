using Chaiser;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;
using System.Collections.Specialized;

// Global options, anywhere on the command line:
//   --game <ef|hsr>           which game's client (default: ef); see GameProfile
//   --client <data folder>    client to operate on (default: the profile's DefaultClient)
//   --maps <dir>              map cache (default: maps/ next to the .csproj)
//   --rebuild-maps            rebuild this client's maps even if cached
// Maps are keyed by a fingerprint of the client's version/index files and built on first use.
var argList = args.ToList();
string TakeOption(string name)
{
    var i = argList.IndexOf(name);
    if (i < 0 || i + 1 >= argList.Count) return null;
    var value = argList[i + 1];
    argList.RemoveRange(i, 2);
    return value;
}
var profile = GameProfile.Get(TakeOption("--game") ?? "ef");
var clientRoot = TakeOption("--client") ?? profile.DefaultClient;
var mapsDir = TakeOption("--maps");
var rebuildMaps = argList.Remove("--rebuild-maps");
args = argList.ToArray();
var maps = new Lazy<(string AssetMap, string CabMap)>(() => MapStore.Ensure(clientRoot, mapsDir, rebuildMaps, profile));

if (args.Length > 0 && args[0] == "maps")
{
    // maps: print (building if needed) the maps for this client
    Console.WriteLine($"client       {clientRoot}");
    Console.WriteLine($"game         {profile.Name}");
    Console.WriteLine($"fingerprint  {MapStore.Fingerprint(clientRoot, profile)}");
    Console.WriteLine($"assetmap     {maps.Value.AssetMap}");
    Console.WriteLine($"cabmap       {maps.Value.CabMap}");
    return;
}

if (args.Length > 0 && args[0] == "crypto-selftest")
{
    var aesFailures = Aes128.SelfTest();
    Console.WriteLine(aesFailures == 0 ? "AES-128 core: matches .NET AES, schedules round-trip" : $"AES-128 core: {aesFailures} FAILURES");
    var mismatches = VFSUtils.SelfTest(50);
    Console.WriteLine(mismatches == 0 ? "VFS decrypt: new == reference" : $"VFS decrypt: {mismatches} MISMATCHES");
    mismatches += aesFailures;
    Environment.Exit(mismatches == 0 ? 0 : 1);
}

if (args.Length > 0 && args[0] == "maps-compare")
{
    // maps-compare <other.assetmap>: diff this client's map against another (e.g. AnimeStudio's)
    var mine = UiDumper.LoadMap(maps.Value.AssetMap).AssetEntries;
    var other = UiDumper.LoadMap(args[1]).AssetEntries;
    foreach (var type in new[] { ClassIDType.Texture2D, ClassIDType.Sprite, ClassIDType.MonoBehaviour, ClassIDType.Material, ClassIDType.Mesh, ClassIDType.GameObject })
    {
        var a = mine.Where(e => e.Type == type).GroupBy(e => (Path.GetFileName(e.Source), e.Offset, e.PathID)).ToDictionary(g => g.Key, g => g.First());
        var b = other.Where(e => e.Type == type).GroupBy(e => (Path.GetFileName(e.Source), e.Offset, e.PathID)).ToDictionary(g => g.Key, g => g.First());
        var both = a.Keys.Where(b.ContainsKey).ToList();
        var nameDiff = both.Count(k => a[k].Name != b[k].Name);
        var contDiff = both.Where(k => (a[k].Container ?? "") != (b[k].Container ?? "")).ToList();
        Console.WriteLine($"{type,-14} mine {a.Count,8} other {b.Count,8} common {both.Count,8} onlyMine {a.Count - both.Count,7} onlyOther {b.Count - both.Count,7} nameDiff {nameDiff,5} containerDiff {contDiff.Count,6}");
        foreach (var k in contDiff.Take(2)) Console.WriteLine($"    {a[k].Name}: '{a[k].Container}' vs '{b[k].Container}'");
        foreach (var k in b.Keys.Where(k => !a.ContainsKey(k)).Take(2)) Console.WriteLine($"    only other: {b[k].Name} {b[k].Source}@{b[k].Offset}");
    }
    return;
}

if (args.Length > 0 && args[0] == "maps-probe")
{
    // maps-probe <file.chk>: scan one file the way the map builder does, with stack traces
    if (args.Length > 2 && args[2] == "--tree") { MapBuilder.DumpTypeTree(args[1], ClassIDType.AssetBundle); return; }
    MapBuilder.Probe(args[1], clientRoot, profile);
    return;
}

if (args.Length > 0 && args[0] == "locate-textures")
{
    // locate-textures <names.txt>: for each texture name (one per line), the container dirs that
    // hold a Texture2D of that name - for authoring asset kind -> source dir mappings.
    var wanted = File.ReadAllLines(args[1]).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var map = UiDumper.LoadMap(maps.Value.AssetMap);
    var found = map.AssetEntries
        .Where(e => e.Type == ClassIDType.Texture2D && e.Name != null && wanted.Contains(e.Name))
        .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(e => Path.GetDirectoryName(e.Container ?? "")?.Replace(Path.DirectorySeparatorChar, '/') ?? "").Distinct().OrderBy(d => d).ToList(), StringComparer.OrdinalIgnoreCase);
    foreach (var name in wanted.OrderBy(n => n))
        Console.WriteLine($"{name}	{(found.TryGetValue(name, out var dirs) ? string.Join(" | ", dirs) : "-")}");
    return;
}

if (args.Length > 0 && args[0] == "find-in-assetmap")
{
    var name = args[1];

    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    Console.WriteLine($"Loaded {assetMap.AssetEntries.Count} entries for GameType {assetMap.GameType}");

    var matches = assetMap.AssetEntries.Where(e => e.Name == name).ToList();
    Console.WriteLine($"Found {matches.Count} entries named '{name}'");
    foreach (var m in matches)
    {
        Console.WriteLine($"  Name={m.Name} Type={m.Type} PathID={m.PathID} Source={m.Source} Offset=0x{m.Offset:X} Container={m.Container}");
    }
    return;
}

if (args.Length > 0 && args[0] == "dump-assets")
{
    // dump-assets <assets.json> <outRoot>: exactly the textures a data-driven asset list names
    // (AIC's src/lib/data/assets.json) -> outRoot/<folder>/<name>.png
    var errors = UiDumper.DumpAssets(UiDumper.LoadMap(maps.Value.AssetMap), args[1], args[2], profile);
    Environment.Exit(errors == 0 ? 0 : 1);
}

if (args.Length > 0 && args[0] == "dump-ui")
{
    // dump-ui --list [pathContains]
    // dump-ui <outDir> <folder,folder,...> [--skip-existing] [--mirror] [--path <pathContains>]
    //   --mirror writes outDir/<container dir>/<name>.png, e.g. over an AnimeStudio ByContainer dump
    //   folders match container dirs by suffix: "itemicon", or "gameplay/ui/sprites/charinfo" to pin one
    var pathOpt = Array.IndexOf(args, "--path");
    var pathContains = pathOpt > 0 ? args[pathOpt + 1] : "/ui/";
    var map = UiDumper.LoadMap(maps.Value.AssetMap);
    Console.WriteLine($"Loaded {map.AssetEntries.Count} entries from {maps.Value.AssetMap}");
    if (args[1] == "--list")
    {
        UiDumper.List(map, args.Length > 2 && !args[2].StartsWith("--") ? args[2] : pathContains);
        return;
    }
    var folders = new HashSet<string>(args[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                                      StringComparer.OrdinalIgnoreCase);
    var failed = UiDumper.Dump(map, args[1], folders, pathContains, args.Contains("--skip-existing"), args.Contains("--mirror"), profile);
    Environment.Exit(failed == 0 ? 0 : 1);
}

if (args.Length > 0 && args[0] == "list-buildings")
{
    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    Console.WriteLine($"Loaded {assetMap.AssetEntries.Count} entries for GameType {assetMap.GameType}");

    var buildingEntries = assetMap.AssetEntries
        .Where(e => e.Type == ClassIDType.MonoBehaviour
                 && e.Container != null
                 && e.Container.Contains("ciautogenerated/pc/building/", StringComparison.OrdinalIgnoreCase))
        .ToList();

    Console.WriteLine($"Found {buildingEntries.Count} building MonoBehaviour entries");
    foreach (var e in buildingEntries)
    {
        Console.WriteLine($"  {e.Name}  PathID={e.PathID}  Source={Path.GetFileName(e.Source)}  Offset=0x{e.Offset:X}");
    }
    return;
}

var DefaultModelsDir = Path.Combine(Path.GetDirectoryName(MapStore.DefaultMapsDir())!, "output", "models");
const string DefaultFactoryBuildingTablePath = @"C:\Projects\34_AIC\src\lib\data\FactoryBuildingTable.json";
var DefaultDataDir = Path.GetDirectoryName(DefaultFactoryBuildingTablePath);

// FactoryBuildingTable.json alone is NOT the full set of extractable building ids - the
// planner's own build-buildings.ts merge step (34_AIC/scripts/build-buildings.ts) folds in six
// more tables with a different shape ("screwed-up" router/valve/connector tables, per that
// script's own comment): grid/liquid routers, box/fluid valves, grid/liquid connectors. These
// hold real entries like log_connector, log_pipe_splitter, etc. that are NOT in
// FactoryBuildingTable.json at all - looking them up there was a false "not found" that made the
// extractor treat them as pedestal-less props when several actually do have body meshes (via a
// suffixed variant name) or (rarely) a pedestal. Confirmed each of these tables' entries still
// carry `defaultRendererTemplate`/`rendererTemplateMap`/`id`/`range` at the SAME top level as
// FactoryBuildingTable.json (only cosmetic fields like `name` sit inside a nested
// gridUnitData/liquidUnitData wrapper the build script unwraps) - so a shallow top-level merge
// is enough, no need to replicate that unwrapping here.
JObject LoadMergedBuildingTable()
{
    var merged = JObject.Parse(File.ReadAllText(DefaultFactoryBuildingTablePath));
    var extraTables = new[]
    {
        "FactoryGridRouterTable.json",
        "FactoryBoxValveTable.json",
        "FactoryGridConnecterTable.json",
        "FactoryLiquidRouterTable.json",
        "FactoryFluidValveTable.json",
        "FactoryLiquidConnectorTable.json",
    };
    foreach (var tableFile in extraTables)
    {
        var path = Path.Combine(DefaultDataDir, tableFile);
        if (!File.Exists(path)) continue;
        var table = JObject.Parse(File.ReadAllText(path));
        foreach (var prop in table.Properties())
        {
            merged[prop.Name] = prop.Value;
        }
    }
    return merged;
}

// Confirmed NIY (not implemented yet) by the user - a real building id/table entry that has no
// corresponding renderable asset in the game data at all, not a lookup bug. Extraction should
// not report these as generic failures.
var KnownNiyBuildingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "xiranite_oven_2",
};

if (args.Length > 0 && args[0] == "search-names")
{
    var needle = args[1];
    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var matches = assetMap.AssetEntries
        .Where(e => (e.Name != null && e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                 || (e.Container != null && e.Container.Contains(needle, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    Console.WriteLine($"{matches.Count} entries containing '{needle}' (name or container):");
    foreach (var m in matches.Take(100))
    {
        Console.WriteLine($"  {m.Name}  Type={m.Type}  Container={m.Container}");
    }
    return;
}

if (args.Length > 0 && args[0] == "search-containers")
{
    // search-containers <needle>: every distinct container whose path contains the needle,
    // with its asset type counts - for finding prefabs/models no table names directly.
    var needle = args[1];
    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var groups = assetMap.AssetEntries
        .Where(e => e.Container != null && e.Container.Contains(needle, StringComparison.OrdinalIgnoreCase))
        .GroupBy(e => e.Container)
        .OrderBy(g => g.Key);
    foreach (var g in groups)
    {
        var types = string.Join(", ", g.GroupBy(e => e.Type).Select(t => $"{t.Key}x{t.Count()}"));
        Console.WriteLine($"{g.Key}  [{types}]");
    }
    return;
}

if (args.Length > 0 && args[0] == "extract-prefab")
{
    // extract-prefab <containerContains> <out.glb>: a plain prefab's lod0 mesh (see
    // BuildingExtractor.ResolvePrefabPart), e.g. soilmodel/soil_4_0.
    var extractorPrefab = new BuildingExtractor(maps.Value.AssetMap, maps.Value.CabMap, clientRoot);
    var part = extractorPrefab.ResolvePrefabPart(args[1]);
    if (part == null) { Console.WriteLine("no mesh resolved"); Environment.Exit(1); }
    Console.WriteLine($"{part.Name}: {part.VertexCount} verts");
    GltfExporter.ExportGlb(args[2], new List<GltfPart> { part });
    return;
}

if (args.Length > 0 && args[0] == "dump-mat")
{
    // dump-mat <containerContains>: a Material asset's property sheet by container path, for
    // materials no building config names (dump-body-material starts from a building).
    var needle = args[1];
    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var entry = assetMap.AssetEntries.FirstOrDefault(e => e.Type == ClassIDType.Material
        && e.Container != null && e.Container.Contains(needle, StringComparison.OrdinalIgnoreCase));
    if (entry == null) { Console.WriteLine("not found"); return; }

    using var seedReader = new FileReader(entry.Source);
    seedReader.Position = entry.Offset;
    var seedVfs = new VFSFile(seedReader, entry.Source);
    foreach (var node in seedVfs.Nodes)
    {
        var extracted = seedVfs.ExtractNode(node);
        using var reader = new FileReader(node.path, extracted, leaveOpen: true);
        if (reader.FileType != FileType.AssetsFile) continue;
        var sf = new SerializedFile(reader);
        if (!sf.ObjectsByPathId.TryGetValue(entry.PathID, out var info)) continue;
        reader.Position = 0;
        var mat = new Material(new ObjectReader(reader, sf, info));
        Console.WriteLine($"{entry.Container}: {mat.Name}");
        Console.WriteLine("m_TexEnvs:");
        foreach (var t in mat.m_SavedProperties.m_TexEnvs.Where(t => !t.Value.m_Texture.IsNull))
            Console.WriteLine($"  {t.Key}: external={t.Value.m_Texture.ExternalFileName} pathID={t.Value.m_Texture.m_PathID}");
        Console.WriteLine("m_Floats (nonzero):");
        foreach (var f in mat.m_SavedProperties.m_Floats.Where(f => f.Value != 0))
            Console.WriteLine($"  {f.Key}: {f.Value}");
        Console.WriteLine("m_Colors:");
        foreach (var c in mat.m_SavedProperties.m_Colors)
            Console.WriteLine($"  {c.Key}: r={c.Value.R} g={c.Value.G} b={c.Value.B} a={c.Value.A}");
        // --tex <property> <out.png>: also decode that texture slot.
        var texOpt = Array.IndexOf(args, "--tex");
        if (texOpt > 0)
        {
            var png = new BuildingExtractor(maps.Value.AssetMap, maps.Value.CabMap, clientRoot).ResolveTexturePng(mat, args[texOpt + 1]);
            if (png == null) Console.WriteLine($"{args[texOpt + 1]}: nothing decoded");
            else { File.WriteAllBytes(args[texOpt + 2], png); Console.WriteLine($"Wrote {args[texOpt + 2]}"); }
        }
        return;
    }
    Console.WriteLine("no serialized file holds it");
    return;
}

if (args.Length > 0 && args[0] == "dump-cab")
{
    // dump-cab <containerContains>: every object in the serialized file behind the first
    // matching container, with type trees for the scene-graph pieces (GameObject, Transform,
    // MeshFilter, MeshRenderer) - for prefabs whose meshes no MonoBehaviour names.
    var needle = args[1];
    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var entry = assetMap.AssetEntries.FirstOrDefault(e => e.Container != null && e.Container.Contains(needle, StringComparison.OrdinalIgnoreCase));
    if (entry == null) { Console.WriteLine("not found"); return; }

    using var seedReader = new FileReader(entry.Source);
    seedReader.Position = entry.Offset;
    var seedVfs = new VFSFile(seedReader, entry.Source);
    foreach (var node in seedVfs.Nodes)
    {
        var extracted = seedVfs.ExtractNode(node);
        using var reader = new FileReader(node.path, extracted, leaveOpen: true);
        if (reader.FileType != FileType.AssetsFile) continue;
        var sf = new SerializedFile(reader);
        if (!sf.ObjectsByPathId.ContainsKey(entry.PathID)) continue;

        Console.WriteLine($"// {entry.Container} in {node.path}");
        for (var i = 0; i < sf.m_Externals.Count; i++)
            Console.WriteLine($"//   m_FileID {i + 1} = {sf.m_Externals[i].fileName}");
        var wanted = new HashSet<ClassIDType> { ClassIDType.GameObject, ClassIDType.Transform, ClassIDType.MeshFilter, ClassIDType.MeshRenderer };
        foreach (var info in sf.ObjectsByPathId.Values)
        {
            var type = (ClassIDType)info.classID;
            Console.WriteLine($"== {type} PathID={info.m_PathID}");
            if (!wanted.Contains(type) || info.serializedType?.m_Type == null) continue;
            reader.Position = 0;
            var tree = TypeTreeHelper.ReadType(info.serializedType.m_Type, new ObjectReader(reader, sf, info));
            Console.WriteLine(JsonConvert.SerializeObject(tree, Formatting.Indented));
        }
        return;
    }
    Console.WriteLine("no serialized file holds it");
    return;
}

if (args.Length > 0 && args[0] == "search-and-dump-textures")
{
    // Scoped keyword search (container path must contain pathContains, name or container must
    // contain one of the needles) across Texture2D entries only, dumping every match's decoded
    // PNG into one folder for visual skimming - narrower and more useful than search-names'
    // plain-text listing when hunting for a specific icon/marker texture by naming guesswork.
    var pathContains = args[1];
    var needles = args[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var outDir = args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "texture_search");
    Directory.CreateDirectory(outDir);

    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var matches = assetMap.AssetEntries
        .Where(e => e.Type == ClassIDType.Texture2D
                 && e.Container != null
                 && e.Container.Contains(pathContains, StringComparison.OrdinalIgnoreCase)
                 && needles.Any(n => (e.Name != null && e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
                                   || e.Container.Contains(n, StringComparison.OrdinalIgnoreCase)))
        .DistinctBy(e => e.Name)
        .ToList();

    Console.WriteLine($"{matches.Count} matching Texture2D entries. Dumping to {outDir}...");

    var written = 0;
    foreach (var entry in matches)
    {
        try
        {
            using var seedReader = new FileReader(entry.Source);
            seedReader.Position = entry.Offset;
            var seedVfs = new VFSFile(seedReader, entry.Source);

            SerializedFile sf = null;
            FileReader cabReader = null;
            foreach (var node in seedVfs.Nodes)
            {
                var extracted = seedVfs.ExtractNode(node);
                var candidateReader = new FileReader(node.path, extracted, leaveOpen: true);
                if (candidateReader.FileType == FileType.AssetsFile)
                {
                    var candidateSf = new SerializedFile(candidateReader);
                    if (candidateSf.ObjectsByPathId.ContainsKey(entry.PathID))
                    {
                        sf = candidateSf;
                        cabReader = candidateReader;
                        break;
                    }
                }
                candidateReader.Dispose();
            }
            if (sf == null) continue;
            using var seedCabReader = cabReader;

            var targetInfo = sf.ObjectsByPathId[entry.PathID];
            cabReader.Position = 0;
            var texture = new Texture2D(new ObjectReader(cabReader, sf, targetInfo));

            var resolverSearch = new AssetResolver(maps.Value.CabMap, clientRoot);
            byte[] rawBytes;
            if (texture.image_data.IsInline)
            {
                cabReader.Position = texture.image_data.Offset;
                rawBytes = cabReader.ReadBytes((int)texture.image_data.Size);
            }
            else
            {
                rawBytes = resolverSearch.ResolveStreamedBytes(texture.image_data.ExternalPath, texture.image_data.Offset, texture.image_data.Size);
            }

            if (!TextureExporter.TryDecodeToRgba(texture, rawBytes, out var rgba))
            {
                Console.WriteLine($"  skip (undecodable format {texture.m_TextureFormat}): {entry.Name}");
                continue;
            }

            var safeName = string.Join("_", entry.Name.Split(Path.GetInvalidFileNameChars()));
            var outPath = Path.Combine(outDir, $"{safeName}.png");
            TextureExporter.WritePng(rgba, texture.m_Width, texture.m_Height, outPath);
            written++;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  FAILED {entry.Name}: {e.GetType().Name}: {e.Message}");
        }
    }

    Console.WriteLine($"Wrote {written}/{matches.Count} textures to {outDir}");
    return;
}

if (args.Length > 0 && args[0] == "dump-texture")
{
    var texName = args[1];
    var outDir = args.Length > 2 ? args[2] : Path.GetTempPath();

    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var texEntries = assetMap.AssetEntries.Where(e => e.Name == texName && e.Type == ClassIDType.Texture2D).ToList();
    Console.WriteLine($"Found {texEntries.Count} Texture2D entries named '{texName}'");

    foreach (var entry in texEntries)
    {
        using var seedReader = new FileReader(entry.Source);
        seedReader.Position = entry.Offset;
        var seedVfs = new VFSFile(seedReader, entry.Source);

        foreach (var node in seedVfs.Nodes)
        {
            using var extracted = seedVfs.ExtractNode(node);
            using var candidateReader = new FileReader(node.path, extracted, leaveOpen: true);
            if (candidateReader.FileType != FileType.AssetsFile) continue;
            var candidateSf = new SerializedFile(candidateReader);
            if (!candidateSf.ObjectsByPathId.TryGetValue(entry.PathID, out var texInfo)) continue;

            candidateReader.Position = 0;
            var texture = new Texture2D(new ObjectReader(candidateReader, candidateSf, texInfo));
            Console.WriteLine($"  {texture.Name}: {texture.m_Width}x{texture.m_Height} format={texture.m_TextureFormat} inline={texture.image_data.IsInline}");

            byte[] rawBytes;
            var resolverTex = new AssetResolver(maps.Value.CabMap, clientRoot);
            if (texture.image_data.IsInline)
            {
                candidateReader.Position = texture.image_data.Offset;
                rawBytes = candidateReader.ReadBytes((int)texture.image_data.Size);
            }
            else
            {
                rawBytes = resolverTex.ResolveStreamedBytes(texture.image_data.ExternalPath, texture.image_data.Offset, texture.image_data.Size);
            }

            if (TextureExporter.TryDecodeToRgba(texture, rawBytes, out var rgba))
            {
                var outPath = Path.Combine(outDir, $"{texture.Name}_{entry.PathID}.png");
                TextureExporter.WritePng(rgba, texture.m_Width, texture.m_Height, outPath);
                Console.WriteLine($"    wrote {outPath}");
            }
            else
            {
                Console.WriteLine($"    failed to decode format {texture.m_TextureFormat}");
            }
            break;
        }
    }
    return;
}

if (args.Length > 0 && args[0] == "dump-full-tree")
{
    // Full raw type-tree JSON dump of one MonoBehaviour, unfiltered - for finding fields (e.g.
    // PPtrs to sibling/child decoration objects) that targeted field dumps didn't think to print.
    // `--container <pathContains>` dumps every MonoBehaviour in matching containers instead (for
    // prefabs whose components have no name), each with its file's externals so m_FileID resolves.
    var byContainer = args[1] == "--container";
    var assetName = byContainer ? args[2] : args[1];

    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var entries = assetMap.AssetEntries
        .Where(e => e.Type == ClassIDType.MonoBehaviour
                 && (byContainer ? e.Container != null && e.Container.Contains(assetName, StringComparison.OrdinalIgnoreCase) : e.Name == assetName))
        .ToList();
    if (entries.Count == 0) { Console.WriteLine("not found"); return; }
    if (!byContainer) entries = entries.Take(1).ToList();
    foreach (var entry in entries)
    {

    using var seedReader = new FileReader(entry.Source);
    seedReader.Position = entry.Offset;
    var seedVfs = new VFSFile(seedReader, entry.Source);

    SerializedFile sf = null;
    FileReader cabReader = null;
    foreach (var node in seedVfs.Nodes)
    {
        var extracted = seedVfs.ExtractNode(node);
        var candidateReader = new FileReader(node.path, extracted, leaveOpen: true);
        if (candidateReader.FileType == FileType.AssetsFile)
        {
            var candidateSf = new SerializedFile(candidateReader);
            if (candidateSf.ObjectsByPathId.ContainsKey(entry.PathID))
            {
                sf = candidateSf;
                cabReader = candidateReader;
                break;
            }
        }
        candidateReader.Dispose();
    }
    if (sf == null) { Console.WriteLine("container not found"); continue; }
    using var seedCabReaderFull = cabReader;

    var targetInfo = sf.ObjectsByPathId[entry.PathID];
    cabReader.Position = 0;
    var objReader = new ObjectReader(cabReader, sf, targetInfo);
    var tree = TypeTreeHelper.ReadType(targetInfo.serializedType.m_Type, objReader);

    if (byContainer)
    {
        Console.WriteLine($"// {entry.Container} PathID={entry.PathID}");
        for (var i = 0; i < sf.m_Externals.Count; i++)
            Console.WriteLine($"//   m_FileID {i + 1} = {sf.m_Externals[i].fileName}");
    }
    Console.WriteLine(JsonConvert.SerializeObject(tree, Formatting.Indented));
    }
    return;
}

if (args.Length > 0 && args[0] == "dump-material")
{
    var assetName = args[1];

    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var entry = assetMap.AssetEntries.FirstOrDefault(e => e.Name == assetName && e.Type == ClassIDType.MonoBehaviour);
    if (entry == null) { Console.WriteLine("not found"); return; }

    using var seedReader = new FileReader(entry.Source);
    seedReader.Position = entry.Offset;
    var seedVfs = new VFSFile(seedReader, entry.Source);

    SerializedFile sf = null;
    FileReader cabReader = null;
    foreach (var node in seedVfs.Nodes)
    {
        var extracted = seedVfs.ExtractNode(node);
        var candidateReader = new FileReader(node.path, extracted, leaveOpen: true);
        if (candidateReader.FileType == FileType.AssetsFile)
        {
            var candidateSf = new SerializedFile(candidateReader);
            if (candidateSf.ObjectsByPathId.ContainsKey(entry.PathID))
            {
                sf = candidateSf;
                cabReader = candidateReader;
                break;
            }
        }
        candidateReader.Dispose();
    }
    if (sf == null) { Console.WriteLine("container not found"); return; }
    using var seedCabReader = cabReader;

    var targetInfo = sf.ObjectsByPathId[entry.PathID];
    cabReader.Position = 0;
    var objReader = new ObjectReader(cabReader, sf, targetInfo);
    var tree = TypeTreeHelper.ReadType(targetInfo.serializedType.m_Type, objReader);

    Console.WriteLine($"All top-level keys: {string.Join(", ", ((OrderedDictionary)tree).Keys.Cast<string>())}");
    Console.WriteLine($"hidePreviewArrow={tree["hidePreviewArrow"]} logisticPreviewIndicator={tree["logisticPreviewIndicator"]} logisticEffect={tree["logisticEffect"]} showStateUI={tree["showStateUI"]}");
    if (tree["platformData"] is OrderedDictionary pd && pd["_keyData"] is List<object> pdKeys)
    {
        Console.WriteLine($"platformData: {pdKeys.Count} entries, keys={string.Join(",", pdKeys)}");
        if (pd["_valueData"] is List<object> pdValues && pdValues.Count > 0)
        {
            Console.WriteLine($"  first value: {JsonConvert.SerializeObject(pdValues[0])}");
        }
    }
    if (tree["effectCfgs"] is OrderedDictionary ec && ec["_keyData"] is List<object> ecKeys)
    {
        Console.WriteLine($"effectCfgs: {ecKeys.Count} entries, keys={string.Join(",", ecKeys)}");
        if (ec["_valueData"] is List<object> ecValues && ecValues.Count > 0)
        {
            Console.WriteLine($"  first value: {JsonConvert.SerializeObject(ecValues[0])}");
        }
    }
    foreach (var key in new[] { "extendConfigs", "busStructures", "colliderMeshes", "subAssets", "platformData", "vatSetting", "effectCfgs" })
    {
        var val = tree[key];
        if (val is List<object> list)
        {
            Console.WriteLine($"{key}: list with {list.Count} entries");
        }
        else if (val is OrderedDictionary dict)
        {
            Console.WriteLine($"{key}: dict with keys {string.Join(",", dict.Keys.Cast<string>())}");
        }
        else
        {
            Console.WriteLine($"{key}: {val}");
        }
    }

    var lodSettingsDbg = (OrderedDictionary)tree["lodSettings"];
    var lodKeysDbg = (List<object>)lodSettingsDbg["_keyData"];
    var lodValuesDbg = (List<object>)lodSettingsDbg["_valueData"];
    var bestIdxDbg = lodKeysDbg.Select((k, idx) => (tier: Convert.ToInt32(k), idx)).OrderBy(x => x.tier).First().idx;
    var bestLodDbg = (OrderedDictionary)lodValuesDbg[bestIdxDbg];
    var rendererArrDbg = (List<object>)bestLodDbg["m_rendererDataArr"];
    Console.WriteLine($"rendererDataArr count: {rendererArrDbg.Count}");
    foreach (var rd in rendererArrDbg.Cast<OrderedDictionary>())
    {
        var meshRefDbg = (OrderedDictionary)rd["m_mesh"];
        Console.WriteLine($"  mesh fileID={meshRefDbg["m_FileID"]} pathID={meshRefDbg["m_PathID"]}");
        var matsDbg = (List<object>)rd["m_materials"];
        foreach (var m in matsDbg.Cast<OrderedDictionary>())
        {
            Console.WriteLine($"    material fileID={m["m_FileID"]} pathID={m["m_PathID"]}");
        }
    }

    // additiveMats - separate from the LOD renderer materials, possibly the overlay/decal layer.
    var resolverDbg = new AssetResolver(maps.Value.CabMap, clientRoot);
    Material ResolveMaterialDbg(int fileId, long pathId)
    {
        if (fileId == 0)
        {
            var localInfo = sf.ObjectsByPathId.GetValueOrDefault(pathId);
            if (localInfo == null) return null;
            cabReader.Position = 0;
            return new Material(new ObjectReader(cabReader, sf, localInfo));
        }
        var externalName = sf.m_Externals[fileId - 1].fileName;
        var (extSf, extReader) = resolverDbg.OpenCab(externalName);
        if (extSf == null || !extSf.ObjectsByPathId.TryGetValue(pathId, out var extInfo)) { extReader?.Dispose(); return null; }
        extReader.Position = 0;
        var mat = new Material(new ObjectReader(extReader, extSf, extInfo));
        extReader.Dispose();
        return mat;
    }

    if (tree["additiveMats"] is List<object> additiveMats)
    {
        Console.WriteLine($"additiveMats count: {additiveMats.Count}");
        var idx = 0;
        foreach (var am in additiveMats.Cast<OrderedDictionary>())
        {
            var fid = Convert.ToInt32(am["m_FileID"]);
            var pid = Convert.ToInt64(am["m_PathID"]);
            var mat = ResolveMaterialDbg(fid, pid);
            Console.WriteLine($"  [{idx}] fileID={fid} pathID={pid} -> {(mat == null ? "FAILED" : mat.Name)}");
            if (mat != null)
            {
                foreach (var texEnv in mat.m_SavedProperties.m_TexEnvs)
                {
                    if (!texEnv.Value.m_Texture.IsNull)
                    {
                        Console.WriteLine($"      {texEnv.Key}: external={texEnv.Value.m_Texture.ExternalFileName} pathID={texEnv.Value.m_Texture.m_PathID}");
                    }
                }
            }
            idx++;
        }
    }

    return;
}

if (args.Length > 0 && args[0] == "list-missing")
{
    // Diffs against already-EXTRACTED models (.glb), not rendered sprites - the render step
    // is meant to run over every model present each time (for consistent lighting/camera
    // across the whole batch, even ones extracted in an earlier run), so only extraction
    // itself should skip work for buildings it's already pulled.
    var mapPath = maps.Value.AssetMap;
    var modelsDir = args.Length > 1 ? args[1] : DefaultModelsDir;

    using var stream = File.OpenRead(mapPath);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));

    var buildingEntries = assetMap.AssetEntries
        .Where(e => e.Type == ClassIDType.MonoBehaviour
                 && e.Container != null
                 && e.Container.Contains("ciautogenerated/pc/building/", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var missing = buildingEntries
        .Where(e => !File.Exists(Path.Combine(modelsDir, $"{e.Name}.glb")))
        .ToList();

    Console.WriteLine($"{buildingEntries.Count} total buildings, {buildingEntries.Count - missing.Count} already extracted, {missing.Count} missing:");
    foreach (var e in missing)
    {
        Console.WriteLine($"  {e.Name}");
    }
    return;
}

if (args.Length > 0 && args[0] == "dump-body-material")
{
    // Debug: dump the ACTUAL body renderer material (not additiveMats) full property sheet -
    // m_TexEnvs, m_Floats, m_Colors - to see what tint/palette properties exist that the
    // base-color-only extraction path currently ignores.
    var assetName = args[1];

    using var mapStream = File.OpenRead(maps.Value.AssetMap);
    var assetMapDbm = MessagePackSerializer.Deserialize<AssetMap>(mapStream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var entryDbm = assetMapDbm.AssetEntries.FirstOrDefault(e => e.Name == assetName && e.Type == ClassIDType.MonoBehaviour);
    if (entryDbm == null) { Console.WriteLine("not found"); return; }

    using var seedReaderDbm = new FileReader(entryDbm.Source);
    seedReaderDbm.Position = entryDbm.Offset;
    var seedVfsDbm = new VFSFile(seedReaderDbm, entryDbm.Source);

    SerializedFile sfDbm = null;
    FileReader cabReaderDbm = null;
    foreach (var node in seedVfsDbm.Nodes)
    {
        var extracted = seedVfsDbm.ExtractNode(node);
        var candidateReader = new FileReader(node.path, extracted, leaveOpen: true);
        if (candidateReader.FileType == FileType.AssetsFile)
        {
            var candidateSf = new SerializedFile(candidateReader);
            if (candidateSf.ObjectsByPathId.ContainsKey(entryDbm.PathID))
            {
                sfDbm = candidateSf;
                cabReaderDbm = candidateReader;
                break;
            }
        }
        candidateReader.Dispose();
    }
    if (sfDbm == null) { Console.WriteLine("container not found"); return; }
    using var seedCabReaderDbm = cabReaderDbm;

    var targetInfoDbm = sfDbm.ObjectsByPathId[entryDbm.PathID];
    cabReaderDbm.Position = 0;
    var objReaderDbm = new ObjectReader(cabReaderDbm, sfDbm, targetInfoDbm);
    var treeDbm = TypeTreeHelper.ReadType(targetInfoDbm.serializedType.m_Type, objReaderDbm);

    var lodSettingsDbm = (OrderedDictionary)treeDbm["lodSettings"];
    var lodKeysDbm = (List<object>)lodSettingsDbm["_keyData"];
    var lodValuesDbm = (List<object>)lodSettingsDbm["_valueData"];
    var bestIdxDbm = lodKeysDbm.Select((k, idx) => (tier: Convert.ToInt32(k), idx)).OrderBy(x => x.tier).First().idx;
    var bestLodDbm = (OrderedDictionary)lodValuesDbm[bestIdxDbm];
    var rendererArrDbm = (List<object>)bestLodDbm["m_rendererDataArr"];
    var rendererDataDbm = (OrderedDictionary)rendererArrDbm[0];
    var matsDbm = (List<object>)rendererDataDbm["m_materials"];
    var matIndexDbm = args.Length > 2 ? int.Parse(args[2]) : 0;
    Console.WriteLine($"Material slot {matIndexDbm} of {matsDbm.Count}");
    var matRefDbm = (OrderedDictionary)matsDbm[matIndexDbm];
    var matFileIdDbm = Convert.ToInt32(matRefDbm["m_FileID"]);
    var matPathIdDbm = Convert.ToInt64(matRefDbm["m_PathID"]);

    var resolverDbm = new AssetResolver(maps.Value.CabMap, clientRoot);
    Material bodyMat;
    if (matFileIdDbm == 0)
    {
        var localInfo = sfDbm.ObjectsByPathId[matPathIdDbm];
        cabReaderDbm.Position = 0;
        bodyMat = new Material(new ObjectReader(cabReaderDbm, sfDbm, localInfo));
    }
    else
    {
        var externalName = sfDbm.m_Externals[matFileIdDbm - 1].fileName;
        var (extSf, extReader) = resolverDbm.OpenCab(externalName);
        var extInfo = extSf.ObjectsByPathId[matPathIdDbm];
        extReader.Position = 0;
        bodyMat = new Material(new ObjectReader(extReader, extSf, extInfo));
        extReader.Dispose();
    }

    Console.WriteLine($"Body material: {bodyMat.Name}, shader external={bodyMat.m_Shader.ExternalFileName} pathID={bodyMat.m_Shader.m_PathID}");
    Console.WriteLine("m_TexEnvs:");
    foreach (var t in bodyMat.m_SavedProperties.m_TexEnvs)
    {
        Console.WriteLine($"  {t.Key}: null={t.Value.m_Texture.IsNull} external={t.Value.m_Texture.ExternalFileName} pathID={t.Value.m_Texture.m_PathID} scale={t.Value.m_Scale} offset={t.Value.m_Offset}");
    }
    Console.WriteLine("m_Floats:");
    foreach (var f in bodyMat.m_SavedProperties.m_Floats)
    {
        Console.WriteLine($"  {f.Key}: {f.Value}");
    }
    Console.WriteLine("m_Colors:");
    foreach (var c in bodyMat.m_SavedProperties.m_Colors)
    {
        Console.WriteLine($"  {c.Key}: r={c.Value.R} g={c.Value.G} b={c.Value.B} a={c.Value.A}");
    }
    if (bodyMat.m_SavedProperties.m_Ints != null)
    {
        Console.WriteLine("m_Ints:");
        foreach (var iv in bodyMat.m_SavedProperties.m_Ints)
        {
            Console.WriteLine($"  {iv.Key}: {iv.Value}");
        }
    }

    // Optional 4th arg: resolve and dump the actual decoded texture for a named tex-env
    // property (e.g. _EmissiveMap) by PPtr, not by guessing an AssetMap name.
    if (args.Length > 3)
    {
        var propName = args[3];
        var texEnv = bodyMat.m_SavedProperties.m_TexEnvs.FirstOrDefault(t => t.Key == propName);
        if (texEnv.Value?.m_Texture == null || texEnv.Value.m_Texture.IsNull)
        {
            Console.WriteLine($"'{propName}' not found or null.");
            return;
        }
        var texPPtr = texEnv.Value.m_Texture;
        var (texSf, texReader) = resolverDbm.OpenCab(texPPtr.ExternalFileName);
        if (texSf == null || !texSf.ObjectsByPathId.TryGetValue(texPPtr.m_PathID, out var texInfo))
        {
            Console.WriteLine($"Could not resolve texture object for '{propName}'.");
            return;
        }
        texReader.Position = 0;
        var texture = new Texture2D(new ObjectReader(texReader, texSf, texInfo));
        Console.WriteLine($"{propName}: '{texture.Name}' {texture.m_Width}x{texture.m_Height} format={texture.m_TextureFormat}");

        byte[] rawBytes;
        if (texture.image_data.IsInline)
        {
            texReader.Position = texture.image_data.Offset;
            rawBytes = texReader.ReadBytes((int)texture.image_data.Size);
        }
        else
        {
            rawBytes = resolverDbm.ResolveStreamedBytes(texture.image_data.ExternalPath, texture.image_data.Offset, texture.image_data.Size);
        }
        texReader.Dispose();

        if (!TextureExporter.TryDecodeToRgba(texture, rawBytes, out var rgba))
        {
            Console.WriteLine("Failed to decode.");
            return;
        }
        var outDir = Path.GetTempPath();
        var outPath = Path.Combine(outDir, $"{assetName}_{propName}_{texture.Name}.png");
        TextureExporter.WritePng(rgba, texture.m_Width, texture.m_Height, outPath);
        Console.WriteLine($"Wrote {outPath}");
    }
    return;
}

if (args.Length > 0 && args[0] == "dump-part-texture")
{
    var assetName = args[1];
    var outDir = args.Length > 2 ? args[2] : Path.GetTempPath();
    var extractorTex = new BuildingExtractor(maps.Value.AssetMap, maps.Value.CabMap, clientRoot);
    var partTex = extractorTex.ResolvePart(assetName);
    if (partTex == null || partTex.MaterialSlots.Count == 0)
    {
        Console.WriteLine("No material slots resolved.");
        return;
    }
    var wrote = 0;
    for (var i = 0; i < partTex.MaterialSlots.Count; i++)
    {
        var slot = partTex.MaterialSlots[i];
        if (slot.BaseColorPng == null)
        {
            Console.WriteLine($"  slot {i} ({slot.MaterialName}): no base color texture");
            continue;
        }
        var outPath = Path.Combine(outDir, $"{assetName}_slot{i}_basecolor.png");
        File.WriteAllBytes(outPath, slot.BaseColorPng);
        Console.WriteLine($"Wrote {outPath}");
        wrote++;
    }
    if (wrote == 0)
    {
        Console.WriteLine("No base color texture resolved in any slot.");
    }
    return;
}

// Reverse index: machineRenderId (an AssetMap mesh name) -> the (buildingDef, template) that
// names it. The table's top-level keys are gameplay building ids, not mesh names, and several
// real mesh variants (xiranite_oven_2, liquid_purifier_2, sp_sub_hub_2/3/4,
// battle_cannon_dg002_1 - confirmed via a full scan of every rendererTemplateMap entry) only
// ever appear as a machineRenderId value nested inside a DIFFERENT building's table entry, never
// as a top-level key themselves. Built once per batch, not per building.
Dictionary<string, (JObject buildingDef, JObject template)> BuildMachineRenderIdIndex(JObject tableJson)
{
    var index = new Dictionary<string, (JObject, JObject)>();
    foreach (var prop in tableJson.Properties())
    {
        if (prop.Value is not JObject buildingDef) continue;
        if (buildingDef["rendererTemplateMap"] is not JObject templateMap) continue;
        foreach (var templateProp in templateMap.Properties())
        {
            if (templateProp.Value is not JObject template) continue;
            var renderId = template["machineRenderId"]?.Value<string>();
            if (!string.IsNullOrEmpty(renderId) && !index.ContainsKey(renderId))
            {
                index[renderId] = (buildingDef, template);
            }
        }
    }
    return index;
}

// Placeholder port-direction marker textures (see assets/) - the real
// in-game asset for this couldn't be located after an extensive search (data tables, AssetMap
// PPtr chains, ~900 texture names/images), so these are hand-drawn stand-ins purely to validate
// port position/rotation placement; swap the PNG files for real art later, no code change needed.
var portArrowOutputPngPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "assets", "port_arrow_output.png");
var portArrowInputPngPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "assets", "port_arrow_input.png");
var portArrowOutputPng = File.Exists(portArrowOutputPngPath) ? File.ReadAllBytes(portArrowOutputPngPath) : null;
var portArrowInputPng = File.Exists(portArrowInputPngPath) ? File.ReadAllBytes(portArrowInputPngPath) : null;

// Extracts one building (body + base platform, per FactoryBuildingTable.json's
// rendererTemplateMap) to a combined .glb. Returns true on success.
bool ExtractBuilding(BuildingExtractor extractor, JObject tableJson, Dictionary<string, (JObject buildingDef, JObject template)> machineRenderIdIndex, string buildingId, string modelsOutDir, bool verbose)
{
    void Log(string msg) { if (verbose) Console.WriteLine(msg); }

    Log($"== Extracting '{buildingId}' ==");

    // Both body and base pedestal are separately-extractable FactoryUnitConfigAssets that the
    // game composites at the same identity transform (confirmed: both have localTransform
    // position (0,0,0), no rotation/scale), so we just extract both and place them as sibling
    // nodes - no relative offsetting needed.
    string machineRenderId = buildingId;
    string logisticBaseUnit = null;
    JObject template = null;
    JObject owningBuildingDef = null;

    // 1) Direct key match: buildingId IS a table entry (the common case).
    if (tableJson[buildingId] is JObject directDef)
    {
        owningBuildingDef = directDef;
        var defaultTemplate = directDef["defaultRendererTemplate"]?.Value<string>();
        var templateMap = directDef["rendererTemplateMap"] as JObject;
        template = (defaultTemplate != null ? templateMap?[defaultTemplate] : null) as JObject
                  ?? templateMap?.Properties().FirstOrDefault()?.Value as JObject;
        if (template != null)
        {
            machineRenderId = template["machineRenderId"]?.Value<string>() ?? buildingId;
        }
        Log($"  direct table match");
    }
    // 2) machineRenderId reverse index: buildingId is itself a mesh name that only appears
    // nested inside another building's template (e.g. xiranite_oven_2 inside xiranite_oven_1).
    else if (machineRenderIdIndex.TryGetValue(buildingId, out var indexed))
    {
        template = indexed.template;
        owningBuildingDef = indexed.buildingDef;
        machineRenderId = buildingId;
        Log($"  matched via machineRenderId index (owning id={indexed.buildingDef["id"]?.Value<string>()})");
    }
    // 3) Suffix-stripped fallback: buildingId is a lettered mesh variant the table never names
    // directly at all (e.g. gas_reactor_1_01a/_01b - the table only knows the bare
    // "gas_reactor_1", whose own machineRenderId has no mesh). Confirmed no real table key or
    // machineRenderId value matches this suffix pattern, so it can't collide with a legitimate
    // distinct entry.
    else if (System.Text.RegularExpressions.Regex.IsMatch(buildingId, @"_\d{2}[a-z]?$"))
    {
        var baseId = System.Text.RegularExpressions.Regex.Replace(buildingId, @"_\d{2}[a-z]?$", "");
        if (tableJson[baseId] is JObject baseDef)
        {
            owningBuildingDef = baseDef;
            var defaultTemplate = baseDef["defaultRendererTemplate"]?.Value<string>();
            var templateMap = baseDef["rendererTemplateMap"] as JObject;
            template = (defaultTemplate != null ? templateMap?[defaultTemplate] : null) as JObject
                      ?? templateMap?.Properties().FirstOrDefault()?.Value as JObject;
            machineRenderId = buildingId; // use the actual suffixed mesh, not the base entry's (likely bare/meshless) machineRenderId
            Log($"  matched via suffix-stripped base '{baseId}'");
        }
    }

    if (template != null)
    {
        var logistic = template["logisticBaseUnit"]?.Value<string>();
        var nonLogistic = template["nonLogisticBaseUnit"]?.Value<string>();
        logisticBaseUnit = !string.IsNullOrEmpty(logistic) ? logistic
                          : !string.IsNullOrEmpty(nonLogistic) ? nonLogistic
                          : null;
        Log($"  machineRenderId={machineRenderId} baseUnit={logisticBaseUnit ?? "(none)"}");
    }
    else
    {
        Log($"  '{buildingId}' not found in FactoryBuildingTable.json (direct, indexed, or suffix-stripped) - extracting body mesh only, no base platform.");
    }

    var parts = new List<GltfPart>();

    var bodyPart = extractor.ResolvePart(machineRenderId);
    if (bodyPart == null)
    {
        Log($"  FAILED: could not resolve body '{machineRenderId}'.");
        return false;
    }
    Log($"  Body: '{bodyPart.Name}' {bodyPart.VertexCount} verts");
    parts.Add(bodyPart);

    if (logisticBaseUnit != null)
    {
        var basePart = extractor.ResolvePart(logisticBaseUnit);
        if (basePart == null)
        {
            Log($"  Warning: failed to resolve base platform '{logisticBaseUnit}', exporting body only.");
        }
        else
        {
            Log($"  Base: '{basePart.Name}' {basePart.VertexCount} verts");
            parts.Add(basePart);
        }
    }

    // Synthetic port-direction markers, placed from FactoryBuildingTable.json's own
    // inputPorts/outputPorts transforms (position + Y-axis rotation) - not backed by any game
    // mesh, since the real in-game marker asset couldn't be located (see the module-level
    // comment above portArrowOutputPngPath). Sized at exactly 1 world unit - the full grid-cell
    // tile, no margin - matching the 1-unit spacing between adjacent port grid coordinates.
    //
    // Port trans.position.x/z are 0-indexed GRID-CELL coordinates within the building's
    // range.width x range.depth footprint (confirmed on storager_1: a 3x3 building has input
    // ports at x=0,1,2 all on one edge and output ports at x=0,1,2 on the opposite edge - i.e.
    // spanning the full footprint from a corner, not centered) - NOT world-space offsets from
    // the mesh origin, which is footprint-centered like every extracted body/pedestal mesh.
    // Recenter by subtracting (extent-1)/2 per axis so a port at grid-cell 0 of a width-3
    // footprint lands at world x=-1, matching the mesh's own -1.5..1.5 span.
    if (owningBuildingDef != null && (portArrowOutputPng != null || portArrowInputPng != null))
    {
        // The grid cell is nominally 1 world unit (matching range.width/depth), but real
        // pedestal meshes render slightly smaller than their nominal grid size - e.g.
        // storager_1's pedestal spans 2.93 units for a logical 3x3 footprint, not exactly 3.0.
        // cellPadding scales the WHOLE port-grid plane in toward the footprint center (not each
        // marker's own size in place) - this is a uniform shrink of the coordinate space the
        // ports live in, so the whole arrangement insets evenly from the platform edge and
        // marker spacing shrinks proportionally with it, keeping cells touching rather than
        // opening gaps between them.
        const float cellPadding = 0.95f;
        var range = owningBuildingDef["range"];
        var footprintWidth = range?["width"]?.Value<float>() ?? 1f;
        var footprintDepth = range?["depth"]?.Value<float>() ?? 1f;
        var originOffsetX = (footprintWidth - 1f) / 2f;
        var originOffsetZ = (footprintDepth - 1f) / 2f;
        var markerSize = 1.0f * cellPadding;

        void AddPortMarkers(string fieldName, byte[] png, string materialName)
        {
            if (png == null || owningBuildingDef[fieldName] is not JArray ports) return;
            foreach (var port in ports)
            {
                // Only solid/belt ports get the directional arrow marker - fluid/pipe ports
                // (isPipe: true, e.g. log_pipe_* buildings) use a visually distinct in-game
                // connector (a physical pipe-socket mesh feature, not a flat stamped decal) so
                // stamping the same arrow there would be wrong, not just redundant.
                if (port["isPipe"]?.Value<bool>() == true) continue;

                var trans = port["trans"];
                if (trans == null) continue;
                var pos = trans["position"];
                var rot = trans["rotation"];
                var gridX = pos?["x"]?.Value<float>() ?? 0f;
                var py = pos?["y"]?.Value<float>() ?? 0f;
                var gridZ = pos?["z"]?.Value<float>() ?? 0f;
                var yaw = rot?["y"]?.Value<float>() ?? 0f;
                var index = port["index"]?.Value<int>() ?? 0;
                // Center relative to the footprint first, THEN scale the whole plane by
                // cellPadding - scaling before centering would shrink toward grid-cell 0
                // (a corner), not toward the footprint's actual center.
                var worldX = (gridX - originOffsetX) * cellPadding;
                var worldZ = (gridZ - originOffsetZ) * cellPadding;
                // Pedestal meshes have real height variation (e.g. storager_1's pedestal top
                // surface/rim sits up to y=0.58, not flat at y=0) - a marker too close to y=0
                // sits buried under that geometry.
                parts.Add(GltfPart.Quad($"{materialName}_{index}", markerSize, markerSize, worldX, py + 0.10f, worldZ, yaw, png, materialName));
            }
        }
        AddPortMarkers("outputPorts", portArrowOutputPng, "port_output_marker");
        AddPortMarkers("inputPorts", portArrowInputPng, "port_input_marker");
    }

    var glbOutPath = Path.Combine(modelsOutDir, $"{buildingId}.glb");
    GltfExporter.ExportGlb(glbOutPath, parts);
    Log($"Wrote glTF: {glbOutPath}");
    return true;
}

if (args.Length > 0 && args[0] == "extract-all")
{
    var modelsOutDirAll = args.Length > 1 ? args[1] : DefaultModelsDir;
    Directory.CreateDirectory(modelsOutDirAll);

    using var stream = File.OpenRead(maps.Value.AssetMap);
    var assetMap = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
    var buildingEntries = assetMap.AssetEntries
        .Where(e => e.Type == ClassIDType.MonoBehaviour
                 && e.Container != null
                 && e.Container.Contains("ciautogenerated/pc/building/", StringComparison.OrdinalIgnoreCase)
                 && !e.Name.StartsWith("battle_", StringComparison.OrdinalIgnoreCase)
                 && !KnownNiyBuildingIds.Contains(e.Name))
        .ToList();

    var toExtract = buildingEntries
        .Where(e => !File.Exists(Path.Combine(modelsOutDirAll, $"{e.Name}.glb")))
        .ToList();

    Console.WriteLine($"{buildingEntries.Count} total buildings, {buildingEntries.Count - toExtract.Count} already extracted, extracting {toExtract.Count}...");

    var tableJsonAll = LoadMergedBuildingTable();
    var machineRenderIdIndexAll = BuildMachineRenderIdIndex(tableJsonAll);
    var extractorAll = new BuildingExtractor(maps.Value.AssetMap, maps.Value.CabMap, clientRoot);

    var succeeded = 0;
    var failed = new List<string>();
    for (var i = 0; i < toExtract.Count; i++)
    {
        var name = toExtract[i].Name;
        Console.Write($"[{i + 1}/{toExtract.Count}] {name} ... ");
        try
        {
            if (ExtractBuilding(extractorAll, tableJsonAll, machineRenderIdIndexAll, name, modelsOutDirAll, verbose: false))
            {
                Console.WriteLine("ok");
                succeeded++;
            }
            else
            {
                Console.WriteLine("FAILED (no body mesh)");
                failed.Add(name);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"FAILED ({e.GetType().Name}: {e.Message})");
            failed.Add(name);
        }
    }

    Console.WriteLine($"Done. {succeeded} succeeded, {failed.Count} failed.");
    if (failed.Count > 0)
    {
        Console.WriteLine("Failed: " + string.Join(", ", failed));
    }
    return;
}

// --- Default mode: extract one building (body + base platform) to a combined .glb ---

var buildingId = args.Length > 0 ? args[0] : "storager_1";
var modelsOutDir = args.Length > 1 ? args[1] : DefaultModelsDir;
Directory.CreateDirectory(modelsOutDir);

var tableJsonSingle = LoadMergedBuildingTable();
var machineRenderIdIndexSingle = BuildMachineRenderIdIndex(tableJsonSingle);
var extractorSingle = new BuildingExtractor(maps.Value.AssetMap, maps.Value.CabMap, clientRoot);
if (!ExtractBuilding(extractorSingle, tableJsonSingle, machineRenderIdIndexSingle, buildingId, modelsOutDir, verbose: true))
{
    Environment.Exit(1);
}
