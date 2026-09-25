using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public sealed class MeshRenderer : Renderer
    {
        public PPtr<Mesh> m_AdditionalVertexStreams;
        public MeshRenderer(ObjectReader reader) : base(reader)
        {
            // not serialized in Unity 4.x (the object ends after the Renderer fields there)
            if (version[0] >= 5 && reader.Position - reader.byteStart < reader.byteSize)
            {
                m_AdditionalVertexStreams = new PPtr<Mesh>(reader);
            }
        }
    }
}
