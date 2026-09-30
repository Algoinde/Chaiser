using System;
using System.Text;

namespace Chaiser
{
    [Flags]
    public enum ArchiveFlags
    {
        CompressionTypeMask = 0x3f,
        BlocksAndDirectoryInfoCombined = 0x40,
        BlocksInfoAtTheEnd = 0x80,
        OldWebPluginCompatibility = 0x100,
        BlockInfoNeedPaddingAtStart = 0x200,
    }

    [Flags]
    public enum StorageBlockFlags
    {
        CompressionTypeMask = 0x3f,
        Streamed = 0x40,
    }

    public class VfsHeader
    {
        public string signature;
        public uint version;
        public string unityVersion;
        public string unityRevision;
        public long size;
        public uint compressedBlocksInfoSize;
        public uint uncompressedBlocksInfoSize;
        public ArchiveFlags flags;
        public uint encFlags;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"signature: {signature} | ");
            sb.Append($"version: {version} | ");
            sb.Append($"unityVersion: {unityVersion} | ");
            sb.Append($"unityRevision: {unityRevision} | ");
            sb.Append($"size: 0x{size:X8} | ");
            sb.Append($"compressedBlocksInfoSize: 0x{compressedBlocksInfoSize:X8} | ");
            sb.Append($"uncompressedBlocksInfoSize: 0x{uncompressedBlocksInfoSize:X8} | ");
            sb.Append($"flags: 0x{(int)flags:X8}");
            return sb.ToString();
        }
    }

    public class VfsStorageBlock
    {
        public uint compressedSize;
        public uint uncompressedSize;
        public StorageBlockFlags flags;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"compressedSize: 0x{compressedSize:X8} | ");
            sb.Append($"uncompressedSize: 0x{uncompressedSize:X8} | ");
            sb.Append($"flags: 0x{(int)flags:X8}");
            return sb.ToString();
        }
    }

    public class VfsNode
    {
        public long offset;
        public long size;
        public uint flags;
        public string path;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"offset: 0x{offset:X8} | ");
            sb.Append($"size: 0x{size:X8} | ");
            sb.Append($"flags: {flags} | ");
            sb.Append($"path: {path}");
            return sb.ToString();
        }
    }
}
