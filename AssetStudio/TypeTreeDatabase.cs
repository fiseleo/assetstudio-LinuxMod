using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AssetStudio
{
    /// <summary>
    /// The release type trees of Unity's classes for every Unity version, used for objects of files built without
    /// type trees. The data is a TPK file (format of https://github.com/AssetRipper/Tpk, MIT) made from
    /// https://github.com/AssetRipper/TypeTreeDumps, the lzma.tpk that UnityPy ships (MIT).
    /// Newer (or missing) versions can be added without rebuilding: TypeTreeDumps InfoJson files (<c>&lt;version&gt;.json</c>)
    /// put in <see cref="DumpsDirectory"/> are used for the versions they are the closest release to.
    /// </summary>
    public static class TypeTreeDatabase
    {
        private sealed class Node
        {
            public ushort TypeName;
            public ushort Name;
            public int ByteSize;
            public short Version;
            public byte TypeFlags;
            public uint MetaFlag;
            public ushort[] SubNodes;
        }

        private const uint TpkMagic = 0x2A4B5054; //TPK*
        private const byte HasReleaseRootNode = 128;
        private const byte HasEditorRootNode = 64;

        private static readonly object loadLock = new object();
        private static bool loaded;
        private static Dictionary<int, List<(ulong version, int rootNode)>> classes; //root node -1: the class is absent from that version on
        private static List<Node> nodes;
        private static List<string> strings;
        private static readonly ConcurrentDictionary<(int, ulong), TypeTree> cache = new ConcurrentDictionary<(int, ulong), TypeTree>();
        private static readonly ConcurrentDictionary<ulong, bool> warnedVersions = new ConcurrentDictionary<ulong, bool>();
        private static ulong latestVersion;

        public static string LatestVersion { get; private set; }

        private static readonly Regex dumpVersionRegex = new Regex(@"^(\d+)\.(\d+)\.(\d+)([abcfpx])(\d+)$", RegexOptions.Compiled);
        private static string dumpsDirectory;
        private static List<(ulong version, string path)> dumpFiles; //sorted by version
        private static readonly ConcurrentDictionary<ulong, Dictionary<int, TypeTree>> dumps = new ConcurrentDictionary<ulong, Dictionary<int, TypeTree>>();

        /// <summary>
        /// Folder of TypeTreeDumps InfoJson files (<c>InfoJson/&lt;version&gt;.json</c>) that extend the embedded database;
        /// null: the <c>TypeTreeDumps</c> folder in <see cref="AppData.Directory"/>.
        /// </summary>
        public static string DumpsDirectory
        {
            get => dumpsDirectory ?? Path.Combine(AppData.Directory, "TypeTreeDumps");
            set
            {
                lock (loadLock)
                {
                    dumpsDirectory = value;
                    dumpFiles = null;
                    dumps.Clear();
                    cache.Clear();
                    warnedVersions.Clear();
                }
            }
        }

        /// <summary>The Unity versions of the dumps found in <see cref="DumpsDirectory"/>.</summary>
        public static IReadOnlyList<string> DumpVersions
        {
            get
            {
                lock (loadLock)
                {
                    return ScanDumps().Select(x => FormatVersion(x.version)).ToList();
                }
            }
        }

        /// <summary>
        /// The type tree of a class in a Unity version, null when unknown. MonoBehaviour is left out: its fields depend on the script.
        /// </summary>
        public static TypeTree GetTypeTree(int classID, int[] version, BuildType buildType)
        {
            if (classID == (int)ClassIDType.MonoBehaviour || version == null || version.Length < 3 || version[0] == 0)
                return null;
            if (!Load())
                return null;
            var bits = PackVersion(version[0], version[1], version[2], buildType?.Order ?? 3, version.Length > 3 ? version[3] : 0);
            List<(ulong version, string path)> dumpList;
            lock (loadLock)
            {
                dumpList = ScanDumps();
            }
            var latest = Math.Max(latestVersion, dumpList.Count > 0 ? dumpList[^1].version : 0);
            if (bits > latest && warnedVersions.TryAdd(bits, true))
            {
                Logger.Warning($"Unity {string.Join('.', version)} is newer than the type tree database (Unity {FormatVersion(latest)}): the objects of files without type trees are read with the latest known layouts. Newer TypeTreeDumps InfoJson files can be put in {DumpsDirectory}");
            }
            return cache.GetOrAdd((classID, bits), key => Build(key.Item1, key.Item2, dumpList));
        }

        private static ulong PackVersion(int major, int minor, int patch, int buildTypeOrder, int build)
        {
            return ((ulong)(ushort)major << 48) | ((ulong)(ushort)minor << 32) | ((ulong)(ushort)patch << 16)
                | ((ulong)(byte)buildTypeOrder << 8) | (byte)build;
        }

        private static string FormatVersion(ulong version)
        {
            return $"{version >> 48}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}{"abcfpx"[(int)Math.Min((version >> 8) & 0xFF, 5)]}{version & 0xFF}";
        }

        private static TypeTree Build(int classID, ulong version, List<(ulong version, string path)> dumpList)
        {
            //the closest release at or before the version: a layout change of the embedded data, or a dump
            ulong embeddedVersion = 0;
            var rootNode = -1;
            var known = classes.TryGetValue(classID, out var ranges);
            if (known)
            {
                foreach (var (minVersion, root) in ranges)
                {
                    if (minVersion > version)
                        break;
                    embeddedVersion = minVersion;
                    rootNode = root;
                }
            }
            var dumpIndex = dumpList.FindLastIndex(x => x.version <= version);
            if (dumpIndex >= 0 && dumpList[dumpIndex].version >= embeddedVersion)
            {
                var dump = LoadDump(dumpList[dumpIndex]);
                if (dump != null)
                {
                    return dump.TryGetValue(classID, out var dumpTree) ? dumpTree : null;
                }
            }
            if (rootNode < 0)
                return null;
            var typeTree = new TypeTree { m_Nodes = new List<TypeTreeNode>() };
            AddNode(typeTree.m_Nodes, rootNode, 0);
            return typeTree;
        }

        private static List<(ulong version, string path)> ScanDumps()
        {
            if (dumpFiles != null)
                return dumpFiles;
            dumpFiles = new List<(ulong, string)>();
            var directory = DumpsDirectory;
            try
            {
                if (Directory.Exists(directory))
                {
                    foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                    {
                        var match = dumpVersionRegex.Match(Path.GetFileNameWithoutExtension(path));
                        if (!match.Success)
                            continue;
                        var order = new BuildType(match.Groups[4].Value).Order;
                        dumpFiles.Add((PackVersion(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), order, int.Parse(match.Groups[5].Value)), path));
                    }
                    dumpFiles.Sort((a, b) => a.version.CompareTo(b.version));
                    if (dumpFiles.Count > 0)
                    {
                        Logger.Info($"Type tree dumps in {directory}: Unity {string.Join(", ", dumpFiles.Select(x => FormatVersion(x.version)))}");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to list the type tree dumps in {directory}: {e.Message}");
            }
            return dumpFiles;
        }

        /// <summary>The release type trees of a TypeTreeDumps InfoJson file, by class ID; null when it can't be read.</summary>
        private static Dictionary<int, TypeTree> LoadDump((ulong version, string path) dump)
        {
            return dumps.GetOrAdd(dump.version, _ =>
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(dump.path));
                    var result = new Dictionary<int, TypeTree>();
                    foreach (var unityClass in json["Classes"])
                    {
                        if (unityClass["ReleaseRootNode"] is not JObject root)
                            continue;
                        var typeTree = new TypeTree { m_Nodes = new List<TypeTreeNode>() };
                        AddDumpNode(typeTree.m_Nodes, root, 0);
                        result[(int)unityClass["TypeID"]] = typeTree;
                    }
                    Logger.Verbose($"Type tree dump {dump.path} loaded, {result.Count} classes");
                    return result;
                }
                catch (Exception e)
                {
                    Logger.Warning($"Unable to read the type tree dump {dump.path}: {e.Message}");
                    return null;
                }
            });
        }

        private static void AddDumpNode(List<TypeTreeNode> list, JObject node, int level)
        {
            list.Add(new TypeTreeNode
            {
                m_Type = (string)node["TypeName"],
                m_Name = (string)node["Name"],
                m_ByteSize = (int)node["ByteSize"],
                m_Version = (int)node["Version"],
                m_TypeFlags = (int)node["TypeFlags"],
                m_MetaFlag = (int)(long)node["MetaFlag"],
                m_Level = level,
                m_Index = list.Count,
            });
            if (node["SubNodes"] is JArray subNodes)
            {
                foreach (JObject subNode in subNodes)
                {
                    AddDumpNode(list, subNode, level + 1);
                }
            }
        }

        private static void AddNode(List<TypeTreeNode> list, int index, int level)
        {
            var node = nodes[index];
            list.Add(new TypeTreeNode
            {
                m_Type = strings[node.TypeName],
                m_Name = strings[node.Name],
                m_ByteSize = node.ByteSize,
                m_Version = node.Version,
                m_TypeFlags = node.TypeFlags,
                m_MetaFlag = (int)node.MetaFlag,
                m_Level = level,
                m_Index = list.Count,
            });
            foreach (var subNode in node.SubNodes)
            {
                AddNode(list, subNode, level + 1);
            }
        }

        private static bool Load()
        {
            lock (loadLock)
            {
                if (loaded)
                    return classes != null;
                loaded = true;
                try
                {
                    using var stream = typeof(TypeTreeDatabase).Assembly.GetManifestResourceStream("AssetStudio.Resources.lzma.tpk");
                    if (stream == null)
                        return false;
                    Read(stream);
                    Logger.Verbose($"Type tree database loaded, {classes.Count} classes up to Unity {LatestVersion}");
                    return true;
                }
                catch (Exception e)
                {
                    Logger.Warning($"Unable to load the type tree database: {e.Message}");
                    classes = null;
                    return false;
                }
            }
        }

        private static void Read(Stream stream)
        {
            using var header = new BinaryReader(stream);
            if (header.ReadUInt32() != TpkMagic)
                throw new InvalidDataException("not a TPK file");
            var formatVersion = header.ReadByte();
            var compression = header.ReadByte();
            var dataType = header.ReadByte();
            header.ReadByte();
            header.ReadUInt32();
            var compressedSize = header.ReadInt32();
            var decompressedSize = header.ReadInt32();
            if (formatVersion != 2 || dataType != 0)
                throw new NotSupportedException($"TPK version {formatVersion} data type {dataType}");
            var compressed = header.ReadBytes(compressedSize);
            byte[] data;
            switch (compression)
            {
                case 0:
                    data = compressed;
                    break;
                case 2: //LZMA: 5 bytes of properties, then the stream
                    {
                        using var input = new MemoryStream(compressed);
                        using var output = new MemoryStream(decompressedSize);
                        SevenZipHelper.StreamDecompress(input, output, compressed.Length, decompressedSize);
                        data = output.ToArray();
                        break;
                    }
                default:
                    throw new NotSupportedException($"TPK compression {compression}");
            }

            using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            reader.ReadInt64(); //creation time
            var versionCount = reader.ReadInt32();
            ulong latest = 0;
            for (int i = 0; i < versionCount; i++)
            {
                latest = Math.Max(latest, reader.ReadUInt64());
            }
            latestVersion = latest;
            LatestVersion = FormatVersion(latest);

            var classCount = reader.ReadInt32();
            classes = new Dictionary<int, List<(ulong, int)>>(classCount);
            for (int i = 0; i < classCount; i++)
            {
                var id = reader.ReadInt32();
                var count = reader.ReadInt32();
                var ranges = new List<(ulong, int)>(count);
                for (int j = 0; j < count; j++)
                {
                    var minVersion = reader.ReadUInt64();
                    var root = -1;
                    if (reader.ReadBoolean()) //has class data
                    {
                        reader.ReadUInt16(); //name
                        reader.ReadUInt16(); //base
                        var flags = reader.ReadByte();
                        if ((flags & HasEditorRootNode) != 0)
                            reader.ReadUInt16();
                        if ((flags & HasReleaseRootNode) != 0)
                            root = reader.ReadUInt16();
                    }
                    ranges.Add((minVersion, root));
                }
                classes[id] = ranges;
            }

            var commonStringVersions = reader.ReadInt32(); //common strings are not needed: the nodes have their names
            for (int i = 0; i < commonStringVersions; i++)
            {
                reader.ReadUInt64();
                var entries = reader.ReadInt32();
                reader.BaseStream.Position += entries * 4L;
            }

            var nodeCount = reader.ReadInt32();
            nodes = new List<Node>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
            {
                var node = new Node
                {
                    TypeName = reader.ReadUInt16(),
                    Name = reader.ReadUInt16(),
                    ByteSize = reader.ReadInt32(),
                    Version = reader.ReadInt16(),
                    TypeFlags = reader.ReadByte(),
                    MetaFlag = reader.ReadUInt32(),
                };
                node.SubNodes = new ushort[reader.ReadUInt16()];
                for (int j = 0; j < node.SubNodes.Length; j++)
                {
                    node.SubNodes[j] = reader.ReadUInt16();
                }
                nodes.Add(node);
            }

            var stringCount = reader.ReadInt32();
            strings = new List<string>(stringCount);
            for (int i = 0; i < stringCount; i++)
            {
                strings.Add(reader.ReadString());
            }
        }
    }
}
