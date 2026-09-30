using System.Buffers;

namespace Chaiser
{
    /// <summary>
    /// Star Rail's ENCR bundle: a UnityFS archive under another signature. A .block is several of
    /// them back to back. Differences from stock UnityFS (from AnimeStudio's BundleFile SR paths):
    /// the signature is "ENCR" with no version/unity strings after it, there is no extra byte
    /// after the flags and no 16-byte alignment before the block info, and the block info has
    /// no uncompressed-data hash. Blocks are Oodle (6), Oodle + Mr0k (7), or stored.
    /// </summary>
    public sealed class EncrFile : BlockArchive
    {
        const int HeaderSize = 5 + 8 + 4 + 4 + 4; // "ENCR\0", size, compressed/uncompressed block info sizes, flags
        static readonly byte[] Signature = "ENCR\0"u8.ToArray();

        public static bool IsHeader(FileReader reader)
        {
            var position = reader.Position;
            if (position + HeaderSize > reader.BaseStream.Length) return false;
            var magic = reader.ReadBytes(5);
            reader.Position = position;
            return magic.AsSpan().SequenceEqual(Signature);
        }

        public EncrFile(FileReader reader, string path) : base(path)
        {
            var offset = reader.Position;
            reader.Endian = EndianType.BigEndian;
            if (!IsHeader(reader)) throw new InvalidDataException("Not an ENCR bundle");
            reader.Position += 5;
            var size = reader.ReadInt64();
            var compressedInfoSize = reader.ReadUInt32();
            var uncompressedInfoSize = reader.ReadUInt32();
            var flags = (ArchiveFlags)reader.ReadUInt32();

            var infoAtEnd = (flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0;
            reader.Position = infoAtEnd ? offset + size - compressedInfoSize : offset + HeaderSize;
            var info = reader.ReadBytes((int)compressedInfoSize);
            var (blocks, nodes) = ReadBlocksInfo(DecodeInfo(info, (int)uncompressedInfoSize, (int)flags & 0x3F));

            long dataOffset = offset + HeaderSize + (infoAtEnd ? 0 : compressedInfoSize);
            if ((flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0) dataOffset = (dataOffset + 15) & ~15L;
            Init(blocks, nodes, dataOffset);
            if (NextContainerOffset != offset + size)
                throw new InvalidDataException($"ENCR blocks end at {NextContainerOffset}, header says {offset + size}");
        }

        static byte[] DecodeInfo(byte[] info, int uncompressedSize, int compression)
        {
            if (compression == 0) return info;
            var output = new byte[uncompressedSize];
            var written = Decode(info, output, compression);
            if (written != uncompressedSize) throw new IOException($"Block info decoded to {written} of {uncompressedSize} bytes");
            return output;
        }

        static (List<VfsStorageBlock>, List<VfsNode>) ReadBlocksInfo(byte[] info)
        {
            using var reader = new EndianBinaryReader(new MemoryStream(info));
            reader.Endian = EndianType.BigEndian;
            var blocks = new List<VfsStorageBlock>();
            for (int i = 0, count = reader.ReadInt32(); i < count; i++)
            {
                blocks.Add(new VfsStorageBlock
                {
                    uncompressedSize = reader.ReadUInt32(),
                    compressedSize = reader.ReadUInt32(),
                    flags = (StorageBlockFlags)reader.ReadUInt16(),
                });
            }
            var nodes = new List<VfsNode>();
            for (int i = 0, count = reader.ReadInt32(); i < count; i++)
            {
                nodes.Add(new VfsNode
                {
                    offset = reader.ReadInt64(),
                    size = reader.ReadInt64(),
                    flags = reader.ReadUInt32(),
                    path = reader.ReadStringToNull(),
                });
            }
            return (blocks, nodes);
        }

        protected override int DecodeBlock(Span<byte> compressed, Span<byte> output, StorageBlockFlags flags) =>
            Decode(compressed, output, (int)(flags & StorageBlockFlags.CompressionTypeMask));

        static int Decode(Span<byte> compressed, Span<byte> output, int compression)
        {
            switch (compression)
            {
                case 0:
                    compressed.CopyTo(output);
                    return compressed.Length;
                case 2: // LZ4
                case 3: // LZ4HC
                    return LZ4.Instance.Decompress(compressed, output);
                case 5: // LZ4 + Mr0k
                    return LZ4.Instance.Decompress(Mr0k.IsMr0k(compressed) ? Mr0k.StarRail.Decrypt(compressed) : compressed, output);
                case 6: // Oodle
                    return Oodle.Decompress(compressed, output);
                case 7: // Oodle + Mr0k
                    return Oodle.Decompress(Mr0k.IsMr0k(compressed) ? Mr0k.StarRail.Decrypt(compressed) : compressed, output);
                default:
                    throw new NotSupportedException($"Unsupported ENCR compression type {compression}");
            }
        }
    }

    public sealed class EncrFormat : IArchiveFormat
    {
        public bool IsHeader(FileReader reader) => EncrFile.IsHeader(reader);
        public BlockArchive Open(FileReader reader, string path) => new EncrFile(reader, path);
    }
}
