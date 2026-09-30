using SkiaSharp;
using Texture2DDecoder;

namespace Chaiser
{
    /// <summary>
    /// Minimal texture decoder covering only the formats actually seen on Endfield factory
    /// building materials (BC7 diffuse/normal/MRO maps) plus a couple of trivial uncompressed
    /// fallbacks, rather than porting AnimeStudio's full ~25-format Texture2DConverter.
    /// </summary>
    public static class TextureExporter
    {
        public static bool TryDecodeToRgba(Texture2D texture, byte[] rawImageBytes, out byte[] rgba)
        {
            var outputSize = texture.m_Width * texture.m_Height * 4;
            rgba = null;
            // The native decoders read width x height worth of input unchecked: short input (a
            // misparsed object) would crash the process instead of failing this texture.
            if (texture.m_Width <= 0 || texture.m_Height <= 0 || rawImageBytes.Length < InputSize(texture)) return false;
            rgba = new byte[outputSize];

            switch (texture.m_TextureFormat)
            {
                case TextureFormat.BC7:
                    return TextureDecoder.DecodeBC7(rawImageBytes, texture.m_Width, texture.m_Height, rgba);
                case TextureFormat.BC5:
                    return TextureDecoder.DecodeBC5(rawImageBytes, texture.m_Width, texture.m_Height, rgba);
                case TextureFormat.BC4:
                    return TextureDecoder.DecodeBC4(rawImageBytes, texture.m_Width, texture.m_Height, rgba);
                case TextureFormat.DXT5:
                    return TextureDecoder.DecodeDXT5(rawImageBytes, texture.m_Width, texture.m_Height, rgba);
                case TextureFormat.DXT1:
                    return TextureDecoder.DecodeDXT1(rawImageBytes, texture.m_Width, texture.m_Height, rgba);
                case TextureFormat.RGBA32:
                    for (var i = 0; i < outputSize; i += 4)
                    {
                        // BGRA -> RGBA (matches AnimeStudio's DecodeRGBA32 byte order).
                        rgba[i] = rawImageBytes[i + 2];
                        rgba[i + 1] = rawImageBytes[i + 1];
                        rgba[i + 2] = rawImageBytes[i];
                        rgba[i + 3] = rawImageBytes[i + 3];
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Bytes the top mip level needs in this format (0 if not decodable here).</summary>
        static long InputSize(Texture2D texture)
        {
            long blocks = (long)((texture.m_Width + 3) / 4) * ((texture.m_Height + 3) / 4);
            return texture.m_TextureFormat switch
            {
                TextureFormat.BC7 or TextureFormat.BC5 or TextureFormat.DXT5 => blocks * 16,
                TextureFormat.BC4 or TextureFormat.DXT1 => blocks * 8,
                TextureFormat.RGBA32 => (long)texture.m_Width * texture.m_Height * 4,
                _ => 0,
            };
        }

        public static void WritePng(byte[] rgba, int width, int height, string outPath, bool flipVertically = true)
        {
            using var file = File.Create(outPath);
            WritePngToStream(rgba, width, height, file, flipVertically);
        }

        public static void WritePngToStream(byte[] rgba, int width, int height, Stream outStream, bool flipVertically = true)
        {
            // The Texture2DDecoder BC7/BC5/BC4/DXT decoders (and our own manual RGBA32 branch
            // above) all emit bytes in BGRA order - confirmed against AnimeStudio's own
            // ImageExtensions.CreateBitmapFromBgra, which wraps the same decoder output as
            // SKColorType.Bgra8888. Tagging this buffer as Rgba8888 instead silently swaps the
            // R and B channels (e.g. a yellow accent renders cyan).
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var pixelSpan = bitmap.GetPixelSpan();
            if (!flipVertically)
            {
                rgba.AsSpan().CopyTo(pixelSpan);
            }
            else
            {
                // Unity textures are stored bottom-to-top; flip rows so the PNG reads top-to-bottom.
                var stride = width * 4;
                for (var row = 0; row < height; row++)
                {
                    var srcRow = rgba.AsSpan((height - 1 - row) * stride, stride);
                    srcRow.CopyTo(pixelSpan.Slice(row * stride, stride));
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            data.SaveTo(outStream);
        }
    }
}
