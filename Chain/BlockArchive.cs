using System.Buffers;
using System.Diagnostics;

namespace Chaiser
{
    /// <summary>
    /// The shared shape of Unity-style archives: a block table (compressed blocks laid out after
    /// DataOffset) plus a directory of nodes (byte ranges in the concatenated uncompressed
    /// blocks). Formats differ only in how the header is parsed and how one block is decoded, so a
    /// format subclass parses its header, calls Init, and implements DecodeBlock.
    ///
    /// Nothing is inflated up front: OpenNodeLazy decodes blocks as they are read, ExtractNode
    /// decodes just the blocks covering one node.
    /// </summary>
    public abstract class BlockArchive
    {
        List<VfsStorageBlock> m_Blocks;
        List<VfsNode> m_Nodes;
        long[] m_UncompressedStarts;
        long[] m_CompressedOffsets;

        protected BlockArchive(string path) => Path = path;

        public string Path { get; }
        public IReadOnlyList<VfsNode> Nodes => m_Nodes;
        public IReadOnlyList<VfsStorageBlock> Blocks => m_Blocks;

        /// <summary>
        /// Where the next archive starts in the same physical file (a .chk is several VFS
        /// containers back to back, as AnimeStudio's OffsetStream.GetOffsets confirms).
        /// </summary>
        public long NextContainerOffset { get; private set; }

        protected void Init(List<VfsStorageBlock> blocks, List<VfsNode> nodes, long dataOffset)
        {
            m_Blocks = blocks;
            m_Nodes = nodes;
            m_UncompressedStarts = new long[blocks.Count + 1];
            m_CompressedOffsets = new long[blocks.Count];
            long u = 0, c = dataOffset;
            for (var i = 0; i < blocks.Count; i++)
            {
                m_UncompressedStarts[i] = u;
                m_CompressedOffsets[i] = c;
                u += blocks[i].uncompressedSize;
                c += blocks[i].compressedSize;
            }
            m_UncompressedStarts[blocks.Count] = u;
            NextContainerOffset = c;
        }

        /// <summary>
        /// Decodes one block. <paramref name="compressed"/> may be modified (decrypted in place);
        /// <paramref name="output"/> is exactly the block's uncompressed size. Returns bytes written.
        /// </summary>
        protected abstract int DecodeBlock(Span<byte> compressed, Span<byte> output, StorageBlockFlags flags);

        const int SafeSpace = 64;

        // Profiling, summed across threads.
        public static long TicksInflate, BlocksInflated, BytesInflated;

        /// <summary>Reads and decodes block <paramref name="index"/> into a pooled buffer (caller returns it).</summary>
        byte[] ReadBlock(FileStream file, int index)
        {
            var started = Stopwatch.GetTimestamp();
            var info = m_Blocks[index];
            // Pooled: map builds inflate ~17 GB of blocks per run; fresh arrays for each one kept
            // every thread waiting on the GC.
            var compressedLength = (int)info.compressedSize;
            var uncompressedLength = (int)info.uncompressedSize;
            // + SafeSpace: Oodle decoders read and write a little past both ends of their buffers.
            var compressed = ArrayPool<byte>.Shared.Rent(compressedLength + SafeSpace);
            var data = ArrayPool<byte>.Shared.Rent(uncompressedLength + SafeSpace);
            try
            {
                file.Position = m_CompressedOffsets[index];
                file.ReadExactly(compressed, 0, compressedLength);
                var written = DecodeBlock(compressed.AsSpan(0, compressedLength), data.AsSpan(0, uncompressedLength), info.flags);
                if (written != uncompressedLength)
                    throw new IOException($"Block {index} decoded to {written} of {uncompressedLength} bytes");
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(data);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }
            Interlocked.Add(ref TicksInflate, Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref BlocksInflated);
            Interlocked.Add(ref BytesInflated, uncompressedLength);
            return data;
        }

        /// <summary>
        /// Random-access view of one node that decodes blocks only when read (keeping the last
        /// few). Parsing a SerializedFile's metadata plus the first bytes of each object touches a
        /// handful of blocks instead of the whole node. <paramref name="shared"/>: the caller's
        /// handle on this file (not disposed here), so scanning ~250k nodes doesn't open ~250k
        /// handles.
        /// </summary>
        public Stream OpenNodeLazy(VfsNode node, FileStream shared = null) => new LazyNodeStream(this, node, shared);

        /// <summary>One node fully decoded into memory.</summary>
        public Stream ExtractNode(VfsNode node)
        {
            var output = new MemoryStream((int)node.size);
            using (var lazy = OpenNodeLazy(node))
            {
                lazy.CopyTo(output);
            }
            output.Position = 0;
            return output;
        }

        sealed class LazyNodeStream : Stream
        {
            const int CacheSize = 4;
            readonly BlockArchive m_Archive;
            readonly FileStream m_File;
            readonly bool m_OwnsFile;
            readonly long m_NodeStart;
            readonly long m_Length;
            readonly LinkedList<(int Index, byte[] Data)> m_Cache = new();
            long m_Position;

            public LazyNodeStream(BlockArchive archive, VfsNode node, FileStream shared)
            {
                m_Archive = archive;
                m_OwnsFile = shared == null;
                m_File = shared ?? new FileStream(archive.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
                m_NodeStart = node.offset;
                m_Length = node.size;
            }

            byte[] Block(int index)
            {
                for (var n = m_Cache.First; n != null; n = n.Next)
                {
                    if (n.Value.Index != index) continue;
                    if (n != m_Cache.First) { m_Cache.Remove(n); m_Cache.AddFirst(n); }
                    return n.Value.Data;
                }

                var data = m_Archive.ReadBlock(m_File, index);
                m_Cache.AddFirst((index, data));
                if (m_Cache.Count > CacheSize)
                {
                    ArrayPool<byte>.Shared.Return(m_Cache.Last!.Value.Data);
                    m_Cache.RemoveLast();
                }
                return data;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var starts = m_Archive.m_UncompressedStarts;
                var total = 0;
                while (count > 0 && m_Position < m_Length)
                {
                    var absolute = m_NodeStart + m_Position;
                    var index = Array.BinarySearch(starts, absolute);
                    if (index < 0) index = ~index - 1;
                    var block = Block(index);
                    var inBlock = (int)(absolute - starts[index]);
                    var blockLength = (int)m_Archive.m_Blocks[index].uncompressedSize; // rented arrays can be longer
                    var n = (int)Math.Min(Math.Min(count, blockLength - inBlock), m_Length - m_Position);
                    Buffer.BlockCopy(block, inBlock, buffer, offset, n);
                    m_Position += n; offset += n; count -= n; total += n;
                }
                return total;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                m_Position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => m_Position + offset,
                    _ => m_Length + offset,
                };
                return m_Position;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => m_Length;
            public override long Position { get => m_Position; set => m_Position = value; }
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    foreach (var (_, data) in m_Cache) ArrayPool<byte>.Shared.Return(data);
                    m_Cache.Clear();
                    if (m_OwnsFile) m_File.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
