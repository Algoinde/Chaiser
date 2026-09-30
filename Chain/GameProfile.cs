namespace Chaiser
{
    /// <summary>How a game's archives are recognized and opened. Everything above the archive is shared.</summary>
    public interface IArchiveFormat
    {
        /// <summary>Whether an archive starts at the reader's position (position unchanged).</summary>
        bool IsHeader(FileReader reader);

        /// <summary>Parses the archive starting at the reader's position.</summary>
        BlockArchive Open(FileReader reader, string path);
    }

    public sealed class VfsFormat : IArchiveFormat
    {
        public bool IsHeader(FileReader reader) => VFSUtils.IsValidHeader(reader);
        public BlockArchive Open(FileReader reader, string path) => new VFSFile(reader, path);
    }

    /// <summary>
    /// Which variant of the engine a quirk belongs to. A quirk keys on the narrowest thing that
    /// explains it: the Unity version (read from each file, not stored here) for stock Unity
    /// differences, Tech for a vendor's engine changes shared across its games ("mhy", "hg"), Game
    /// for one title's own ("ef", "hsr").
    /// </summary>
    public sealed record Dialect(string Game, string Tech);

    /// <summary>
    /// Everything game-specific, chosen once: where the client keeps its archives, which files
    /// fingerprint an install (for the map cache), the archive format, and its Dialect.
    /// </summary>
    public sealed class GameProfile
    {
        public required string Name { get; init; }
        public required GameType GameType { get; init; }
        public required Dialect Dialect { get; init; }
        public required string DefaultClient { get; init; }

        /// <summary>
        /// Files that change with every update or hotfix (absolute paths; missing ones are skipped).
        /// Small files are hashed by content, big ones (> 4 MB, e.g. manifests with a hash in their
        /// name) by name and size.
        /// </summary>
        public required Func<string, IEnumerable<string>> FingerprintFiles { get; init; }

        /// <summary>Archive files, highest priority first (a CAB found in an earlier file wins).</summary>
        public required Func<string, IEnumerable<string>> ArchiveFiles { get; init; }

        /// <summary>Plain SerializedFiles outside the archives (resources.assets, level0, ...).</summary>
        public Func<string, IEnumerable<string>> LooseFiles { get; init; } = _ => [];

        public required IArchiveFormat Format { get; init; }

        public static readonly GameProfile Endfield = new()
        {
            Name = "ef",
            GameType = GameType.ArknightsEndfield,
            Dialect = new Dialect("ef", "hg"),
            DefaultClient = @"Z:\clients\endfield\EndField Game\Endfield_Data",
            FingerprintFiles = client => new[]
            {
                @"StreamingAssets\index_initial.json", @"StreamingAssets\index_main.json",
                @"Persistent\index_initial.json", @"Persistent\index_main.json",
            }.Select(rel => Path.Combine(client, rel)),
            // Persistent (hotfixes) first: it shadows StreamingAssets in-game.
            ArchiveFiles = client => new[] { @"Persistent\VFS", @"StreamingAssets\VFS" }
                .Select(rel => Path.Combine(client, rel))
                .Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, "*.chk", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)),
            LooseFiles = client => Directory.EnumerateFiles(client)
                .Concat(Directory.Exists(Path.Combine(client, "Resources")) ? Directory.EnumerateFiles(Path.Combine(client, "Resources")) : [])
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase),
            Format = new VfsFormat(),
        };

        public static readonly GameProfile StarRail = new()
        {
            Name = "hsr",
            GameType = GameType.SR,
            Dialect = new Dialect("hsr", "mhy"),
            DefaultClient = @"Z:\clients\bhrpg\OSREL\StarRail_Data",
            // BinaryVersion.bytes moves with versions; the M_*.bytes pointers and the Persistent
            // manifests (AsbV_/BlockV_<hash>.bytes) and InstallVersion.bin with hotfixes.
            FingerprintFiles = client => new[] { @"StreamingAssets\BinaryVersion.bytes", @"Persistent\InstallVersion.bin" }
                .Select(rel => Path.Combine(client, rel))
                .Concat(Files(client, @"StreamingAssets\Asb\Windows", "M_*.bytes"))
                .Concat(Files(client, @"Persistent\Asb\Windows", "*V_*.bytes")),
            // Persistent (hotfixes) first: it shadows StreamingAssets in-game.
            ArchiveFiles = client => Files(client, @"Persistent\Asb\Windows", "*.block")
                .Concat(Files(client, @"StreamingAssets\Asb\Windows", "*.block")),
            Format = new EncrFormat(),
        };

        public static readonly IReadOnlyList<GameProfile> All = [Endfield, StarRail];

        public static GameProfile Get(string name) =>
            All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown game '{name}' (known: {string.Join(", ", All.Select(p => p.Name))})");

        static IEnumerable<string> Files(string client, string rel, string pattern)
        {
            var dir = Path.Combine(client, rel);
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                : [];
        }
    }
}
