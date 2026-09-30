namespace Chaiser
{
    /// <summary>
    /// Trimmed from AnimeStudio's Classes/PPtr.cs. The original resolves cross-file references
    /// automatically against a live AssetsManager.assetsFileList (every dependency file already
    /// loaded and held in memory) - the exact pattern this tool exists to avoid. Here a PPtr is
    /// just the raw (m_FileID, m_PathID) pair plus, if m_FileID != 0, the external CAB filename
    /// it points at (resolved from the owning SerializedFile's m_Externals) - callers use
    /// ExternalFileName with CabMap + on-demand extraction to resolve it themselves, one CAB at
    /// a time, only when actually needed.
    /// </summary>
    public sealed class PPtr<T> where T : Object
    {
        public int m_FileID;
        public long m_PathID;
        public string ExternalFileName;

        public PPtr(ObjectReader reader)
        {
            m_FileID = reader.ReadInt32();
            m_PathID = reader.m_Version < SerializedFileFormatVersion.Unknown_14 ? reader.ReadInt32() : reader.ReadInt64();
            if (m_FileID > 0 && m_FileID - 1 < reader.assetsFile.m_Externals.Count)
            {
                ExternalFileName = reader.assetsFile.m_Externals[m_FileID - 1].fileName;
            }
        }

        public bool IsNull => m_PathID == 0 || m_FileID < 0;
    }
}
