using System.Collections.Generic;
using System.IO;

namespace Chaiser
{
    public record CabMapEntry(string Path, long Offset, List<string> Dependencies);

    /// <summary>
    /// Reads AnimeStudio's CABMap binary format (AssetsHelper.DumpCABMap/ParseCABMap) - maps a
    /// CAB hash name to which file it lives in and its exact byte offset there. Used to resolve
    /// each of a target asset's external dependency CABs directly, without scanning.
    /// </summary>
    public static class CabMap
    {
        public static Dictionary<string, CabMapEntry> Load(string mapPath, out string baseFolder)
        {
            using var fs = File.OpenRead(mapPath);
            using var reader = new BinaryReader(fs);
            baseFolder = reader.ReadString();
            var count = reader.ReadInt32();

            var map = new Dictionary<string, CabMapEntry>(count);
            for (int i = 0; i < count; i++)
            {
                var cab = reader.ReadString();
                var path = reader.ReadString();
                var offset = reader.ReadInt64();
                var depCount = reader.ReadInt32();
                var deps = new List<string>(depCount);
                for (int j = 0; j < depCount; j++)
                {
                    deps.Add(reader.ReadString());
                }
                map[cab] = new CabMapEntry(path, offset, deps);
            }
            return map;
        }
    }
}
