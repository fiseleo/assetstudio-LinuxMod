using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public abstract class Texture : NamedObject
    {
        protected Texture(ObjectReader reader) : base(reader)
        {
            if (version[0] > 2017 || (version[0] == 2017 && version[1] >= 3)) //2017.3 and up
            {
                if (version[0] < 2023 || (version[0] == 2023 && version[1] < 2)) //2017.3 to 2023.1
                {
                    var m_ForcedFallbackFormat = reader.ReadInt32();
                    var m_DownscaleFallback = reader.ReadBoolean();
                }
                if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) //2020.2 and up
                {
                    var m_IsAlphaChannelOptional = reader.ReadBoolean();
                }
                reader.AlignStream();
            }
        }

        /// <summary>
        /// Whether the object has a field: from the type tree when there is one (it also covers alpha versions), else from the version.
        /// </summary>
        protected static bool HasField(ObjectReader reader, string fieldName, bool byVersion)
        {
            var nodes = reader.serializedType?.m_Type?.m_Nodes;
            if (nodes == null || nodes.Count == 0)
                return byVersion;
            return nodes.Exists(x => x.m_Level == 1 && x.m_Name == fieldName);
        }

        protected ResourceReader ReadImageData(ObjectReader reader, out StreamingInfo streamData)
        {
            var image_data_size = reader.ReadInt32();
            var offset = reader.BaseStream.Position;
            reader.Position += image_data_size;
            reader.AlignStream();
            streamData = null;
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 6)) //5.6 and up
            {
                streamData = new StreamingInfo(reader);
            }
            if (!string.IsNullOrEmpty(streamData?.path))
            {
                return new ResourceReader(streamData.path, assetsFile, streamData.offset, streamData.size);
            }
            return new ResourceReader(reader, offset, image_data_size);
        }
    }
}
