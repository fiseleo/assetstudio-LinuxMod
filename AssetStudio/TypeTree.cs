using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio
{
    public class TypeTree
    {
        public List<TypeTreeNode> m_Nodes;
        public byte[] m_StringBuffer;
        /// <summary>Type trees of [SerializeReference] classes, for type trees built without a serialized file (from assemblies).</summary>
        public RefTypeResolver m_RefTypeResolver;
    }
}
