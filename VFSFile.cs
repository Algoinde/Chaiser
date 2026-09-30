using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace Chaiser
{
    /// <summary>
    /// Endfield's VFS container (trimmed from AnimeStudio's VFSFile.cs): a scrambled UnityFS-like
    /// header, an encrypted + LZ4 block info, and blocks that are either stored (0) or encrypted
    /// + LZ4Inv (5). Everything else (lazy node streams, extraction) is BlockArchive's.
    /// </summary>
    public class VFSFile : BlockArchive
    {
        public VfsHeader m_Header;

        public VFSFile(FileReader reader, string path) : base(path)
        {
            var offset = reader.Position;
            reader.Endian = EndianType.BigEndian;

            if (!VFSUtils.IsValidHeader(reader))
            {
                throw new InvalidDataException("Not a VFS file / VFS version mismatch");
            }

            reader.ReadBytes(8);
            m_Header = VFSUtils.ReadHeader(reader);

            uint blockInfosOffset;
            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
                blockInfosOffset = (uint)m_Header.size - m_Header.compressedBlocksInfoSize;
            else
                blockInfosOffset = m_Header.encFlags >= 7 ? 48u : 40u;

            reader.Position = offset + blockInfosOffset;
            var (blocks, nodes) = ReadBlocksInfoAndDirectory(reader);

            uint dataOffset = m_Header.encFlags >= 7 ? 48u : 40u;
            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) == 0)
            {
                var temp = m_Header.compressedBlocksInfoSize;
                if ((m_Header.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
                    temp = (temp + 15) & 0xFFFFFFF0;
                dataOffset += temp;
            }

            Init(blocks, nodes, offset + dataOffset);
        }

        private (List<VfsStorageBlock>, List<VfsNode>) ReadBlocksInfoAndDirectory(FileReader reader)
        {
            byte[] blocksInfoBytes = reader.ReadBytes((int)m_Header.compressedBlocksInfoSize);

            MemoryStream blocksInfoUncompressedStream;
            if (((int)m_Header.flags & 0x3F) != 0)
            {
                VFSUtils.DecryptBlock(blocksInfoBytes);

                var uncompressedSize = m_Header.uncompressedBlocksInfoSize;
                var uncompressedBytes = ArrayPool<byte>.Shared.Rent((int)uncompressedSize);
                try
                {
                    var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, (int)uncompressedSize);
                    var numWrite = LZ4.Instance.Decompress(blocksInfoBytes, uncompressedBytesSpan);
                    if (numWrite != uncompressedSize)
                    {
                        throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                    }
                    blocksInfoUncompressedStream = new MemoryStream(uncompressedBytesSpan.ToArray());
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(uncompressedBytes);
                }
            }
            else
            {
                blocksInfoUncompressedStream = new MemoryStream(blocksInfoBytes);
            }

            using var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompressedStream);
            blocksInfoReader.Endian = EndianType.BigEndian;
            var blocks = VFSUtils.ReadBlocksInfos(blocksInfoReader);
            var directory = VFSUtils.ReadDirectoryInfos(blocksInfoReader);
            return (blocks, directory);
        }

        protected override int DecodeBlock(Span<byte> compressed, Span<byte> output, StorageBlockFlags flags)
        {
            switch ((int)flags)
            {
                case 0:
                    compressed.CopyTo(output);
                    return compressed.Length;
                case 5:
                    VFSUtils.DecryptBlock(compressed);
                    return LZ4Inv.Instance.Decompress(compressed, output);
                default:
                    throw new NotSupportedException($"Unsupported block compression type {(int)flags}");
            }
        }
    }
}
