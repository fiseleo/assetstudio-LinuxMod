using System.Text;

namespace AssetStudio.Avalonia
{
    public class TypeTreeItem
    {
        private readonly TypeTree m_Type;

        public string Version { get; }
        public int TypeID { get; }
        public string Text { get; }

        public TypeTreeItem(string version, int typeID, TypeTree m_Type)
        {
            this.m_Type = m_Type;
            Version = version;
            TypeID = typeID;
            Text = m_Type.m_Nodes[0].m_Type + " " + m_Type.m_Nodes[0].m_Name;
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            foreach (var i in m_Type.m_Nodes)
            {
                sb.AppendFormat("{0}{1} {2} {3} {4}\n", new string('\t', i.m_Level), i.m_Type, i.m_Name, i.m_ByteSize, (i.m_MetaFlag & 0x4000) != 0);
            }
            return sb.ToString();
        }
    }
}
