using System.Collections.Concurrent;

namespace Chaiser
{
    /// <summary>A file on disk holding one or more archives back to back.</summary>
    public sealed record RawFile(string Path, string Relative)
    {
        public override string ToString() => Relative;
    }

    /// <summary>An archive to open at a known offset (from a map), with the objects wanted from it.</summary>
    public sealed record ContainerRef(RawFile File, long Offset, ConcurrentDictionary<long, AssetEntry> Wanted = null)
    {
        public override string ToString() => $"{File}@{Offset}";
    }

    /// <summary>One archive (block table + directory parsed, nothing inflated).</summary>
    public sealed class Container
    {
        public Container(RawFile file, long offset, BlockArchive archive, ConcurrentDictionary<long, AssetEntry> wanted = null)
        {
            File = file;
            Offset = offset;
            Archive = archive;
            Wanted = wanted;
        }

        public RawFile File { get; }
        public long Offset { get; }
        public BlockArchive Archive { get; }

        /// <summary>
        /// Objects still to find, by PathID (null: everything). Stages take what they find (TryRemove,
        /// so each is taken once even if the stages below fan out).
        /// </summary>
        public ConcurrentDictionary<long, AssetEntry> Wanted { get; }

        public override string ToString() => $"{File}@{Offset}";
    }

    public sealed record NodeItem(Container Container, VfsNode Node)
    {
        public override string ToString() => $"{Container}/{Node.path}";
    }

    /// <summary>A parsed SerializedFile over its node's lazy stream; disposing closes the stream.</summary>
    public sealed class AssetsFileItem : IDisposable
    {
        public AssetsFileItem(NodeItem node, FileReader reader, SerializedFile file)
        {
            Node = node;
            Reader = reader;
            File = file;
        }

        public NodeItem Node { get; }
        public FileReader Reader { get; }
        public SerializedFile File { get; }
        public Container Container => Node.Container;

        public void Dispose() => Reader.Dispose();
        public override string ToString() => Node.ToString();
    }

    /// <summary>One object in an assets file; Entry is the map entry it was requested by, if any.</summary>
    public sealed record ObjectItem(AssetsFileItem Owner, ObjectInfo Info, AssetEntry Entry)
    {
        public override string ToString() => $"{Owner}#{Info.m_PathID}";
    }

    public sealed record TextureImage(AssetEntry Entry, byte[] Rgba, int Width, int Height);

    public sealed record Written(AssetEntry Entry, string Path);
}
