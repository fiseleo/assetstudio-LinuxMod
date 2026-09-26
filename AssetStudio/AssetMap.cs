using MessagePack;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AssetStudio
{
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
        /// <summary>The Addressables address (primary key) of the asset; null when no catalog locates it (older maps too).</summary>
        [Key(5)]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Address { get; set; }

        public bool Matches(Dictionary<string, Regex> filters)
        {
            var matches = new List<bool>();
            foreach(var filter in filters)
            {
                matches.Add(filter.Key switch
                {
                    string value when value.Equals(nameof(Name), StringComparison.OrdinalIgnoreCase) => filter.Value.IsMatch(Name),
                    string value when value.Equals(nameof(Container), StringComparison.OrdinalIgnoreCase) => filter.Value.IsMatch(Container),
                    string value when value.Equals(nameof(Source), StringComparison.OrdinalIgnoreCase) => filter.Value.IsMatch(Source),
                    string value when value.Equals(nameof(PathID), StringComparison.OrdinalIgnoreCase) => filter.Value.IsMatch(PathID.ToString()),
                    string value when value.Equals(nameof (Type), StringComparison.OrdinalIgnoreCase) => filter.Value.IsMatch(Type.ToString()),
                    string value when value.Equals(nameof(Address), StringComparison.OrdinalIgnoreCase) => filter.Value.IsMatch(Address ?? string.Empty),
                    _ => throw new NotImplementedException()
                });
            }
            return matches.Count(x => x == true) == filters.Count;
        }
    }
}
