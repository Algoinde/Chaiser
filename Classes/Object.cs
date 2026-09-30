namespace Chaiser
{
    /// <summary>
    /// Trimmed from AnimeStudio's Classes/Object.cs: dropped GetHash/Dump/ToType (asset-map
    /// hashing and generic type-tree dumping - unused by this tool, which reads Mesh/Material/
    /// Texture2D through their typed binary constructors directly).
    /// </summary>
    public class Object
    {
        public SerializedFile assetsFile;
        public ObjectReader reader;
        public long m_PathID;
        public int[] version;
        protected BuildType buildType;
        public BuildTarget platform;
        public ClassIDType type;
        public SerializedType serializedType;
        public uint byteSize;

        public virtual string Name => string.Empty;

        public Object(ObjectReader reader)
        {
            this.reader = reader;
            reader.Reset();
            assetsFile = reader.assetsFile;
            type = reader.type;
            m_PathID = reader.m_PathID;
            version = reader.version;
            buildType = reader.buildType;
            platform = reader.platform;
            serializedType = reader.serializedType;
            byteSize = reader.byteSize;

            if (platform == BuildTarget.NoTarget)
            {
                var m_ObjectHideFlags = reader.ReadUInt32();
            }
        }
    }
}
