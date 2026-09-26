namespace AssetStudio
{
    /// <summary>
    /// m_CubemapCount cubemaps: their faces one after the other in the image data, each with its mipmaps.
    /// </summary>
    public sealed class CubemapArray : Texture
    {
        public int m_ColorSpace;
        public GraphicsFormat m_GraphicsFormat; //2019.1 and up
        public TextureFormat m_TextureFormat;
        public int m_Width;
        public int m_CubemapCount;
        public int m_MipCount;
        public uint m_DataSize;
        public GLTextureSettings m_TextureSettings;
        public ResourceReader image_data;
        public StreamingInfo m_StreamData;

        public CubemapArray(ObjectReader reader) : base(reader)
        {
            if (version[0] >= 2019) //2019.1 and up
            {
                m_ColorSpace = reader.ReadInt32();
                m_GraphicsFormat = (GraphicsFormat)reader.ReadInt32();
                m_TextureFormat = m_GraphicsFormat.ToTextureFormat();
                m_Width = reader.ReadInt32();
                m_CubemapCount = reader.ReadInt32();
                m_MipCount = reader.ReadInt32();
                m_DataSize = reader.ReadUInt32();
                m_TextureSettings = new GLTextureSettings(reader);
                if (HasField(reader, "m_UsageMode", version[0] > 2020 || (version[0] == 2020 && version[1] >= 2))) //2020.2 and up
                {
                    var m_UsageMode = reader.ReadInt32();
                }
                var m_IsReadable = reader.ReadBoolean();
                reader.AlignStream();
            }
            else
            {
                m_Width = reader.ReadInt32();
                m_CubemapCount = reader.ReadInt32();
                m_TextureFormat = (TextureFormat)reader.ReadInt32();
                m_MipCount = reader.ReadInt32();
                m_DataSize = reader.ReadUInt32();
                m_TextureSettings = new GLTextureSettings(reader);
                m_ColorSpace = reader.ReadInt32();
                var m_IsReadable = reader.ReadBoolean();
                reader.AlignStream();
            }
            image_data = ReadImageData(reader, out m_StreamData);
        }
    }
}
