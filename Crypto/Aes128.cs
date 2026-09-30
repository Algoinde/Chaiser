namespace Chaiser
{
    /// <summary>
    /// The AES-128 block core, with the two things vendors vary made pluggable:
    ///  - the S-box (Hypergryph's VFS swaps it; the key schedule then runs through it too), and
    ///  - where the round keys come from: expanded here from a key, or shipped pre-expanded in the
    ///    equivalent-inverse-cipher order (Mr0k's "expansion key": reversed, InvMixColumns applied to
    ///    rounds 1-9). The two schedules convert into each other exactly, so either gives both
    ///    directions.
    /// State layout is column-major (state[4 * column + row]), i.e. plain byte order.
    /// </summary>
    public sealed class Aes128
    {
        const int Rounds = 10;
        readonly byte[] m_SBox;
        readonly byte[] m_InvSBox;
        readonly byte[] m_Encrypt; // 11 x 16, round 0 first
        readonly byte[] m_Decrypt; // 11 x 16, equivalent inverse cipher order

        public static readonly byte[] StandardSBox = BuildStandardSBox();

        Aes128(byte[] sbox, byte[] encrypt, byte[] decrypt)
        {
            m_SBox = sbox;
            m_InvSBox = new byte[256];
            for (var i = 0; i < 256; i++) m_InvSBox[sbox[i]] = (byte)i;
            m_Encrypt = encrypt;
            m_Decrypt = decrypt;
        }

        /// <summary>Standard key expansion, through <paramref name="sbox"/> (default: the standard one).</summary>
        public static Aes128 FromKey(ReadOnlySpan<byte> key, byte[] sbox = null)
        {
            sbox ??= StandardSBox;
            var encrypt = Expand(key, sbox);
            return new Aes128(sbox, encrypt, Convert(encrypt, InvMixColumns));
        }

        /// <summary>Pre-expanded round keys in equivalent-inverse-cipher order (176 bytes).</summary>
        public static Aes128 FromDecryptionKeys(byte[] roundKeys, byte[] sbox = null)
        {
            if (roundKeys.Length != 16 * (Rounds + 1)) throw new ArgumentException($"expected {16 * (Rounds + 1)} bytes of round keys, got {roundKeys.Length}");
            return new Aes128(sbox ?? StandardSBox, Convert(roundKeys, MixColumns), roundKeys.ToArray());
        }

        public void EncryptBlock(Span<byte> state)
        {
            var keys = m_Encrypt.AsSpan();
            AddRoundKey(state, keys[..16]);
            for (var r = 1; r < Rounds; r++)
            {
                Sub(state, m_SBox);
                ShiftRows(state);
                MixColumns(state);
                AddRoundKey(state, keys.Slice(r * 16, 16));
            }
            Sub(state, m_SBox);
            ShiftRows(state);
            AddRoundKey(state, keys.Slice(Rounds * 16, 16));
        }

        public void DecryptBlock(Span<byte> state)
        {
            var keys = m_Decrypt.AsSpan();
            AddRoundKey(state, keys[..16]);
            for (var r = 1; r < Rounds; r++)
            {
                Sub(state, m_InvSBox);
                InvShiftRows(state);
                InvMixColumns(state);
                AddRoundKey(state, keys.Slice(r * 16, 16));
            }
            Sub(state, m_InvSBox);
            InvShiftRows(state);
            AddRoundKey(state, keys.Slice(Rounds * 16, 16));
        }

        /// <summary>Encryption <-> equivalent-inverse schedule: reverse the rounds, (Inv)MixColumns the middle ones.</summary>
        static byte[] Convert(byte[] schedule, SpanAction mix)
        {
            var result = new byte[schedule.Length];
            for (var r = 0; r <= Rounds; r++)
            {
                var round = result.AsSpan(r * 16, 16);
                schedule.AsSpan((Rounds - r) * 16, 16).CopyTo(round);
                if (r > 0 && r < Rounds) mix(round);
            }
            return result;
        }

        delegate void SpanAction(Span<byte> state);

        static byte[] Expand(ReadOnlySpan<byte> key, byte[] sbox)
        {
            if (key.Length != 16) throw new ArgumentException("AES-128 needs a 16-byte key");
            byte[] rCon = { 0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36 };
            var words = new byte[16 * (Rounds + 1)];
            key.CopyTo(words);
            Span<byte> word = stackalloc byte[4];
            for (int w = 4, i = 1; w < 4 * (Rounds + 1); w++)
            {
                words.AsSpan((w - 1) * 4, 4).CopyTo(word);
                if (w % 4 == 0)
                {
                    var first = word[0];
                    word[0] = word[1]; word[1] = word[2]; word[2] = word[3]; word[3] = first;
                    for (var k = 0; k < 4; k++) word[k] = sbox[word[k]];
                    word[0] ^= rCon[i++];
                }
                for (var k = 0; k < 4; k++) words[w * 4 + k] = (byte)(word[k] ^ words[(w - 4) * 4 + k]);
            }
            return words;
        }

        static void Sub(Span<byte> s, byte[] box)
        {
            for (var i = 0; i < 16; i++) s[i] = box[s[i]];
        }

        static void AddRoundKey(Span<byte> s, ReadOnlySpan<byte> k)
        {
            for (var i = 0; i < 16; i++) s[i] ^= k[i];
        }

        // Row r rotates left by r (state[4c + r]).
        static void ShiftRows(Span<byte> s)
        {
            (s[1], s[5], s[9], s[13]) = (s[5], s[9], s[13], s[1]);
            (s[2], s[6], s[10], s[14]) = (s[10], s[14], s[2], s[6]);
            (s[3], s[7], s[11], s[15]) = (s[15], s[3], s[7], s[11]);
        }

        static void InvShiftRows(Span<byte> s)
        {
            (s[1], s[5], s[9], s[13]) = (s[13], s[1], s[5], s[9]);
            (s[2], s[6], s[10], s[14]) = (s[10], s[14], s[2], s[6]);
            (s[3], s[7], s[11], s[15]) = (s[7], s[11], s[15], s[3]);
        }

        static byte XTime(byte a) => (byte)((a << 1) ^ ((a & 0x80) != 0 ? 0x1B : 0));

        static byte Mul(byte a, int b)
        {
            byte result = 0;
            for (; b != 0; b >>= 1, a = XTime(a))
                if ((b & 1) != 0) result ^= a;
            return result;
        }

        static void MixColumns(Span<byte> s)
        {
            for (var c = 0; c < 16; c += 4)
            {
                var a0 = s[c]; var a1 = s[c + 1]; var a2 = s[c + 2]; var a3 = s[c + 3];
                var t = (byte)(a0 ^ a1 ^ a2 ^ a3);
                s[c] = (byte)(a0 ^ t ^ XTime((byte)(a0 ^ a1)));
                s[c + 1] = (byte)(a1 ^ t ^ XTime((byte)(a1 ^ a2)));
                s[c + 2] = (byte)(a2 ^ t ^ XTime((byte)(a2 ^ a3)));
                s[c + 3] = (byte)(a3 ^ t ^ XTime((byte)(a3 ^ a0)));
            }
        }

        static void InvMixColumns(Span<byte> s)
        {
            for (var c = 0; c < 16; c += 4)
            {
                var a0 = s[c]; var a1 = s[c + 1]; var a2 = s[c + 2]; var a3 = s[c + 3];
                s[c] = (byte)(Mul(a0, 14) ^ Mul(a1, 11) ^ Mul(a2, 13) ^ Mul(a3, 9));
                s[c + 1] = (byte)(Mul(a1, 14) ^ Mul(a2, 11) ^ Mul(a3, 13) ^ Mul(a0, 9));
                s[c + 2] = (byte)(Mul(a2, 14) ^ Mul(a3, 11) ^ Mul(a0, 13) ^ Mul(a1, 9));
                s[c + 3] = (byte)(Mul(a3, 14) ^ Mul(a0, 11) ^ Mul(a1, 13) ^ Mul(a2, 9));
            }
        }

        /// <summary>FIPS-197 S-box: multiplicative inverse in GF(2^8), then the affine transform.</summary>
        static byte[] BuildStandardSBox()
        {
            var box = new byte[256];
            for (var x = 0; x < 256; x++)
            {
                byte inverse = 0;
                if (x != 0)
                {
                    for (var y = 1; y < 256; y++)
                    {
                        if (Mul((byte)x, y) == 1) { inverse = (byte)y; break; }
                    }
                }
                var b = inverse;
                var s = b ^ Rot(b, 1) ^ Rot(b, 2) ^ Rot(b, 3) ^ Rot(b, 4) ^ 0x63;
                box[x] = (byte)s;
            }
            return box;

            static int Rot(byte v, int n) => ((v << n) | (v >> (8 - n))) & 0xFF;
        }

        /// <summary>Checks against .NET's AES (standard S-box) and the encrypt/decrypt round trip; returns failures.</summary>
        public static int SelfTest()
        {
            var failures = 0;
            var rng = new Random(42);
            for (var n = 0; n < 200; n++)
            {
                var key = new byte[16];
                var block = new byte[16];
                rng.NextBytes(key);
                rng.NextBytes(block);
                using var reference = System.Security.Cryptography.Aes.Create();
                reference.Key = key;
                var expected = reference.EncryptEcb(block, System.Security.Cryptography.PaddingMode.None);
                var mine = block.ToArray();
                var aes = FromKey(key);
                aes.EncryptBlock(mine);
                if (!mine.AsSpan().SequenceEqual(expected)) failures++;
                aes.DecryptBlock(mine);
                if (!mine.AsSpan().SequenceEqual(block)) failures++;
                // A shipped decryption schedule must round-trip the same way.
                var shipped = FromDecryptionKeys(aes.m_Decrypt);
                var again = block.ToArray();
                shipped.EncryptBlock(again);
                if (!again.AsSpan().SequenceEqual(expected)) failures++;
            }
            return failures;
        }
    }
}
