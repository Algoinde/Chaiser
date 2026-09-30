using System;
using System.IO;

namespace Chaiser
{
    public class ObjectReader : EndianBinaryReader
    {
        public SerializedFile assetsFile;
        public long m_PathID;
        public long byteStart;
        public uint byteSize;
        public ClassIDType type;
        public SerializedType serializedType;
        public BuildTarget platform;
        public SerializedFileFormatVersion m_Version;

        public int[] version => assetsFile.version;
        public BuildType buildType => assetsFile.buildType;
        public Dialect dialect => assetsFile.Dialect;

        public ObjectReader(EndianBinaryReader reader, SerializedFile assetsFile, ObjectInfo objectInfo) : base(reader.BaseStream, reader.Endian)
        {
            this.assetsFile = assetsFile;
            m_PathID = objectInfo.m_PathID;
            byteStart = objectInfo.byteStart;
            byteSize = objectInfo.byteSize;
            if (Enum.IsDefined(typeof(ClassIDType), objectInfo.classID))
            {
                type = (ClassIDType)objectInfo.classID;
            }
            else
            {
                type = ClassIDType.UnknownType;
                Logger.Warning($"Unknown ClassIDType {objectInfo.classID} for object with PathID {m_PathID} in file {assetsFile.fileName}");
            }
            serializedType = objectInfo.serializedType;
            platform = assetsFile.m_TargetPlatform;
            m_Version = assetsFile.header.m_Version;
        }

        public override int Read(byte[] buffer, int index, int count)
        {
            var pos = Position - byteStart;
            if (pos + count > byteSize)
            {
                throw new EndOfStreamException("Unable to read beyond the end of the stream.");
            }
            return base.Read(buffer, index, count);
        }

        public void Reset()
        {
            Position = byteStart;
        }

        public int BytesLeft()
        {
            return (int)(byteSize - (Position - byteStart));
        }

        public Vector3 ReadVector3()
        {
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 4))
            {
                return new Vector3(ReadSingle(), ReadSingle(), ReadSingle());
            }
            else
            {
                return new Vector4(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
            }
        }

        public XForm ReadXForm()
        {
            var t = ReadVector3();
            var q = ReadQuaternion();
            var s = ReadVector3();

            return new XForm(t, q, s);
        }

        public XForm ReadXForm4()
        {
            var t = ReadVector4();
            var q = ReadQuaternion();
            var s = ReadVector4();

            return new XForm(t, q, s);
        }

        public Vector3[] ReadVector3Array(int length = 0)
        {
            if (length == 0)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadVector3, length);
        }

        public XForm[] ReadXFormArray()
        {
            return ReadArray(ReadXForm, ReadInt32());
        }
    }
}
