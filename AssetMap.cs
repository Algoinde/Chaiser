using System.Collections.Generic;
using MessagePack;

namespace Chaiser
{
    // MessagePack contract copied verbatim from AnimeStudio's AssetMap.cs so ef1.map
    // deserializes correctly - Key ordinals must match exactly. String interning (StringCache)
    // was dropped since this tool only ever loads one map for one lookup, not a long-running
    // process amortizing repeated string allocations across millions of entries.
    [MessagePackObject]
    public record AssetMap
    {
        [Key(0)]
        public GameType GameType { get; set; }

        [Key(1)]
        public List<AssetEntry> AssetEntries { get; set; }
    }

    [MessagePackObject]
    public record AssetEntry
    {
        [Key(0)]
        public string Name { get; set; }

        [Key(1)]
        public string Container { get; set; }

        [Key(2)]
        public string Source { get; set; }

        [Key(3)]
        public long PathID { get; set; }

        [Key(4)]
        public ClassIDType Type { get; set; }

        [Key(5)]
        public string Hash { get; set; }

        [Key(6)]
        public long Offset { get; set; } = -1;
    }
}
