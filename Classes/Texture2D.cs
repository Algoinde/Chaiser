namespace Chaiser
{
    // Trimmed from AnimeStudio's Classes/Texture2D.cs: dropped every reader.Game.Type.IsXxx()
    // branch not gated on ArknightsEndfield(CB3). ResourceReader (the original's abstraction
    // for "pixel bytes may live in a companion .resS file on disk") has been replaced with a
    // plain record: image_data now just records where the bytes are (either inline in this
    // object's own byte range, or in a named .resS-style companion node) and the caller
    // resolves it against whichever VFS container node holds that data - this tool never
    // assumes resource streams are real files on disk, since here they are also just VFS nodes.
    public class StreamingInfo
    {
        public long offset;
        public uint size;
        public string path;

        public StreamingInfo(ObjectReader reader)
        {
            var version = reader.version;

            if (version[0] >= 2020) //2020.1 and up
            {
                offset = reader.ReadInt64();
            }
            else
            {
                offset = reader.ReadUInt32();
            }
            size = reader.ReadUInt32();
            path = reader.ReadAlignedString();
        }
    }

    public class GLTextureSettings
    {
        public int m_FilterMode;
        public int m_Aniso;
        public float m_MipBias;
        public int m_WrapMode;

        public GLTextureSettings(ObjectReader reader)
        {
            var version = reader.version;

            m_FilterMode = reader.ReadInt32();
            m_Aniso = reader.ReadInt32();
            m_MipBias = reader.ReadSingle();
            if (version[0] >= 2017)//2017.x and up
            {
                m_WrapMode = reader.ReadInt32(); //m_WrapU
                int m_WrapV = reader.ReadInt32();
                int m_WrapW = reader.ReadInt32();
            }
            else
            {
                m_WrapMode = reader.ReadInt32();
            }
            // Endfield's own field (IsArknightsEndfieldCB3/IsArknightsEndfield in the original).
            if (reader.dialect.Game == "ef")
            {
                var m_TextureGroup = reader.ReadUInt32();
            }
        }
    }

    public record ImageDataLocation(string ExternalPath, long Offset, long Size, bool IsInline);

    public sealed class Texture2D : Texture
    {
        public int m_Width;
        public int m_Height;
        public TextureFormat m_TextureFormat;
        public bool m_MipMap;
        public int m_MipCount;
        public GLTextureSettings m_TextureSettings;
        public ImageDataLocation image_data;
        public StreamingInfo m_StreamData;

        public Texture2D(ObjectReader reader) : base(reader)
        {
            m_Width = reader.ReadInt32();
            m_Height = reader.ReadInt32();
            var m_CompleteImageSize = reader.ReadInt32();
            if (version[0] >= 2020) //2020.1 and up
            {
                var m_MipsStripped = reader.ReadInt32();
            }
            m_TextureFormat = (TextureFormat)reader.ReadInt32();

            if (version[0] < 5 || (version[0] == 5 && version[1] < 2)) //5.2 down
            {
                m_MipMap = reader.ReadBoolean();
            }
            else
            {
                m_MipCount = reader.ReadInt32();
            }
            if (version[0] > 2 || (version[0] == 2 && version[1] >= 6)) //2.6.0 and up
            {
                var m_IsReadable = reader.ReadBoolean();
            }
            if (version[0] >= 2020) //2020.1 and up
            {
                var m_IsPreProcessed = reader.ReadBoolean();
            }
            if (version[0] > 2019 || (version[0] == 2019 && version[1] >= 3)) //2019.3 and up
            {
                var m_IgnoreMasterTextureLimit = reader.ReadBoolean();
            }
            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 2)) //2022.2 and up
            {
                reader.AlignStream(); //m_IgnoreMipmapLimit
                var m_MipmapLimitGroupName = reader.ReadAlignedString();
            }
            if (version[0] >= 3) //3.0.0 - 5.4
            {
                if (version[0] < 5 || (version[0] == 5 && version[1] <= 4))
                {
                    var m_ReadAllowed = reader.ReadBoolean();
                }
            }
            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 2)) //2018.2 and up
            {
                var m_StreamingMipmaps = reader.ReadBoolean();
            }
            reader.AlignStream();
            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 2)) //2018.2 and up
            {
                var m_StreamingMipmapsPriority = reader.ReadInt32();
            }
            var m_ImageCount = reader.ReadInt32();
            var m_TextureDimension = reader.ReadInt32();
            m_TextureSettings = new GLTextureSettings(reader);
            if (version[0] >= 3) //3.0 and up
            {
                var m_LightmapFormat = reader.ReadInt32();
            }
            if (version[0] > 3 || (version[0] == 3 && version[1] >= 5)) //3.5.0 and up
            {
                var m_ColorSpace = reader.ReadInt32();
            }
            if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) //2020.2 and up
            {
                var m_PlatformBlob = reader.ReadUInt8Array();
                reader.AlignStream();
            }
            var image_data_size = reader.ReadInt32();
            if (image_data_size == 0 && ((version[0] == 5 && version[1] >= 3) || version[0] > 5))//5.3.0 and up
            {
                m_StreamData = new StreamingInfo(reader);
            }

            if (!string.IsNullOrEmpty(m_StreamData?.path))
            {
                image_data = new ImageDataLocation(m_StreamData.path, m_StreamData.offset, m_StreamData.size, IsInline: false);
            }
            else
            {
                image_data = new ImageDataLocation(null, reader.Position, image_data_size, IsInline: true);
            }
        }
    }

    public enum TextureFormat
    {
        Alpha8 = 1,
        ARGB4444 = 2,
        RGB24 = 3,
        RGBA32 = 4,
        ARGB32 = 5,
        RGB565 = 7,
        R16_Alt = 8,
        R16 = 9,
        DXT1 = 10,
        DXT3 = 11,
        DXT5 = 12,
        RGBA4444 = 13,
        BGRA32 = 14,
        RHalf = 15,
        RGHalf = 16,
        RGBAHalf = 17,
        RFloat = 18,
        RGFloat = 19,
        RGBAFloat = 20,
        YUY2 = 21,
        RGB9e5Float = 22,
        BC4 = 26,
        BC5 = 27,
        BC6H = 24,
        BC7 = 25,
        DXT1Crunched = 28,
        DXT5Crunched = 29,
        PVRTC_RGB2 = 30,
        PVRTC_RGBA2 = 31,
        PVRTC_RGB4 = 32,
        PVRTC_RGBA4 = 33,
        ETC_RGB4 = 34,
        ATC_RGB4 = 35,
        ATC_RGBA8 = 36,
        EAC_R = 41,
        EAC_R_SIGNED = 42,
        EAC_RG = 43,
        EAC_RG_SIGNED = 44,
        ETC2_RGB = 45,
        ETC2_RGBA1 = 46,
        ETC2_RGBA8 = 47,
        ASTC_4x4 = 48,
        ASTC_RGB_4x4 = -48,
        ASTC_5x5 = 49,
        ASTC_RGB_5x5 = -49,
        ASTC_6x6 = 50,
        ASTC_RGB_6x6 = -50,
        ASTC_8x8 = 51,
        ASTC_RGB_8x8 = -51,
        ASTC_10x10 = 52,
        ASTC_RGB_10x10 = -52,
        ASTC_12x12 = 53,
        ASTC_RGB_12x12 = -53,
        ASTC_RGBA_4x4 = 54,
        ASTC_RGBA_5x5 = 55,
        ASTC_RGBA_6x6 = 56,
        ASTC_RGBA_8x8 = 57,
        ASTC_RGBA_10x10 = 58,
        ASTC_RGBA_12x12 = 59,
        ETC_RGB4_3DS = 60,
        ETC_RGBA8_3DS = 61,
        RG16 = 62,
        R8 = 63,
        ETC_RGB4Crunched = 64,
        ETC2_RGBA8Crunched = 65,
        ASTC_HDR_4x4 = 66,
        ASTC_HDR_5x5 = 67,
        ASTC_HDR_6x6 = 68,
        ASTC_HDR_8x8 = 69,
        ASTC_HDR_10x10 = 70,
        ASTC_HDR_12x12 = 71,
        RG32 = 72,
        RGB48 = 73,
        RGBA64 = 74,
        R8_SIGNED = 75,
        RG16_SIGNED = 76,
        RGB24_SIGNED = 77,
        RGBA32_SIGNED = 78,
        R16_SIGNED = 79,
        RG32_SIGNED = 80,
        RGB48_SIGNED = 81,
        RGBA64_SIGNED = 82,
    }
}
