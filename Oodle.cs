using System.Runtime.InteropServices;

namespace Chaiser
{
    /// <summary>
    /// Oodle (Kraken/Mermaid/...) block decompression through AnimeStudio's native Ooz build
    /// (native/AnimeStudio.Ooz.dll, x64, copied next to the exe by the csproj).
    /// </summary>
    public static class Oodle
    {
        [DllImport("AnimeStudio.Ooz.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int Ooz_Decompress(ref byte compressedBuffer, int compressedBufferSize, ref byte decompressedBuffer, int decompressedBufferSize,
                                         int fuzzSafe, int checkCRC, int verbosity, IntPtr rawBuffer, int rawBufferSize, IntPtr fpCallback,
                                         IntPtr callbackUserData, IntPtr decoderMemory, IntPtr decoderMemorySize, int threadPhase);

        /// <summary>Returns bytes written (negative on a corrupt stream).</summary>
        public static int Decompress(ReadOnlySpan<byte> compressed, Span<byte> output) =>
            Ooz_Decompress(ref MemoryMarshal.GetReference(compressed), compressed.Length, ref MemoryMarshal.GetReference(output), output.Length,
                           1, 0, 0, 0, 0, 0, 0, 0, 0, 3);
    }
}
