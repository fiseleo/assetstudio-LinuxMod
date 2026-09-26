using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AssetStudio
{
    /// <summary>
    /// An Addressables content catalog: the addresses, labels and bundles of the assets of a build.
    /// Reads the JSON catalogs (catalog.json, Addressables 1.x to 4.x) and the binary ones (catalog.bin, versions 1 to 3).
    /// </summary>
    public sealed class AddressablesCatalog
    {
        public sealed class Location
        {
            public string PrimaryKey;
            public string InternalId;
            public string ProviderId;
            public string ResourceType;
            public List<Location> Dependencies = new List<Location>();
            /// <summary>All keys that locate this location: the address, the GUID, the labels.</summary>
            public List<string> Keys = new List<string>();
            //AssetBundleRequestOptions of bundle locations
            public string BundleName;
            public string BundleHash;
            public uint BundleCrc;
            public long BundleSize;

            public bool IsBundle => ProviderId?.Contains("AssetBundleProvider", StringComparison.Ordinal) == true;
            public bool IsScene => ResourceType?.EndsWith("SceneInstance", StringComparison.Ordinal) == true;

            /// <summary>The file name of a bundle location: the last part of its internal id.</summary>
            public string FileName
            {
                get
                {
                    var id = InternalId ?? string.Empty;
                    var end = id.IndexOfAny(new[] { '?', '#' });
                    if (end >= 0)
                        id = id[..end];
                    return id[(Math.Max(id.LastIndexOf('/'), id.LastIndexOf('\\')) + 1)..];
                }
            }

            /// <summary>The keys that are neither the address nor a GUID nor a hash: the labels.</summary>
            public IEnumerable<string> Labels => Keys.Where(x => x != PrimaryKey && !IsGuid(x) && !int.TryParse(x, out _));

            public override string ToString() => $"{PrimaryKey} ({InternalId})";
        }

        private const uint BinaryMagic = 0x0de38942;

        public string FilePath { get; private set; }
        public string Format { get; private set; }
        public string LocatorId { get; private set; }
        public string BuildResultHash { get; private set; }
        public List<Location> Locations { get; } = new List<Location>();

        private Dictionary<string, List<Location>> assetsByInternalId;
        private Dictionary<string, List<Location>> bundlesByFileName;

        /// <summary>A catalog file name: catalog.json, catalog.bin, catalog_&lt;version&gt;.json...</summary>
        public static bool IsCatalogFileName(string path)
        {
            var name = Path.GetFileName(path);
            return name.StartsWith("catalog", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Reads a catalog; null when the data is not a catalog.</summary>
        public static AddressablesCatalog TryRead(byte[] data, string path)
        {
            try
            {
                var catalog = new AddressablesCatalog { FilePath = path };
                if (data.Length >= 8 && BitConverter.ToUInt32(data, 0) == BinaryMagic)
                {
                    catalog.ReadBinary(data);
                }
                else
                {
                    var text = Encoding.UTF8.GetString(data).TrimStart('﻿', ' ', '\t', '\r', '\n');
                    if (!text.StartsWith('{') || !text.Contains("m_InternalIds", StringComparison.Ordinal))
                        return null;
                    catalog.ReadJson(JObject.Parse(text));
                }
                catalog.BuildIndex();
                return catalog;
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to read the Addressables catalog {path}: {e.Message}");
                return null;
            }
        }

        public static AddressablesCatalog TryLoad(string path) => TryRead(File.ReadAllBytes(path), path);

        private static bool IsGuid(string key) => key.Length == 32 && key.All(Uri.IsHexDigit);

        #region Lookup

        private void BuildIndex()
        {
            assetsByInternalId = new Dictionary<string, List<Location>>(StringComparer.OrdinalIgnoreCase);
            bundlesByFileName = new Dictionary<string, List<Location>>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in Locations)
            {
                var index = location.IsBundle ? bundlesByFileName : assetsByInternalId;
                var id = location.IsBundle ? location.FileName : StripSubObject(location.InternalId);
                if (string.IsNullOrEmpty(id))
                    continue;
                if (!index.TryGetValue(id, out var list))
                    index[id] = list = new List<Location>();
                list.Add(location);
            }
        }

        //"Assets/Atlas.spriteatlas[name]" locates a sub object of the asset
        private static string StripSubObject(string internalId)
        {
            if (internalId == null)
                return null;
            var bracket = internalId.IndexOf('[');
            return bracket > 0 && internalId.EndsWith(']') ? internalId[..bracket] : internalId;
        }

        /// <summary>
        /// The location of the asset at a container path (the asset path in the bundle), preferring the one in the bundle file given.
        /// </summary>
        public Location FindAsset(string container, string bundleFileName)
        {
            if (string.IsNullOrEmpty(container) || !assetsByInternalId.TryGetValue(container, out var candidates))
                return null;
            if (candidates.Count > 1 && !string.IsNullOrEmpty(bundleFileName))
            {
                var inBundle = candidates.FirstOrDefault(x => x.Dependencies.Any(d => d.IsBundle && string.Equals(d.FileName, bundleFileName, StringComparison.OrdinalIgnoreCase)));
                if (inBundle != null)
                    return inBundle;
            }
            return candidates.FirstOrDefault(x => x.InternalId.Length == container.Length) ?? candidates[0];
        }

        /// <summary>The bundle location of a bundle file.</summary>
        public Location FindBundle(string bundleFileName)
        {
            return !string.IsNullOrEmpty(bundleFileName) && bundlesByFileName.TryGetValue(bundleFileName, out var list) ? list[0] : null;
        }

        /// <summary>The asset locations whose first dependency is the bundle file (the bundle that holds them).</summary>
        public IEnumerable<Location> AssetsInBundle(string bundleFileName)
        {
            return Locations.Where(x => !x.IsBundle && x.Dependencies.Count > 0 && string.Equals(x.Dependencies[0].FileName, bundleFileName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A JSON friendly summary: the bundles and the assets with their addresses, labels and bundles.</summary>
        public object ToExportObject()
        {
            return new
            {
                File = FilePath,
                Format,
                LocatorId,
                BuildResultHash,
                Bundles = Locations.Where(x => x.IsBundle).Select(x => new
                {
                    x.PrimaryKey,
                    x.InternalId,
                    x.BundleName,
                    x.BundleHash,
                    x.BundleCrc,
                    x.BundleSize,
                    Dependencies = x.Dependencies.Select(d => d.PrimaryKey),
                }),
                Assets = Locations.Where(x => !x.IsBundle).Select(x => new
                {
                    Address = x.PrimaryKey,
                    x.InternalId,
                    x.ResourceType,
                    x.ProviderId,
                    Labels = x.Labels,
                    Bundles = x.Dependencies.Select(d => d.FileName),
                }),
            };
        }

        #endregion

        #region JSON

        private void ReadJson(JObject json)
        {
            Format = "JSON";
            LocatorId = (string)json["m_LocatorId"];
            BuildResultHash = (string)json["m_BuildResultHash"];
            var providerIds = json["m_ProviderIds"]?.ToObject<string[]>() ?? Array.Empty<string>();
            var internalIds = json["m_InternalIds"]?.ToObject<string[]>() ?? Array.Empty<string>();
            var prefixes = json["m_InternalIdPrefixes"]?.ToObject<string[]>();
            var oldKeys = json["m_Keys"]?.ToObject<string[]>(); //before Addressables 1.9 the primary keys were a string list
            var resourceTypes = (json["m_resourceTypes"] as JArray)?.Select(x => (string)x["m_ClassName"]).ToArray() ?? Array.Empty<string>();

            var bucketData = Convert.FromBase64String((string)json["m_BucketDataString"] ?? "");
            var keyData = Convert.FromBase64String((string)json["m_KeyDataString"] ?? "");
            var entryData = Convert.FromBase64String((string)json["m_EntryDataString"] ?? "");
            var extraData = Convert.FromBase64String((string)json["m_ExtraDataString"] ?? "");

            var bucketCount = BitConverter.ToInt32(bucketData, 0);
            var buckets = new (int keyOffset, int[] entries)[bucketCount];
            var position = 4;
            for (int i = 0; i < bucketCount; i++)
            {
                var keyOffset = BitConverter.ToInt32(bucketData, position);
                var count = BitConverter.ToInt32(bucketData, position + 4);
                position += 8;
                var entries = new int[count];
                for (int j = 0; j < count; j++, position += 4)
                {
                    entries[j] = BitConverter.ToInt32(bucketData, position);
                }
                buckets[i] = (keyOffset, entries);
            }
            var keys = buckets.Select(x => KeyToString(ReadJsonObject(keyData, x.keyOffset))).ToArray();

            var entryCount = BitConverter.ToInt32(entryData, 0);
            //internal id, provider, dependency key, dependency hash, extra data, primary key, resource type;
            //the first catalogs (2019.1) end after the extra data: their primary key is the first key of the location
            var entrySize = entryCount > 0 ? (entryData.Length - 4) / entryCount : 28;
            var dependencyKeys = new int[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                var entry = 4 + i * entrySize;
                int Field(int n) => n * 4 < entrySize ? BitConverter.ToInt32(entryData, entry + n * 4) : -1;
                var location = new Location
                {
                    InternalId = ExpandInternalId(prefixes, internalIds[Field(0)]),
                    ProviderId = Field(1) >= 0 ? providerIds[Field(1)] : null,
                    PrimaryKey = Field(5) < 0 ? null : oldKeys != null ? oldKeys[Field(5)] : keys[Field(5)],
                    ResourceType = Field(6) >= 0 && Field(6) < resourceTypes.Length ? resourceTypes[Field(6)] : null,
                };
                dependencyKeys[i] = Field(2);
                if (Field(4) >= 0 && ReadJsonObject(extraData, Field(4)) is JObject options)
                {
                    SetBundleOptions(location, options);
                }
                Locations.Add(location);
            }
            for (int i = 0; i < bucketCount; i++)
            {
                foreach (var entry in buckets[i].entries)
                {
                    Locations[entry].Keys.Add(keys[i]);
                }
            }
            //dependencies are a key: the locations of its bucket
            for (int i = 0; i < entryCount; i++)
            {
                //the address is the first key of the location alone (labels are shared), GUIDs aside
                Locations[i].PrimaryKey ??= Enumerable.Range(0, bucketCount)
                    .Where(k => buckets[k].entries.Length == 1 && buckets[k].entries[0] == i && !IsGuid(keys[k]))
                    .Select(k => keys[k]).FirstOrDefault() ?? Locations[i].Keys.FirstOrDefault();
                if (dependencyKeys[i] >= 0 && dependencyKeys[i] < bucketCount)
                {
                    Locations[i].Dependencies.AddRange(buckets[dependencyKeys[i]].entries.Select(x => Locations[x]));
                }
            }
        }

        private static string ExpandInternalId(string[] prefixes, string id)
        {
            if (prefixes == null || prefixes.Length == 0)
                return id;
            var hash = id.LastIndexOf('#');
            return hash > 0 && int.TryParse(id[..hash], out var index) && index < prefixes.Length ? prefixes[index] + id[(hash + 1)..] : id;
        }

        //SerializationUtilities.ObjectType: AsciiString, UnicodeString, UInt16, UInt32, Int32, Hash128, Type, JsonObject
        private static object ReadJsonObject(byte[] data, int offset)
        {
            var type = data[offset++];
            switch (type)
            {
                case 0:
                    return Encoding.ASCII.GetString(data, offset + 4, BitConverter.ToInt32(data, offset));
                case 1:
                    return Encoding.Unicode.GetString(data, offset + 4, BitConverter.ToInt32(data, offset));
                case 2:
                    return BitConverter.ToUInt16(data, offset);
                case 3:
                    return BitConverter.ToUInt32(data, offset);
                case 4:
                    return BitConverter.ToInt32(data, offset);
                case 5:
                case 6:
                    return Encoding.ASCII.GetString(data, offset + 1, data[offset]);
                case 7:
                    {
                        offset += 1 + data[offset]; //assembly name
                        var className = Encoding.ASCII.GetString(data, offset + 1, data[offset]);
                        offset += 1 + data[offset];
                        var json = Encoding.Unicode.GetString(data, offset + 4, BitConverter.ToInt32(data, offset));
                        return className.EndsWith("AssetBundleRequestOptions", StringComparison.Ordinal) ? JObject.Parse(json) : (object)json;
                    }
                default:
                    return null;
            }
        }

        private static void SetBundleOptions(Location location, JObject options)
        {
            location.BundleName = (string)options["m_BundleName"];
            location.BundleHash = (string)options["m_Hash"];
            location.BundleCrc = (uint?)options["m_Crc"] ?? 0;
            location.BundleSize = (long?)options["m_BundleSize"] ?? 0;
        }

        private static string KeyToString(object key) => key?.ToString() ?? string.Empty;

        #endregion

        #region Binary

        // BinaryStorageBuffer: values are addressed by offset; strings and arrays have their byte size in the 4 bytes before
        // the offset. Offsets of strings can carry flags: unicode, and "dynamic" (a chain of parts joined by a separator).
        private const uint UnicodeFlag = 0x80000000;
        private const uint DynamicFlag = 0x40000000;
        private const uint NoValue = uint.MaxValue;

        private byte[] buffer;
        private int binaryVersion;
        private readonly Dictionary<uint, Location> locationsByOffset = new Dictionary<uint, Location>();

        private void ReadBinary(byte[] data)
        {
            buffer = data;
            binaryVersion = BitConverter.ToInt32(data, 4);
            if (binaryVersion < 1 || binaryVersion > 3)
                throw new NotSupportedException($"binary catalog version {binaryVersion}");
            Format = $"binary v{binaryVersion}";
            var keysOffset = U32(8);
            LocatorId = ReadString(U32(12), '\0');
            //the first 1.21 catalogs have no build result hash: their keys start right after a shorter header
            if (!(binaryVersion == 1 && keysOffset == 0x20))
            {
                BuildResultHash = ReadString(U32(28), '\0');
            }

            var keyData = ReadU32Array(keysOffset);
            for (int i = 0; i + 1 < keyData.Length; i += 2)
            {
                var key = KeyToString(ReadTypedObject(keyData[i]));
                foreach (var locationOffset in ReadU32Array(keyData[i + 1]))
                {
                    ReadLocation(locationOffset).Keys.Add(key);
                }
            }
            buffer = null;
        }

        private uint U32(uint offset) => BitConverter.ToUInt32(buffer, (int)offset);

        private uint[] ReadU32Array(uint offset)
        {
            if (offset == NoValue)
                return Array.Empty<uint>();
            var count = U32(offset - 4) / 4;
            var result = new uint[count];
            for (uint i = 0; i < count; i++)
            {
                result[i] = U32(offset + i * 4);
            }
            return result;
        }

        private string ReadPlainString(uint id)
        {
            var unicode = (id & UnicodeFlag) != 0;
            var offset = id & ~(UnicodeFlag | DynamicFlag);
            var length = (int)U32(offset - 4);
            return (unicode ? Encoding.Unicode : Encoding.ASCII).GetString(buffer, (int)offset, length);
        }

        private string ReadString(uint id, char separator)
        {
            if (id == NoValue)
                return null;
            if (separator == '\0' || (id & DynamicFlag) == 0)
                return ReadPlainString(id);
            //a chain of (part, next part); version 2+ stores the rightmost part first
            var parts = new List<string>();
            for (var next = id; next != NoValue;)
            {
                var offset = next & ~(UnicodeFlag | DynamicFlag);
                parts.Add(ReadPlainString(U32(offset)));
                next = U32(offset + 4);
            }
            if (binaryVersion > 1)
                parts.Reverse();
            return string.Join(separator, parts);
        }

        /// <summary>A value with its type: (type offset, value offset); the type is (assembly name, class name).</summary>
        private object ReadTypedObject(uint offset)
        {
            if (offset == NoValue)
                return null;
            var typeOffset = U32(offset);
            var valueOffset = U32(offset + 4);
            var className = ReadTypeName(typeOffset);
            if (className == null)
                return null;
            //default values of value types are not stored
            switch (className)
            {
                case "System.Int32":
                    return valueOffset == NoValue ? 0 : BitConverter.ToInt32(buffer, (int)valueOffset);
                case "System.Int64":
                    return valueOffset == NoValue ? 0L : BitConverter.ToInt64(buffer, (int)valueOffset);
                case "System.Boolean":
                    return valueOffset != NoValue && buffer[valueOffset] != 0;
                case "System.String":
                    //(string, separator char)
                    return valueOffset == NoValue ? null : ReadString(U32(valueOffset), (char)BitConverter.ToUInt16(buffer, (int)valueOffset + 4));
                case "UnityEngine.Hash128":
                    return valueOffset == NoValue ? new string('0', 32) : Convert.ToHexString(buffer, (int)valueOffset, 16).ToLowerInvariant();
                default:
                    if (className.EndsWith("AssetBundleRequestOptions", StringComparison.Ordinal) && valueOffset != NoValue)
                    {
                        //hash, bundle name, crc, bundle size, common options
                        return (Hash: Convert.ToHexString(buffer, (int)U32(valueOffset), 16).ToLowerInvariant(),
                            Name: ReadString(U32(valueOffset + 4), '_'), Crc: U32(valueOffset + 8), Size: (long)U32(valueOffset + 12));
                    }
                    return $"({className})";
            }
        }

        private string ReadTypeName(uint offset)
        {
            if (offset == NoValue)
                return null;
            return ReadString(U32(offset + 4), '.');
        }

        private Location ReadLocation(uint offset)
        {
            if (locationsByOffset.TryGetValue(offset, out var location))
                return location;
            //primary key, internal id, provider, dependencies, dependency hash, extra data, resource type
            location = new Location
            {
                PrimaryKey = ReadString(U32(offset), '/'),
                InternalId = ReadString(U32(offset + 4), '/'),
                ProviderId = ReadString(U32(offset + 8), '.'),
                ResourceType = ReadTypeName(U32(offset + 24)),
            };
            locationsByOffset[offset] = location;
            Locations.Add(location);
            if (ReadTypedObject(U32(offset + 20)) is ValueTuple<string, string, uint, long> options)
            {
                location.BundleHash = options.Item1;
                location.BundleName = options.Item2;
                location.BundleCrc = options.Item3;
                location.BundleSize = options.Item4;
            }
            foreach (var dependency in ReadU32Array(U32(offset + 12)))
            {
                location.Dependencies.Add(ReadLocation(dependency));
            }
            return location;
        }

        #endregion
    }
}
