using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AssetStudio
{
    public class SerializedFile
    {
        public AssetsManager assetsManager;
        public FileReader reader;
        public Game game;
        public long offset = 0;
        public string fullName;
        public string originalPath;
        public string fileName;
        public int[] version = { 0, 0, 0, 0 };
        public BuildType buildType;
        public List<Object> Objects;
        public Dictionary<long, Object> ObjectsDic;

        public SerializedFileHeader header;
        private byte m_FileEndianess;
        public string unityVersion = "2.5.0f5";
        public BuildTarget m_TargetPlatform = BuildTarget.UnknownPlatform;
        private bool m_EnableTypeTree = true;
        public List<SerializedType> m_Types;
        public int bigIDEnabled = 0;
        public List<ObjectInfo> m_Objects;
        private List<LocalSerializedObjectIdentifier> m_ScriptTypes;
        public List<FileIdentifier> m_Externals;
        public List<SerializedType> m_RefTypes;
        public string userInformation;

        /// <summary>
        /// Indicates if this file was originally loaded without version info (stripped).
        /// This is set once during initial load and won't change even if SetVersion is called later.
        /// Used to determine if the file's object info may be incorrect (from a stripped standalone CAB).
        /// </summary>
        public bool WasOriginallyStripped { get; private set; } = false;

        /// <summary>
        /// Indicates if this file was loaded from a bundle/archive (MemoryStream) rather than directly from disk (FileStream).
        /// Files from bundles typically have correct object data, while standalone CAB files may be stripped/incomplete.
        /// </summary>
        public bool IsFromBundle { get; set; } = false;

        public SerializedFile(FileReader reader, AssetsManager assetsManager)
        {
            this.assetsManager = assetsManager;
            this.reader = reader;
            game = assetsManager.Game;
            fullName = reader.FullPath;
            fileName = reader.FileName;

            // ReadHeader
            header = new SerializedFileHeader();
            header.m_MetadataSize = reader.ReadUInt32();
            header.m_FileSize = reader.ReadUInt32();
            header.m_Version = (SerializedFileFormatVersion)reader.ReadUInt32();
            header.m_DataOffset = reader.ReadUInt32();

            if (header.m_Version >= SerializedFileFormatVersion.Unknown_9)
            {
                header.m_Endianess = reader.ReadByte();
                header.m_Reserved = reader.ReadBytes(3);
                m_FileEndianess = header.m_Endianess;
            }
            else
            {
                reader.Position = header.m_FileSize - header.m_MetadataSize;
                m_FileEndianess = reader.ReadByte();
            }

            if (header.m_Version >= SerializedFileFormatVersion.LargeFilesSupport)
            {
                header.m_MetadataSize = reader.ReadUInt32();
                header.m_FileSize = reader.ReadInt64();
                header.m_DataOffset = reader.ReadInt64();
                reader.ReadInt64(); // unknown

            }

            Logger.Verbose($"File {fileName} Info: {header}");

            // ReadMetadata
            if (m_FileEndianess == 0)
            {
                reader.Endian = EndianType.LittleEndian;
                Logger.Verbose($"Endianness {reader.Endian}");
            }
            if (header.m_Version >= SerializedFileFormatVersion.Unknown_7)
            {
                unityVersion = reader.ReadStringToNull();
                Logger.Verbose($"Unity version {unityVersion}");
                // Track if the file was originally stripped
                WasOriginallyStripped = (unityVersion == strippedVersion);
                SetVersion(unityVersion);
            }
            if (header.m_Version >= SerializedFileFormatVersion.Unknown_8)
            {
                m_TargetPlatform = (BuildTarget)reader.ReadInt32();
                if (!Enum.IsDefined(typeof(BuildTarget), m_TargetPlatform))
                {
                    Logger.Verbose($"Parsed target format {m_TargetPlatform} doesn't match any of supported formats, defaulting to {BuildTarget.UnknownPlatform}");
                    m_TargetPlatform = BuildTarget.UnknownPlatform;
                }
                else if (m_TargetPlatform == BuildTarget.NoTarget && game.Type.IsMhyGroup())
                {
                    Logger.Verbose($"Selected game {game.Name} is a mhy game, forcing target format {BuildTarget.StandaloneWindows64}");
                    m_TargetPlatform = BuildTarget.StandaloneWindows64;
                }
                Logger.Verbose($"Target format {m_TargetPlatform}");
            }
            if (header.m_Version >= SerializedFileFormatVersion.HasTypeTreeHashes)
            {
                m_EnableTypeTree = reader.ReadBoolean();
            }

            // Read Types
            int typeCount = reader.ReadInt32();
            m_Types = new List<SerializedType>();
            Logger.Verbose($"Found {typeCount} serialized types");
            for (int i = 0; i < typeCount; i++)
            {
                m_Types.Add(ReadSerializedType(false));
            }

            if (header.m_Version >= SerializedFileFormatVersion.Unknown_7 && header.m_Version < SerializedFileFormatVersion.Unknown_14)
            {
                bigIDEnabled = reader.ReadInt32();
            }

            // Read Objects
            int objectCount = reader.ReadInt32();
            m_Objects = new List<ObjectInfo>();
            Objects = new List<Object>();
            ObjectsDic = new Dictionary<long, Object>();
            Logger.Verbose($"Found {objectCount} objects");
            for (int i = 0; i < objectCount; i++)
            {
                var objectInfo = new ObjectInfo();
                if (bigIDEnabled != 0)
                {
                    objectInfo.m_PathID = reader.ReadInt64();
                }
                else if (header.m_Version < SerializedFileFormatVersion.Unknown_14)
                {
                    objectInfo.m_PathID = reader.ReadInt32();
                }
                else
                {
                    reader.AlignStream();
                    objectInfo.m_PathID = reader.ReadInt64();
                }

                if (header.m_Version >= SerializedFileFormatVersion.LargeFilesSupport)
                    objectInfo.byteStart = reader.ReadInt64();
                else
                    objectInfo.byteStart = reader.ReadUInt32();

                objectInfo.byteStart += header.m_DataOffset;
                objectInfo.byteSize = reader.ReadUInt32();
                objectInfo.typeID = reader.ReadInt32();
                if (header.m_Version < SerializedFileFormatVersion.RefactoredClassId)
                {
                    objectInfo.classID = reader.ReadUInt16();
                    objectInfo.serializedType = m_Types.Find(x => x.classID == objectInfo.typeID);
                }
                else
                {
                    var type = m_Types[objectInfo.typeID];
                    objectInfo.serializedType = type;
                    objectInfo.classID = type.classID;
                }
                if (header.m_Version < SerializedFileFormatVersion.HasScriptTypeIndex)
                {
                    objectInfo.isDestroyed = reader.ReadUInt16();
                }
                if (header.m_Version >= SerializedFileFormatVersion.HasScriptTypeIndex && header.m_Version < SerializedFileFormatVersion.RefactorTypeData)
                {
                    var m_ScriptTypeIndex = reader.ReadInt16();
                    if (objectInfo.serializedType != null)
                        objectInfo.serializedType.m_ScriptTypeIndex = m_ScriptTypeIndex;
                }
                if (header.m_Version == SerializedFileFormatVersion.SupportsStrippedObject || header.m_Version == SerializedFileFormatVersion.RefactoredClassId)
                {
                    objectInfo.stripped = reader.ReadByte();
                }
                Logger.Verbose($"Object Info: {objectInfo}");
                m_Objects.Add(objectInfo);
            }

            if (header.m_Version >= SerializedFileFormatVersion.HasScriptTypeIndex)
            {
                int scriptCount = reader.ReadInt32();
                Logger.Verbose($"Found {scriptCount} scripts");
                m_ScriptTypes = new List<LocalSerializedObjectIdentifier>();
                for (int i = 0; i < scriptCount; i++)
                {
                    var m_ScriptType = new LocalSerializedObjectIdentifier();
                    m_ScriptType.localSerializedFileIndex = reader.ReadInt32();
                    if (header.m_Version < SerializedFileFormatVersion.Unknown_14)
                    {
                        m_ScriptType.localIdentifierInFile = reader.ReadInt32();
                    }
                    else
                    {
                        reader.AlignStream();
                        m_ScriptType.localIdentifierInFile = reader.ReadInt64();
                    }
                    Logger.Verbose($"Script Info: {m_ScriptType}");
                    m_ScriptTypes.Add(m_ScriptType);
                }
            }

            int externalsCount = reader.ReadInt32();
            m_Externals = new List<FileIdentifier>();
            Logger.Verbose($"Found {externalsCount} externals");
            for (int i = 0; i < externalsCount; i++)
            {
                var m_External = new FileIdentifier();
                if (header.m_Version >= SerializedFileFormatVersion.Unknown_6)
                {
                    var tempEmpty = reader.ReadStringToNull();
                }
                if (header.m_Version >= SerializedFileFormatVersion.Unknown_5)
                {
                    m_External.guid = new Guid(reader.ReadBytes(16));
                    m_External.type = reader.ReadInt32();
                }
                m_External.pathName = reader.ReadStringToNull();
                m_External.fileName = Path.GetFileName(m_External.pathName);
                Logger.Verbose($"External Info: {m_External}");
                m_Externals.Add(m_External);
            }

            if (header.m_Version >= SerializedFileFormatVersion.SupportsRefObject)
            {
                int refTypesCount = reader.ReadInt32();
                m_RefTypes = new List<SerializedType>();
                Logger.Verbose($"Found {refTypesCount} reference types");
                for (int i = 0; i < refTypesCount; i++)
                {
                    m_RefTypes.Add(ReadSerializedType(true));
                }
            }

            if (header.m_Version >= SerializedFileFormatVersion.SharedTypeTrees && m_EnableTypeTree)
            {
                ResolveSharedTypeTrees();
            }

            if (header.m_Version >= SerializedFileFormatVersion.Unknown_5)
            {
                userInformation = reader.ReadStringToNull();
            }

            //reader.AlignStream(16);
        }

        public void SetVersion(string stringVersion)
        {
            if (stringVersion != strippedVersion)
            {
                unityVersion = stringVersion;
                // Extract build type (e.g., 'f' from "2020.1.0f1" or "6000.0.58f2")
                var buildSplit = Regex.Replace(stringVersion, @"\d", "").Split(new[] { "." }, StringSplitOptions.RemoveEmptyEntries);
                buildType = new BuildType(buildSplit[0]);
                // Parse version components (e.g., [2020, 1, 0, 1] or [6000, 0, 58, 2])
                // Supports Unity 2.x through Unity 6 (6000.x) and beyond
                var versionSplit = Regex.Replace(stringVersion, @"\D", ".").Split(new[] { "." }, StringSplitOptions.RemoveEmptyEntries);
                version = versionSplit.Select(int.Parse).ToArray();
            }
        }

        private SerializedType ReadSerializedType(bool isRefType)
        {
            Logger.Verbose($"Attempting to parse serialized" + (isRefType ? " reference" : " ") + "type");
            var type = new SerializedType();

            type.classID = reader.ReadInt32();

            if (game.Type.IsGIGroup() && BitConverter.ToBoolean(header.m_Reserved))
            {
                Logger.Verbose($"Encoded class ID {type.classID}, decoding...");
                type.classID = DecodeClassID(type.classID);
            }

            if (header.m_Version >= SerializedFileFormatVersion.RefactoredClassId)
            {
                type.m_IsStrippedType = reader.ReadBoolean();
            }

            if (header.m_Version >= SerializedFileFormatVersion.RefactorTypeData)
            {
                type.m_ScriptTypeIndex = reader.ReadInt16();
            }

            if (header.m_Version >= SerializedFileFormatVersion.HasTypeTreeHashes)
            {
                if (isRefType && type.m_ScriptTypeIndex >= 0)
                {
                    type.m_ScriptID = reader.ReadBytes(16);
                }
                else if ((header.m_Version < SerializedFileFormatVersion.RefactoredClassId && type.classID < 0) || (header.m_Version >= SerializedFileFormatVersion.RefactoredClassId && type.classID == 114))
                {
                    type.m_ScriptID = reader.ReadBytes(16);
                }
                type.m_OldTypeHash = reader.ReadBytes(16);
            }

            if (header.m_Version >= SerializedFileFormatVersion.TypeTreeBlobs)
            {
                type.m_TypeTreeHash = reader.ReadBytes(16);
            }

            if (m_EnableTypeTree)
            {
                Logger.Verbose($"File has type tree enabled !!");
                type.m_Type = new TypeTree();
                type.m_Type.m_Nodes = new List<TypeTreeNode>();
                var extractedTypeTree = false;
                if (header.m_Version >= SerializedFileFormatVersion.TypeTreeBlobs)
                {
                    var blobSize = reader.ReadUInt32();
                    if (blobSize == 0)
                    {
                        extractedTypeTree = true; // stored in a separate .typetreedata file
                    }
                    else if (header.m_Version >= SerializedFileFormatVersion.SharedTypeTrees)
                    {
                        // the referenced sub trees are only known after the reference types, see ResolveSharedTypeTrees
                        pendingTypeTrees.Add((type.m_Type, ReadTypeTreeBlob()));
                    }
                    else
                    {
                        AppendSharedTypeTree(type.m_Type.m_Nodes, ReadTypeTreeBlob(), 0, false, null, 0);
                    }
                }
                else if (header.m_Version >= SerializedFileFormatVersion.Unknown_12 || header.m_Version == SerializedFileFormatVersion.Unknown_10)
                {
                    TypeTreeBlobRead(type.m_Type);
                }
                else
                {
                    ReadTypeTree(type.m_Type);
                }
                if (header.m_Version >= SerializedFileFormatVersion.StoresTypeDependencies)
                {
                    if (isRefType)
                    {
                        type.m_KlassName = reader.ReadStringToNull();
                        type.m_NameSpace = reader.ReadStringToNull();
                        type.m_AsmName = reader.ReadStringToNull();
                    }
                    else
                    {
                        type.m_TypeDependencies = reader.ReadInt32Array();
                    }
                }
                if (extractedTypeTree)
                {
                    type.m_Type = null; // read like a file without type trees
                }
            }

            Logger.Verbose($"Serialized type info: {type}");
            return type;
        }

        private void ReadTypeTree(TypeTree m_Type, int level = 0)
        {
            Logger.Verbose($"Attempting to parse type tree...");
            var typeTreeNode = new TypeTreeNode();
            m_Type.m_Nodes.Add(typeTreeNode);
            typeTreeNode.m_Level = level;
            typeTreeNode.m_Type = reader.ReadStringToNull();
            typeTreeNode.m_Name = reader.ReadStringToNull();
            typeTreeNode.m_ByteSize = reader.ReadInt32();
            if (header.m_Version == SerializedFileFormatVersion.Unknown_2)
            {
                var variableCount = reader.ReadInt32();
            }
            if (header.m_Version != SerializedFileFormatVersion.Unknown_3)
            {
                typeTreeNode.m_Index = reader.ReadInt32();
            }
            typeTreeNode.m_TypeFlags = reader.ReadInt32();
            typeTreeNode.m_Version = reader.ReadInt32();
            if (header.m_Version != SerializedFileFormatVersion.Unknown_3)
            {
                typeTreeNode.m_MetaFlag = reader.ReadInt32();
            }

            int childrenCount = reader.ReadInt32();
            for (int i = 0; i < childrenCount; i++)
            {
                ReadTypeTree(m_Type, level + 1);
            }

            Logger.Verbose($"Type Tree Info: {m_Type}");
        }

        private void TypeTreeBlobRead(TypeTree m_Type)
        {
            Logger.Verbose($"Attempting to parse blob type tree...");
            int numberOfNodes = reader.ReadInt32();
            int stringBufferSize = reader.ReadInt32();
            Logger.Verbose($"Found {numberOfNodes} nodes and {stringBufferSize} strings");
            for (int i = 0; i < numberOfNodes; i++)
            {
                var typeTreeNode = new TypeTreeNode();
                m_Type.m_Nodes.Add(typeTreeNode);
                typeTreeNode.m_Version = reader.ReadUInt16();
                typeTreeNode.m_Level = reader.ReadByte();
                typeTreeNode.m_TypeFlags = reader.ReadByte();
                typeTreeNode.m_TypeStrOffset = reader.ReadUInt32();
                typeTreeNode.m_NameStrOffset = reader.ReadUInt32();
                typeTreeNode.m_ByteSize = reader.ReadInt32();
                typeTreeNode.m_Index = reader.ReadInt32();
                typeTreeNode.m_MetaFlag = reader.ReadInt32();
                if (header.m_Version >= SerializedFileFormatVersion.TypeTreeNodeWithTypeFlags)
                {
                    typeTreeNode.m_RefTypeHash = reader.ReadUInt64();
                }
            }
            m_Type.m_StringBuffer = reader.ReadBytes(stringBufferSize);

            using (var stringBufferReader = new EndianBinaryReader(new MemoryStream(m_Type.m_StringBuffer), EndianType.LittleEndian))
            {
                for (int i = 0; i < numberOfNodes; i++)
                {
                    var m_Node = m_Type.m_Nodes[i];
                    m_Node.m_Type = ReadString(stringBufferReader, m_Node.m_TypeStrOffset);
                    m_Node.m_Name = ReadString(stringBufferReader, m_Node.m_NameStrOffset);
                }
            }

            Logger.Verbose($"Type Tree Info: {m_Type}");

            string ReadString(EndianBinaryReader stringBufferReader, uint value)
            {
                var isOffset = (value & 0x80000000) == 0;
                if (isOffset)
                {
                    stringBufferReader.BaseStream.Position = value;
                    return stringBufferReader.ReadStringToNull();
                }
                var offset = value & 0x7FFFFFFF;
                if (CommonString.StringBuffer.TryGetValue(offset, out var str))
                {
                    return str;
                }
                return offset.ToString();
            }
        }

        #region Type tree blobs (SerializedFileFormatVersion.TypeTreeBlobs and SharedTypeTrees)

        private sealed class SharedTypeTreeBlob
        {
            public (ushort version, byte level, byte flags, uint typeStr, uint nameStr, int byteSize, int index, int metaFlag, ulong reference)[] nodes;
            public byte[] strings;
            public string[] references; // hashes of the referenced sub trees
        }

        private const byte SubTreeReferenceFlag = 0x20;
        private readonly List<(TypeTree tree, SharedTypeTreeBlob blob)> pendingTypeTrees = new List<(TypeTree, SharedTypeTreeBlob)>();

        private SharedTypeTreeBlob ReadTypeTreeBlob()
        {
            var magic = reader.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'm' || magic[1] != 'h' || magic[2] != 't' || magic[3] != 't')
                throw new InvalidDataException($"unexpected type tree blob magic {Convert.ToHexString(magic)}");
            reader.ReadUInt32(); // blob version
            var numberOfNodes = reader.ReadInt32();
            var stringBufferSize = reader.ReadInt32();
            var blob = new SharedTypeTreeBlob { nodes = new (ushort, byte, byte, uint, uint, int, int, int, ulong)[numberOfNodes] };
            for (int i = 0; i < numberOfNodes; i++)
            {
                blob.nodes[i] = (reader.ReadUInt16(), reader.ReadByte(), reader.ReadByte(), reader.ReadUInt32(), reader.ReadUInt32(),
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt64());
            }
            blob.strings = reader.ReadBytes(stringBufferSize);
            blob.references = new string[header.m_Version >= SerializedFileFormatVersion.SharedTypeTrees ? reader.ReadInt32() : 0];
            for (int i = 0; i < blob.references.Length; i++)
            {
                blob.references[i] = Convert.ToHexString(reader.ReadBytes(16));
            }
            return blob;
        }

        /// <summary>Reads the sub tree table and expands the sub tree references of every type tree.</summary>
        private void ResolveSharedTypeTrees()
        {
            var count = reader.ReadInt32();
            var subTrees = new Dictionary<string, SharedTypeTreeBlob>(count);
            for (int i = 0; i < count; i++)
            {
                var hash = Convert.ToHexString(reader.ReadBytes(16));
                reader.ReadUInt32(); // blob size
                subTrees[hash] = ReadTypeTreeBlob();
            }
            foreach (var (tree, blob) in pendingTypeTrees)
            {
                AppendSharedTypeTree(tree.m_Nodes, blob, 0, false, subTrees, 0);
            }
            pendingTypeTrees.Clear();
        }

        private void AppendSharedTypeTree(List<TypeTreeNode> nodes, SharedTypeTreeBlob blob, int baseLevel, bool skipRoot,
            Dictionary<string, SharedTypeTreeBlob> subTrees, int depth)
        {
            if (depth > 64)
                throw new InvalidDataException("type tree references nested too deeply");
            for (int i = skipRoot ? 1 : 0; i < blob.nodes.Length; i++)
            {
                var raw = blob.nodes[i];
                var level = baseLevel + raw.level;
                nodes.Add(new TypeTreeNode
                {
                    m_Version = raw.version,
                    m_Level = level,
                    m_TypeFlags = raw.flags & ~SubTreeReferenceFlag,
                    m_Type = ReadSharedString(blob.strings, raw.typeStr),
                    m_Name = ReadSharedString(blob.strings, raw.nameStr),
                    m_ByteSize = raw.byteSize,
                    m_Index = nodes.Count,
                    m_MetaFlag = raw.metaFlag,
                });
                if ((raw.flags & SubTreeReferenceFlag) != 0)
                {
                    // the node stands for the root of the sub tree, its children follow one level below it
                    if (subTrees != null && raw.reference < (ulong)blob.references.Length && subTrees.TryGetValue(blob.references[raw.reference], out var subTree))
                    {
                        AppendSharedTypeTree(nodes, subTree, level, true, subTrees, depth + 1);
                    }
                    else
                    {
                        Logger.Warning($"Type tree of {fileName}: sub tree {raw.reference} not found");
                    }
                }
            }
        }

        private static string ReadSharedString(byte[] strings, uint value)
        {
            if ((value & 0x80000000) != 0)
            {
                var offset = value & 0x7FFFFFFF;
                return CommonString.StringBuffer.TryGetValue(offset, out var str) ? str : offset.ToString();
            }
            var end = Array.IndexOf(strings, (byte)0, (int)value);
            return System.Text.Encoding.UTF8.GetString(strings, (int)value, (end < 0 ? strings.Length : end) - (int)value);
        }

        #endregion

        public void AddObject(Object obj)
        {
            Logger.Verbose($"Caching object with {obj.m_PathID} in file {fileName}...");
            Objects.Add(obj);
            ObjectsDic.Add(obj.m_PathID, obj);
        }

        private static int DecodeClassID(int value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            value = BitConverter.ToInt32(bytes, 0);
            return (value ^ 0x23746FBE) - 3;
        }

        public bool IsVersionStripped => unityVersion == strippedVersion;

        private const string strippedVersion = "0.0.0";
    }
}
