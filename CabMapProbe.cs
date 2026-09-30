using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Chaiser
{
    public static class CabMapProbe
    {
        public static void Run(string mapPath, string needle)
        {
            using var fs = File.OpenRead(mapPath);
            using var reader = new BinaryReader(fs);
            var baseFolder = reader.ReadString();
            var count = reader.ReadInt32();
            System.Console.WriteLine($"BaseFolder: {baseFolder}, Count: {count}");

            var found = 0;
            for (int i = 0; i < count; i++)
            {
                var cab = reader.ReadString();
                var path = reader.ReadString();
                var offset = reader.ReadInt64();
                var depCount = reader.ReadInt32();
                var deps = new string[depCount];
                for (int j = 0; j < depCount; j++)
                {
                    deps[j] = reader.ReadString();
                }

                if (cab.Contains(needle) || needle.Contains(cab.Replace("CAB-", "")))
                {
                    System.Console.WriteLine($"MATCH: cab={cab} path={path} offset=0x{offset:X}");
                    found++;
                }
            }
            System.Console.WriteLine($"Total matches: {found}");
        }

        public static List<(string cab, long offset)> ListForFile(string mapPath, string fileNeedle)
        {
            using var fs = File.OpenRead(mapPath);
            using var reader = new BinaryReader(fs);
            reader.ReadString();
            var count = reader.ReadInt32();

            var results = new List<(string, long)>();
            for (int i = 0; i < count; i++)
            {
                var cab = reader.ReadString();
                var path = reader.ReadString();
                var offset = reader.ReadInt64();
                var depCount = reader.ReadInt32();
                for (int j = 0; j < depCount; j++)
                {
                    reader.ReadString();
                }

                if (path.Contains(fileNeedle))
                {
                    results.Add((cab, offset));
                }
            }
            return results;
        }
    }
}
