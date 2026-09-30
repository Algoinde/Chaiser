using System.Collections.Generic;
using System.IO;

namespace Chaiser
{
    /// <summary>
    /// The single reusable "given a CAB name (or a PPtr's external file name), find and
    /// decompress the VFS container that holds it, then locate a specific object inside" flow.
    /// Every resolution goes through the CABMap for (file, offset) - never a scan - and each
    /// call decompresses only the one VFS container + node actually needed, then lets it be
    /// garbage collected. This is the core mechanism that keeps this tool's memory bounded to
    /// "the handful of objects currently being processed" instead of "everything ever touched".
    /// </summary>
    public class AssetResolver
    {
        private readonly Dictionary<string, CabMapEntry> _cabMap;
        private readonly string _vfsRoot;

        public AssetResolver(string cabMapPath, string vfsRoot)
        {
            // CABMap paths are relative to the folder it was built over (stored in the file), so
            // a map built over all of Endfield_Data (hotfixes in Persistent too) resolves as-is.
            _cabMap = CabMap.Load(cabMapPath, out var baseFolder);
            _vfsRoot = Directory.Exists(baseFolder) ? baseFolder : vfsRoot;
        }

        /// <summary>
        /// Resolves a CAB name to its decompressed SerializedFile + the FileReader backing it
        /// (caller must dispose the returned reader once done with the SerializedFile/any
        /// ObjectReaders built from it - after that this CAB's bytes are eligible for GC).
        /// </summary>
        public (SerializedFile file, FileReader reader) OpenCab(string cabName)
        {
            if (!_cabMap.TryGetValue(cabName, out var entry))
            {
                throw new FileNotFoundException($"CAB '{cabName}' not found in CABMap.");
            }

            var fullPath = Path.Combine(_vfsRoot, entry.Path);
            using var vfsReader = new FileReader(fullPath);
            vfsReader.Position = entry.Offset;
            var vfs = new VFSFile(vfsReader, fullPath);

            foreach (var node in vfs.Nodes)
            {
                if (node.path != cabName && !node.path.StartsWith(cabName + "."))
                {
                    continue;
                }

                var extracted = vfs.ExtractNode(node);
                var cabReader = new FileReader(node.path, extracted, leaveOpen: true);
                if (cabReader.FileType == FileType.AssetsFile)
                {
                    var sf = new SerializedFile(cabReader);
                    return (sf, cabReader);
                }
                // Resource-only node (e.g. a .resS companion) - no SerializedFile to parse,
                // caller wanted raw bytes instead; see ResolveResourceBytes.
                cabReader.Dispose();
                return (null, null);
            }

            // Node names inside a container don't always exactly equal the CAB name requested
            // (companion .resS nodes share a prefix) - fall back to the first AssetsFile node.
            foreach (var node in vfs.Nodes)
            {
                var extracted = vfs.ExtractNode(node);
                var cabReader = new FileReader(node.path, extracted, leaveOpen: true);
                if (cabReader.FileType == FileType.AssetsFile)
                {
                    var sf = new SerializedFile(cabReader);
                    return (sf, cabReader);
                }
                cabReader.Dispose();
            }

            throw new FileNotFoundException($"CAB '{cabName}' resolved to a container with no AssetsFile node.");
        }

        /// <summary>
        /// Resolves the raw bytes of a StreamingInfo.path resource - Unity encodes these as
        /// "archive:/CAB-xxxx/CAB-xxxx.resS", where the referenced CAB is the SAME container the
        /// owning object lives in (confirmed empirically: the .resS node is a sibling node in
        /// that CAB's VFS container, not a separate external dependency). Offset/size come from
        /// the StreamingInfo itself and index into this resource node's own byte range.
        /// </summary>
        public byte[] ResolveStreamedBytes(string archivePath, long offset, long size)
        {
            var resourceNodeName = ParseArchivePath(archivePath, out var containingCab);

            if (!_cabMap.TryGetValue(containingCab, out var entry))
            {
                throw new FileNotFoundException($"Resource container CAB '{containingCab}' (from '{archivePath}') not found in CABMap.");
            }

            var fullPath = Path.Combine(_vfsRoot, entry.Path);
            using var vfsReader = new FileReader(fullPath);
            vfsReader.Position = entry.Offset;
            var vfs = new VFSFile(vfsReader, fullPath);

            foreach (var node in vfs.Nodes)
            {
                if (node.path == resourceNodeName)
                {
                    using var extracted = vfs.ExtractNode(node);
                    extracted.Position = offset;
                    var buffer = new byte[size];
                    extracted.ReadExactly(buffer, 0, (int)size);
                    return buffer;
                }
            }

            throw new FileNotFoundException($"Resource node '{resourceNodeName}' not found in container '{containingCab}'.");
        }

        private static string ParseArchivePath(string archivePath, out string containingCab)
        {
            // "archive:/CAB-xxxx/CAB-xxxx.resS" -> containingCab="CAB-xxxx", node="CAB-xxxx.resS"
            var trimmed = archivePath.StartsWith("archive:/") ? archivePath.Substring("archive:/".Length) : archivePath;
            var slashIndex = trimmed.IndexOf('/');
            containingCab = slashIndex >= 0 ? trimmed.Substring(0, slashIndex) : trimmed;
            return slashIndex >= 0 ? trimmed.Substring(slashIndex + 1) : trimmed;
        }
    }
}
