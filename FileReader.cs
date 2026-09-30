using System.IO;

namespace Chaiser
{
    public enum FileType
    {
        VFSFile,
        AssetsFile,
        ResourceFile,
        Unknown,
    }

    /// <summary>
    /// Trimmed from AnimeStudio's FileReader.cs: the original probes for ~15 different games'
    /// bespoke container formats (Mhy0/1, Blb2/3, Naraka, Gunfire, HYG, etc.) since it's a
    /// general multi-game tool. Endfield's file layout is always either a top-level VFS (.chk)
    /// container or a plain Unity SerializedFile extracted from one, so only those two checks
    /// (plus a ResourceFile fallback) are needed here.
    /// </summary>
    public class FileReader : EndianBinaryReader
    {
        public string FullPath;
        public string FileName;
        public FileType FileType;

        public FileReader(string path) : this(path, File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }

        public FileReader(string path, Stream stream, bool leaveOpen = false) : base(stream, EndianType.BigEndian, leaveOpen)
        {
            FullPath = Path.GetFullPath(path);
            FileName = Path.GetFileName(path);
            FileType = CheckFileType();
        }

        private FileType CheckFileType()
        {
            if (BaseStream.Length >= 8 && VFSUtils.IsValidHeader(this))
            {
                Position = 0;
                return FileType.VFSFile;
            }

            Position = 0;
            if (IsSerializedFile())
            {
                Position = 0;
                return FileType.AssetsFile;
            }

            Position = 0;
            return FileType.ResourceFile;
        }

        private bool IsSerializedFile()
        {
            var fileSize = BaseStream.Length;
            if (fileSize < 20)
            {
                return false;
            }
            var m_MetadataSize = ReadUInt32();
            long m_FileSize = ReadUInt32();
            var m_Version = ReadUInt32();
            long m_DataOffset = ReadUInt32();
            var m_Endianess = ReadByte();
            var m_Reserved = ReadBytes(3);
            if (m_Version >= 22)
            {
                if (fileSize < 48)
                {
                    return false;
                }
                m_MetadataSize = ReadUInt32();
                m_FileSize = ReadInt64();
                m_DataOffset = ReadInt64();
            }
            if (m_FileSize != fileSize)
            {
                return false;
            }
            if (m_DataOffset > fileSize)
            {
                return false;
            }
            return true;
        }
    }
}
