using System.Collections.Generic;

namespace AssetStudio
{
    /// <summary>
    /// A Texture2D with six faces (+X, -X, +Y, -Y, +Z, -Z) one after the other in the image data, each with its mipmaps.
    /// </summary>
    public sealed class Cubemap : Texture2D
    {
        public List<PPtr<Texture2D>> m_SourceTextures;

        public Cubemap(ObjectReader reader) : base(reader)
        {
            if (m_ImageDataEnd > 0) //inline image data, the base class stops in front of it
            {
                reader.Position = m_ImageDataEnd;
                reader.AlignStream();
                if (version[0] > 5 || (version[0] == 5 && version[1] >= 3)) //5.3 and up
                {
                    var m_StreamData = new StreamingInfo(reader);
                }
            }
            m_SourceTextures = new List<PPtr<Texture2D>>();
            if (version[0] >= 4) //4.0 and up
            {
                var m_SourceTexturesSize = reader.ReadInt32();
                for (int i = 0; i < m_SourceTexturesSize; i++)
                {
                    m_SourceTextures.Add(new PPtr<Texture2D>(reader));
                }
            }
        }
    }
}
