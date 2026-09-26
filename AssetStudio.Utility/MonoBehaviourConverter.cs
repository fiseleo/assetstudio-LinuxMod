using System.Collections.Generic;
using System.Linq;

namespace AssetStudio
{
    public static class MonoBehaviourConverter
    {
        public static TypeTree ConvertToTypeTree(this MonoBehaviour m_MonoBehaviour, AssemblyLoader assemblyLoader)
        {
            var m_Type = new TypeTree();
            m_Type.m_Nodes = new List<TypeTreeNode>();
            var helper = new SerializedTypeHelper(m_MonoBehaviour.version);
            helper.AddMonoBehaviour(m_Type.m_Nodes, 0);
            if (m_MonoBehaviour.m_Script.TryGet(out var m_Script))
            {
                var typeDef = assemblyLoader.GetTypeDefinition(m_Script.m_AssemblyName, string.IsNullOrEmpty(m_Script.m_Namespace) ? m_Script.m_ClassName : $"{m_Script.m_Namespace}.{m_Script.m_ClassName}");
                if (typeDef != null)
                {
                    var baseCount = m_Type.m_Nodes.Count;
                    var typeDefinitionConverter = new TypeDefinitionConverter(typeDef, helper, 1);
                    m_Type.m_Nodes.AddRange(typeDefinitionConverter.ConvertToTypeTreeNodes());
                    AddManagedReferences(m_Type, helper, baseCount, assemblyLoader);
                }
            }
            return m_Type;
        }

        private static void AddManagedReferences(TypeTree m_Type, SerializedTypeHelper helper, int firstScriptNode, AssemblyLoader assemblyLoader)
        {
            if (!m_Type.m_Nodes.Any(x => x.m_Type == "managedReference" || x.m_Type == "managedRefArrayItem"))
                return;
            switch (helper.ManagedReferencesVersion)
            {
                case 2:
                    helper.AddManagedReferencesRegistry(m_Type.m_Nodes, 1);
                    break;
                case 3:
                    m_Type.m_Nodes[firstScriptNode].m_TypeFlags |= 0x10;
                    break;
            }
            m_Type.m_RefTypeResolver = (className, nameSpace, assemblyName) =>
            {
                var fullName = string.IsNullOrEmpty(nameSpace) ? className : $"{nameSpace}.{className}";
                var typeDef = assemblyLoader.GetTypeDefinition(assemblyName, fullName);
                if (typeDef == null)
                    return null;
                var refType = new TypeTree { m_Nodes = new List<TypeTreeNode> { new TypeTreeNode(typeDef.Name, "Base", 0, false) } };
                refType.m_Nodes.AddRange(new TypeDefinitionConverter(typeDef, helper, 1).ConvertToTypeTreeNodes());
                return refType;
            };
        }
    }
}
