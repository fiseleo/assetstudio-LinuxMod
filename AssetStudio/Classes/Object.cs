using Newtonsoft.Json;
using System.Collections.Specialized;

namespace AssetStudio
{
    public class Object
    {
        [JsonIgnore]
        public SerializedFile assetsFile;
        [JsonIgnore]
        public ObjectReader reader;
        [JsonIgnore]
        public long m_PathID;
        [JsonIgnore]
        public int[] version;
        [JsonIgnore]
        protected BuildType buildType;
        [JsonIgnore]
        public BuildTarget platform;
        [JsonIgnore]
        public ClassIDType type;
        [JsonIgnore]
        public SerializedType serializedType;
        [JsonIgnore]
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

            Logger.Verbose($"Attempting to read object {type} with {m_PathID} in file {assetsFile.fileName}, starting from offset 0x{reader.byteStart:X8} with size of 0x{byteSize:X8} !!");

            if (platform == BuildTarget.NoTarget)
            {
                var m_ObjectHideFlags = reader.ReadUInt32();
            }
        }

        /// <summary>
        /// The type tree of the object: from its file, else from the type tree database (files built without type trees).
        /// </summary>
        public TypeTree GetTypeTree()
        {
            if (serializedType?.m_Type?.m_Nodes?.Count > 0)
            {
                return serializedType.m_Type;
            }
            return TypeTreeDatabase.GetTypeTree((int)type, version, assetsFile?.buildType);
        }

        /// <summary>
        /// m_Name of an object of a named class that has no reader of its own (it is the first field).
        /// </summary>
        public string PeekName()
        {
            lock (reader.BaseStream)
            {
                try
                {
                    reader.Reset();
                    if (platform == BuildTarget.NoTarget)
                    {
                        reader.ReadUInt32(); //m_ObjectHideFlags
                    }
                    return reader.ReadAlignedString();
                }
                catch (System.Exception)
                {
                    return "";
                }
            }
        }

        public string Dump()
        {
            var m_Type = GetTypeTree();
            if (m_Type != null)
            {
                lock (reader.BaseStream)  // readers of one file share the stream
                {
                    return TypeTreeHelper.ReadTypeString(m_Type, reader);
                }
            }
            return null;
        }

        public string Dump(TypeTree m_Type)
        {
            if (m_Type != null)
            {
                lock (reader.BaseStream)  // readers of one file share the stream
                {
                    return TypeTreeHelper.ReadTypeString(m_Type, reader);
                }
            }
            return null;
        }

        public OrderedDictionary ToType()
        {
            var m_Type = GetTypeTree();
            if (m_Type != null)
            {
                lock (reader.BaseStream)  // readers of one file share the stream
                {
                    return TypeTreeHelper.ReadType(m_Type, reader);
                }
            }
            return null;
        }

        public OrderedDictionary ToType(TypeTree m_Type)
        {
            if (m_Type != null)
            {
                lock (reader.BaseStream)  // readers of one file share the stream
                {
                    return TypeTreeHelper.ReadType(m_Type, reader);
                }
            }
            return null;
        }

        public byte[] GetRawData()
        {
            Logger.Verbose($"Dumping raw bytes of the object with {m_PathID} in file {assetsFile.fileName}...");
            lock (reader.BaseStream)  // readers of one file share the stream
            {
                reader.Reset();
                return reader.ReadBytes((int)byteSize);
            }
        }
    }
}
