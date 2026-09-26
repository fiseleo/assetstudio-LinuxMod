namespace AssetStudio
{
    /// <summary>
    /// A volume: the m_Depth slices of each mip level are one after the other, mip level 0 first.
    /// </summary>
    public sealed class Texture3D : Texture
    {
        public int m_ColorSpace;
        public GraphicsFormat m_GraphicsFormat; //2019.1 and up
        public TextureFormat m_TextureFormat;
        public int m_Width;
        public int m_Height;
        public int m_Depth;
        public int m_MipCount;
        public uint m_DataSize;
        public GLTextureSettings m_TextureSettings;
        public ResourceReader image_data;
        public StreamingInfo m_StreamData;

        public Texture3D(ObjectReader reader) : base(reader)
        {
            if (version[0] >= 2019) //2019.1 and up
            {
                m_ColorSpace = reader.ReadInt32();
                m_GraphicsFormat = (GraphicsFormat)reader.ReadInt32();
                m_TextureFormat = m_GraphicsFormat.ToTextureFormat();
                m_Width = reader.ReadInt32();
                m_Height = reader.ReadInt32();
                m_Depth = reader.ReadInt32();
                m_MipCount = reader.ReadInt32();
                reader.AlignStream();
                m_DataSize = reader.ReadUInt32();
                m_TextureSettings = new GLTextureSettings(reader);
                if (HasField(reader, "m_UsageMode", version[0] > 2020 || (version[0] == 2020 && version[1] >= 2))) //2020.2 and up
                {
                    var m_UsageMode = reader.ReadInt32();
                }
                var m_IsReadable = reader.ReadBoolean();
                reader.AlignStream();
            }
            else //4.0 to 2018.4
            {
                m_Width = reader.ReadInt32();
                m_Height = reader.ReadInt32();
                m_Depth = reader.ReadInt32();
                m_TextureFormat = (TextureFormat)reader.ReadInt32();
                if (version[0] < 5 || (version[0] == 5 && version[1] < 2)) //5.2 down
                {
                    var m_MipMap = reader.ReadBoolean();
                    m_MipCount = m_MipMap ? 0 : 1;
                }
                else
                {
                    m_MipCount = reader.ReadInt32();
                }
                reader.AlignStream();
                m_DataSize = reader.ReadUInt32();
                m_TextureSettings = new GLTextureSettings(reader);
                if (version[0] > 5 || (version[0] == 5 && version[1] >= 4)) //5.4 and up
                {
                    var m_IsReadable = reader.ReadBoolean();
                    reader.AlignStream();
                }
            }
            image_data = ReadImageData(reader, out m_StreamData);
        }
    }
}
