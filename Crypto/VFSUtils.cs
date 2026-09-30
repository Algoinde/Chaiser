using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Chaiser
{
    // Trimmed from AnimeStudio's Crypto/VFSUtils.cs: this repo's original supports both the
    // live ArknightsEndfield client and the CB3 closed-beta client (different descrambling
    // constants/byte order per client). This tool only ever targets the live client, so every
    // CB3 branch has been dropped - the descrambling math itself is unchanged.
    public static class VFSUtils
    {
        public static bool IsValidHeader(FileReader reader)
        {
            int originalPosition = (int)reader.Position;
            EndianType originalEndian = reader.Endian;
            reader.Endian = EndianType.BigEndian;

            var a = reader.ReadUInt32();
            var b = reader.ReadUInt32();

            uint c1 = 4 * (a ^ 0x4A92F0CD) & 0xFFFF0000;
            uint c2 = BitOperations.RotateRight(a ^ 0x4A92F0CD, 14);
            uint c3 = c1 ^ c2 ^ 0xD8B1E637;

            reader.Endian = originalEndian;
            reader.Position = (uint)originalPosition;

            return b == c3;
        }

        public static VfsHeader ReadHeader(FileReader reader)
        {
            var Header = new VfsHeader
            {
                signature = "UnityFS",
                version = 6,
                unityVersion = "5.x.x",
                unityRevision = "2021.3.3f5",
            };

            ushort compressedBlocksInfoSize2 = reader.ReadUInt16();
            uint flags2 = reader.ReadUInt32();
            uint encFlags = reader.ReadUInt32();
            uint size2 = reader.ReadUInt32();
            uint flags1 = reader.ReadUInt32();
            ushort uncompressedBlocksInfoSize1 = reader.ReadUInt16();
            reader.ReadUInt32(); // unknown
            ushort uncompressedBlocksInfoSize2 = reader.ReadUInt16();
            uint size1 = reader.ReadUInt32();
            ushort compressedBlocksInfoSize1 = reader.ReadUInt16();
            reader.ReadByte(); // unknown

            // descramble
            uint compressedBlocksInfoSize = BitConcat((ushort)(compressedBlocksInfoSize1 ^ compressedBlocksInfoSize2 ^ 0xA121), compressedBlocksInfoSize2);
            compressedBlocksInfoSize = BitOperations.RotateRight(compressedBlocksInfoSize, 18) ^ 0xF74324EE;

            uint uncompressedBlocksInfoSize = BitConcat((ushort)(uncompressedBlocksInfoSize1 ^ uncompressedBlocksInfoSize2 ^ 0xA121), uncompressedBlocksInfoSize2);
            uncompressedBlocksInfoSize = BitOperations.RotateRight(uncompressedBlocksInfoSize, 18) ^ 0xF74324EE;

            ulong size = BitConcat(size1 ^ size2 ^ 0xDAD76848, size2);
            size = BitOperations.RotateRight(size, 18) ^ 0xA4F1A11747816520UL;

            encFlags ^= flags2;
            uint flags = flags1 ^ flags2 ^ 0xA7F49310;

            Header.compressedBlocksInfoSize = compressedBlocksInfoSize;
            Header.uncompressedBlocksInfoSize = uncompressedBlocksInfoSize;
            Header.size = (uint)size;
            Header.flags = (ArchiveFlags)flags;
            Header.encFlags = encFlags;

            return Header;
        }

        public static List<VfsStorageBlock> ReadBlocksInfos(EndianBinaryReader reader)
        {
            var originalEndian = reader.Endian;
            reader.Endian = EndianType.LittleEndian;
            uint encCount = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32() ^ 0x8A7BF723);
            reader.Endian = originalEndian;

            var low = (ushort)encCount;
            var high = (ushort)(encCount >> 16);

            var blocksCount = BitConcat((ushort)(low ^ high), low);
            blocksCount = BitOperations.RotateRight(blocksCount, 18) ^ 0x91CE0A4F;

            Logger.Debug($"Blocks Count: {blocksCount}");

            List<VfsStorageBlock> blocks = new();

            for (int i = 0; i < blocksCount; i++)
            {
                ushort a = reader.ReadUInt16();
                ushort b = reader.ReadUInt16();
                ushort c = reader.ReadUInt16();
                ushort encFlags = (ushort)(reader.ReadUInt16() ^ 0x9CD6);
                ushort d = reader.ReadUInt16();

                var a0 = (byte)encFlags;
                var a1 = (byte)(encFlags >> 8);

                ushort flags = BitConcat((byte)(a0 ^ a1), a0);
                flags = (ushort)(c ^ RotateLeft(flags, 14) ^ 0x523F);

                uint uncompressedSize = BitConcat((ushort)(a ^ c ^ 0xA121), c);
                uncompressedSize = BitOperations.RotateRight(uncompressedSize, 18) ^ 0xF74324EE;

                uint compressedSize = BitConcat((ushort)(b ^ d ^ 0xA121), d);
                compressedSize = BitOperations.RotateRight(compressedSize, 18) ^ 0xF74324EE;

                blocks.Add(new VfsStorageBlock
                {
                    flags = (StorageBlockFlags)flags,
                    uncompressedSize = uncompressedSize,
                    compressedSize = compressedSize,
                });

                Logger.Debug($"Block {i} Info: {blocks[i]}");
            }

            return blocks;
        }

        public static List<VfsNode> ReadDirectoryInfos(EndianBinaryReader reader)
        {
            var originalEndian = reader.Endian;
            reader.Endian = EndianType.LittleEndian;
            uint encCount = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32() ^ 0x5DE50A6B);
            reader.Endian = originalEndian;

            var low = (ushort)encCount;
            var high = (ushort)(encCount >> 16);

            var nodesCount = BitConcat((ushort)(low ^ high), low);
            nodesCount = BitOperations.RotateRight(nodesCount, 18) ^ 0xE4C1D9F2;

            Logger.Debug($"Nodes Count: {nodesCount}");

            List<VfsNode> nodes = new();

            for (int i = 0; i < nodesCount; i++)
            {
                uint a = reader.ReadUInt32() ^ 0x8E06A9F8;
                uint b = reader.ReadUInt32();
                uint c = reader.ReadUInt32();
                uint d = reader.ReadUInt32();

                // read name
                var bytes = new List<byte>();
                while (reader.Remaining > 0 && bytes.Count < 64)
                {
                    var bt = reader.ReadByte();
                    if (bt == 0)
                        break;
                    bytes.Add(bt);
                }

                for (int j = 0; j < bytes.Count; j++)
                    bytes[j] ^= (byte)((j ^ 0x97) & 0xFF);

                string name = Encoding.ASCII.GetString(bytes.ToArray());

                uint e = reader.ReadUInt32();

                var a0 = (ushort)a;
                var a1 = (ushort)(a >> 16);

                uint flags = BitConcat((ushort)(a1 ^ a0), a0);
                flags = BitOperations.RotateRight(flags, 18) ^ 0xF13927C4 ^ b;

                ulong offset = BitConcat(d ^ c ^ 0xDAD76848, c);
                offset = BitOperations.RotateLeft(offset, 14) ^ 0xA4F1A11747816520UL;

                ulong size = BitConcat(b ^ e ^ 0xDAD76848, e);
                size = BitOperations.RotateLeft(size, 14) ^ 0xA4F1A11747816520UL;

                nodes.Add(new VfsNode
                {
                    path = name,
                    flags = flags,
                    offset = (long)offset,
                    size = (long)size,
                });

                Logger.Debug($"Node {i} Info: {nodes[i]}");
            }

            return nodes;
        }

        public static void DecryptBlock(Span<byte> buffer)
        {
            VFSAES.InitKeys(CryptoHelper.VFSAESSBox, CryptoHelper.VFSAESKey, CryptoHelper.VFSAESIV, 0xF19AB7752CDD0196UL);

            if (buffer.Length <= 256)
            {
                VFSAES.DecryptInPlace(buffer);
                return;
            }

            // Only 256 bytes sampled across the block are encrypted: `step` bytes from the
            // start of each of the first min(n16, 256) 16-byte chunks.
            var numBlocksFloor = buffer.Length / 16;
            var step = numBlocksFloor > 256 ? 1 : 256 / numBlocksFloor;
            var chunks = Math.Min(numBlocksFloor, 256);
            Span<byte> decBuffer = stackalloc byte[256];
            for (var i = 0; i < chunks; i++)
                buffer.Slice(i * 16, step).CopyTo(decBuffer.Slice(i * step, step));
            VFSAES.DecryptInPlace(decBuffer);
            for (var i = 0; i < chunks; i++)
                decBuffer.Slice(i * step, step).CopyTo(buffer.Slice(i * 16, step));
        }

        /// <summary>New vs reference DecryptBlock on random data; returns mismatches.</summary>
        public static int SelfTest(int iterations)
        {
            var rng = new Random(1234);
            var mismatches = 0;
            foreach (var len in new[] { 1, 15, 16, 17, 100, 255, 256, 257, 300, 4095, 4096, 4097, 5000, 65536, 131072 })
            {
                for (var n = 0; n < iterations; n++)
                {
                    var a = new byte[len];
                    rng.NextBytes(a);
                    var b = a.ToArray();
                    DecryptBlock(a);
                    DecryptBlockReference(b);
                    if (!a.AsSpan().SequenceEqual(b)) mismatches++;
                }
            }
            return mismatches;
        }

        private static void DecryptBlockReference(Span<byte> buffer)
        {
            VFSAESReference.InitKeys(CryptoHelper.VFSAESSBox, CryptoHelper.VFSAESKey, CryptoHelper.VFSAESIV, 0xF19AB7752CDD0196UL);
            if (buffer.Length <= 256)
            {
                var dec = VFSAESReference.Decrypt(buffer.ToArray());
                dec.CopyTo(buffer);
            }
            else
            {
                var numBlocksFloor = buffer.Length / 16;
                var step = (int)(256 / numBlocksFloor);
                if (numBlocksFloor > 256) step = 1;
                Span<byte> decBuffer = new byte[256];
                for (int i = 0; i < Math.Min(numBlocksFloor, 256); i++)
                    buffer.Slice(i * 16, step).CopyTo(decBuffer.Slice((int)(i * step), step));
                decBuffer = VFSAESReference.Decrypt(decBuffer.ToArray());
                for (int i = 0; i < Math.Min(numBlocksFloor, 256); i++)
                    decBuffer.Slice(i * step, step).CopyTo(buffer.Slice((int)(i * 16), step));
            }
        }

        private static ushort BitConcat(byte a, byte b) => (ushort)(((ushort)a << 8) | (ushort)b);
        private static uint BitConcat(ushort a, ushort b) => ((uint)a << 16) | (uint)b;
        private static ulong BitConcat(uint a, uint b) => ((ulong)a << 32) | (ulong)b;
        private static ushort RotateLeft(ushort value, int count) => (ushort)((value << count) | (value >> (16 - count)));
    }

    /// <summary>
    /// Hypergryph's VFS block cipher: AES-128 encryption (Aes128 with their S-box) run as a
    /// keystream, each keystream block deriving the next "IV" through the S-box + XOR key.
    /// Rewritten from the reference port (VFSAESReference) to expand the key once and work in
    /// place on spans: the port re-expanded the key for every 16-byte block and allocated dozens
    /// of arrays per call, which dominated map builds.
    /// </summary>
    public static class VFSAES
    {
        private static byte[] SBox;
        private static byte[] Key;
        private static byte[] IV;
        private static ulong XORKey;
        private static Aes128 Cipher;

        public static void InitKeys(byte[] VFSSBox, byte[] VFSKey, byte[] VFSIV, ulong VFSXORKey)
        {
            if (ReferenceEquals(SBox, VFSSBox) && ReferenceEquals(Key, VFSKey) && ReferenceEquals(IV, VFSIV) && XORKey == VFSXORKey)
                return;
            var cipher = Aes128.FromKey(VFSKey, VFSSBox);
            SBox = VFSSBox;
            Key = VFSKey;
            IV = VFSIV;
            XORKey = VFSXORKey;
            Cipher = cipher;
        }

        public static byte[] Decrypt(byte[] ciphertext)
        {
            var output = ciphertext.ToArray();
            DecryptInPlace(output);
            return output;
        }

        public static void DecryptInPlace(Span<byte> data)
        {
            Span<byte> previous = stackalloc byte[16];
            Span<byte> block = stackalloc byte[16];
            IV.CopyTo(previous);
            for (var offset = 0; offset < data.Length; offset += 16)
            {
                previous.CopyTo(block);
                Cipher.EncryptBlock(block);
                var chunk = data.Slice(offset, Math.Min(16, data.Length - offset));
                for (var i = 0; i < chunk.Length; i++) chunk[i] ^= block[i];

                var count = 0;
                for (var i = 0; i < 16; i++)
                {
                    var shiftSrc = XORKey >> (count & 0x38);
                    var temp = (byte)(block[i] ^ (31 * i) ^ (byte)shiftSrc);
                    count += 8;
                    temp = (byte)(((temp >> 5) | (8 * temp)) & 0xFF);
                    previous[i] = SBox[temp];
                }
            }
        }
    }

    internal static class VFSAESReference
    {
        private static byte[] SBox;
        private static byte[] Key;
        private static byte[] IV;
        private static ulong XORKey;

        public static void InitKeys(byte[] VFSSBox, byte[] VFSKey, byte[] VFSIV, ulong VFSXORKey)
        {
            SBox = VFSSBox;
            Key = VFSKey;
            IV = VFSIV;
            XORKey = VFSXORKey;
        }

        public static byte[] Decrypt(byte[] ciphertext)
        {
            byte[] iv = IV;
            List<byte[]> blocks = new();
            byte[] previous = iv.ToArray();

            foreach (var ct in SplitBlocks(ciphertext))
            {
                byte[] block = EncryptBlock(previous);
                byte[] pt = new byte[ct.Length];
                for (int i = 0; i < ct.Length; i++) pt[i] = (byte)(ct[i] ^ block[i]);
                blocks.Add(pt);

                byte[] nextIv = new byte[16];
                int count = 0;

                for (int i = 0; i < 16; i++)
                {
                    ulong shiftSrc = XORKey >> (count & 0x38);
                    byte temp = (byte)(block[i] ^ (31 * i) ^ (byte)shiftSrc);
                    count += 8;
                    temp = (byte)((((temp >> 5) | (8 * temp)) & 0xFF));
                    temp = SBox[temp];
                    nextIv[i] = temp;
                }
                previous = nextIv;
            }

            return blocks.SelectMany(b => b).ToArray();
        }

        private static byte[] EncryptBlock(byte[] plaintext)
        {
            List<byte[,]> keyMats = ExpandKey();
            int nRounds = 10;
            var state = BytesToMatrix(plaintext);

            AddRoundKey(state, keyMats[0]);

            for (int r = 1; r < nRounds; r++)
            {
                SubBytes(state);
                ShiftRows(state);
                MixColumns(state);
                AddRoundKey(state, keyMats[r]);
            }

            SubBytes(state);
            ShiftRows(state);
            AddRoundKey(state, keyMats[^1]);

            return MatrixToBytes(state);
        }

        private static IEnumerable<byte[]> SplitBlocks(byte[] msg, int blockSize = 16)
        {
            for (int i = 0; i < msg.Length; i += blockSize)
                yield return msg.Skip(i).Take(blockSize).ToArray();
        }

        private static void SubBytes(List<byte[]> s)
        {
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    s[i][j] = SBox[s[i][j]];
        }

        private static void ShiftRows(List<byte[]> s)
        {
            (s[0][1], s[1][1], s[2][1], s[3][1]) = (s[1][1], s[2][1], s[3][1], s[0][1]);
            (s[0][2], s[1][2], s[2][2], s[3][2]) = (s[2][2], s[3][2], s[0][2], s[1][2]);
            (s[0][3], s[1][3], s[2][3], s[3][3]) = (s[3][3], s[0][3], s[1][3], s[2][3]);
        }

        private static void AddRoundKey(List<byte[]> s, byte[,] k)
        {
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    s[i][j] ^= k[i, j];
        }

        private static byte XTime(byte a) => (byte)(((a & 0x80) != 0) ? ((a << 1) ^ 0x1B) & 0xFF : (a << 1));

        private static void MixSingleColumn(byte[] a)
        {
            byte t = (byte)(a[0] ^ a[1] ^ a[2] ^ a[3]);
            byte u = a[0];
            a[0] ^= (byte)(t ^ XTime((byte)(a[0] ^ a[1])));
            a[1] ^= (byte)(t ^ XTime((byte)(a[1] ^ a[2])));
            a[2] ^= (byte)(t ^ XTime((byte)(a[2] ^ a[3])));
            a[3] ^= (byte)(t ^ XTime((byte)(a[3] ^ u)));
        }

        private static void MixColumns(List<byte[]> s)
        {
            for (int i = 0; i < 4; i++)
                MixSingleColumn(s[i]);
        }

        private static List<byte[,]> ExpandKey()
        {
            int nRounds = 10;
            byte[] masterKey = Key;
            List<byte[]> keyCols = BytesToMatrix(masterKey);
            int iterationSize = masterKey.Length / 4;
            int i = 1;
            byte[] rCon = new byte[] { 0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36, 0x6C, 0xD8, 0xAB, 0x4D, 0x9A, 0x2F, 0x5E, 0xBC, 0x63, 0xC6, 0x97, 0x35, 0x6A, 0xD4, 0xB3, 0x7D, 0xFA, 0xEF, 0xC5, 0x91, 0x39 };

            while (keyCols.Count < (nRounds + 1) * 4)
            {
                byte[] word = keyCols[^1].ToArray();

                if (keyCols.Count % iterationSize == 0)
                {
                    var first = word[0];
                    Array.Copy(word, 1, word, 0, word.Length - 1);
                    word[^1] = first;

                    for (int k = 0; k < 4; k++) word[k] = SBox[word[k]];

                    word[0] ^= rCon[i];
                    i++;
                }
                else if (masterKey.Length == 32 && keyCols.Count % iterationSize == 4)
                {
                    for (int k = 0; k < 4; k++) word[k] = SBox[word[k]];
                }

                byte[] prev = keyCols[keyCols.Count - iterationSize];
                for (int k = 0; k < 4; k++) word[k] ^= prev[k];

                keyCols.Add(word);
            }

            var res = new List<byte[,]>();

            for (int x = 0; x < keyCols.Count / 4; x++)
            {
                byte[,] m = new byte[4, 4];

                for (int c = 0; c < 4; c++)
                    for (int r = 0; r < 4; r++)
                        m[c, r] = keyCols[x * 4 + c][r];

                res.Add(m);
            }
            return res;
        }

        private static List<byte[]> BytesToMatrix(byte[] text)
        {
            var rows = new List<byte[]>();
            for (int i = 0; i < text.Length; i += 4)
                rows.Add(new byte[] { text[i], text[i + 1], text[i + 2], text[i + 3] });
            return rows;
        }

        private static byte[] MatrixToBytes(List<byte[]> m)
        {
            byte[] r = new byte[16];
            int idx = 0;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    r[idx++] = m[i][j];
            return r;
        }
    }

    internal static class CryptoHelper
    {
        public static readonly byte[] VFSAESSBox = new byte[] { 0xE4, 0xB9, 0x45, 0x07, 0x92, 0x82, 0x2F, 0x43, 0xF5, 0xC9, 0x22, 0x25, 0xA9, 0x4F, 0x46, 0x6D, 0x4A, 0x71, 0x8B, 0x6C, 0x8C, 0xEB, 0xB2, 0xAC, 0xCF, 0x0C, 0x9E, 0x01, 0x38, 0x32, 0xD3, 0x93, 0x98, 0x63, 0xDA, 0x96, 0xE5, 0xC4, 0xC3, 0x6B, 0x7F, 0x26, 0x72, 0xD7, 0x97, 0xD5, 0x80, 0xBC, 0x5D, 0xBB, 0x55, 0x67, 0x10, 0x73, 0xB3, 0x8D, 0xE2, 0x35, 0x29, 0x47, 0xA8, 0x60, 0x3F, 0xC5, 0xEF, 0x68, 0xEC, 0xBE, 0xAB, 0xC6, 0xB8, 0x5C, 0xD8, 0x15, 0x09, 0x54, 0xF3, 0x7A, 0x40, 0xA2, 0x30, 0x0A, 0xDC, 0x53, 0xFA, 0xDB, 0xF1, 0x78, 0xDE, 0xAD, 0xF0, 0xB5, 0xC1, 0x81, 0x9F, 0x3E, 0x83, 0x90, 0x31, 0xF2, 0xFB, 0x21, 0x28, 0x85, 0x06, 0xCA, 0xCD, 0x1E, 0xD4, 0x3C, 0xA0, 0xC8, 0x23, 0x16, 0x6E, 0x89, 0x1D, 0xE7, 0xEE, 0x5E, 0x42, 0xBD, 0xCB, 0x13, 0x50, 0xA6, 0x4E, 0x49, 0x58, 0xDF, 0x2C, 0x84, 0x87, 0xB6, 0x91, 0x52, 0xDD, 0x19, 0xF9, 0x2B, 0x4D, 0x77, 0xBA, 0x04, 0xA5, 0x41, 0xCE, 0x94, 0x3D, 0x5F, 0xFC, 0x9B, 0x79, 0x9A, 0x7E, 0x65, 0x5A, 0xB1, 0x66, 0x34, 0x56, 0xA7, 0x1A, 0xBF, 0xEA, 0x7D, 0x27, 0x0B, 0x59, 0x2E, 0xAE, 0x14, 0x33, 0xC0, 0x51, 0x39, 0xC7, 0x3A, 0x2A, 0x9D, 0xF4, 0x7C, 0xCC, 0xD1, 0xD6, 0x70, 0x37, 0x0E, 0x75, 0x02, 0x1B, 0xE3, 0xE9, 0x48, 0x0D, 0x24, 0x2D, 0xF7, 0xD2, 0xB7, 0xAF, 0xA3, 0xA1, 0x64, 0x7B, 0xED, 0xF8, 0x05, 0x95, 0x3B, 0x74, 0xFD, 0x62, 0xD0, 0x0F, 0xFF, 0x4B, 0xAA, 0x88, 0x5B, 0x03, 0xB4, 0xE8, 0x9C, 0xB0, 0x17, 0x1C, 0x76, 0x57, 0xE0, 0xA4, 0x44, 0x20, 0xD9, 0x8E, 0x11, 0x86, 0x69, 0x36, 0xFE, 0x4C, 0x6F, 0x61, 0x6A, 0x8F, 0xE1, 0x18, 0x8A, 0x12, 0x99, 0xE6, 0x1F, 0x00, 0x08, 0xF6, 0xC2 };
        public static readonly byte[] VFSAESKey = new byte[] { 0x3A, 0xF1, 0x8C, 0x47, 0xB2, 0x09, 0x6D, 0xEE, 0x51, 0x24, 0x90, 0x7C, 0x18, 0xD3, 0xA4, 0x62 };
        public static readonly byte[] VFSAESIV = new byte[] { 0xC7, 0x12, 0x5E, 0xA9, 0x04, 0xDB, 0x33, 0x88, 0xF2, 0x0E, 0x77, 0x49, 0x65, 0xBA, 0x1C, 0x93 };
    }
}
