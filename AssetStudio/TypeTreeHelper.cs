using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Supplies the type tree of a [SerializeReference] class when the serialized file has none for it
    /// (e.g. type trees generated from assemblies).
    /// </summary>
    public delegate TypeTree RefTypeResolver(string className, string nameSpace, string assemblyName);

    public static class TypeTreeHelper
    {
        private sealed class Context
        {
            public SerializedFile AssetsFile;
            public RefTypeResolver Resolver;
            public bool InRefType;
            public bool HasManagedReferences;

            private Context refTypeContext;
            public Context ForRefType() => InRefType ? this : refTypeContext ??= new Context { AssetsFile = AssetsFile, Resolver = Resolver, InRefType = true };

            public static Context Create(List<TypeTreeNode> nodes, ObjectReader reader, RefTypeResolver resolver) => new Context
            {
                AssetsFile = reader.assetsFile,
                Resolver = resolver,
                HasManagedReferences = nodes.Exists(x => x.m_Type == "managedReference" || x.m_Type == "managedRefArrayItem"),
            };

            // Since 6000.7 (registry version 3) the type tree has no registry node: the registry is a frame
            // in front of the first script field, which is flagged in its TypeFlags.
            public bool IsRegistryFrameAt(TypeTreeNode node) => HasManagedReferences && !InRefType && node.m_Level == 1 && (node.m_TypeFlags & 0x10) != 0;
        }

        private sealed class RegistryV3Entry
        {
            public long Rid;
            public ManagedType Type;
            public int DataSize;
        }

        private sealed class ManagedType
        {
            public string ClassName;
            public string NameSpace;
            public string AssemblyName;

            public bool IsNull => string.IsNullOrEmpty(ClassName);
            public bool IsTerminus => ClassName == "Terminus" && NameSpace == "UnityEngine.DMAT" && AssemblyName == "FAKE_ASM";
            public override string ToString() => string.IsNullOrEmpty(NameSpace) ? $"{ClassName} ({AssemblyName})" : $"{NameSpace}.{ClassName} ({AssemblyName})";
        }

        public static string ReadTypeString(TypeTree m_Type, ObjectReader reader)
        {
            reader.Reset();
            var sb = new StringBuilder();
            var m_Nodes = m_Type.m_Nodes;
            var ctx = Context.Create(m_Nodes, reader, m_Type.m_RefTypeResolver);
            try
            {
                for (int i = 0; i < m_Nodes.Count; i++)
                {
                    ReadStringValue(sb, m_Nodes, reader, ctx, ref i);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error reading type {m_Nodes[0].m_Type}: {ex.Message}");
                sb.AppendLine($"<error: {ex.Message}>");
                return sb.ToString();
            }
            var readed = reader.Position - reader.byteStart;
            if (readed != reader.byteSize)
            {
                Logger.Info($"Error while read type, read {readed} bytes but expected {reader.byteSize} bytes");
            }
            return sb.ToString();
        }

        private static void ReadStringValue(StringBuilder sb, List<TypeTreeNode> m_Nodes, EndianBinaryReader reader, Context ctx, ref int i)
        {
            var m_Node = m_Nodes[i];
            var level = m_Node.m_Level;
            var varTypeStr = m_Node.m_Type;
            var varNameStr = m_Node.m_Name;
            object value = null;
            var append = true;
            var align = (m_Node.m_MetaFlag & 0x4000) != 0;
            switch (varTypeStr)
            {
                case "SInt8":
                    value = reader.ReadSByte();
                    break;
                case "UInt8":
                    value = reader.ReadByte();
                    break;
                case "char":
                    value = BitConverter.ToChar(reader.ReadBytes(2), 0);
                    break;
                case "short":
                case "SInt16":
                    value = reader.ReadInt16();
                    break;
                case "UInt16":
                case "unsigned short":
                    value = reader.ReadUInt16();
                    break;
                case "int":
                case "SInt32":
                    value = reader.ReadInt32();
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    value = reader.ReadUInt32();
                    break;
                case "long long":
                case "SInt64":
                    value = reader.ReadInt64();
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    value = reader.ReadUInt64();
                    break;
                case "float":
                    value = reader.ReadSingle();
                    break;
                case "double":
                    value = reader.ReadDouble();
                    break;
                case "bool":
                    value = reader.ReadBoolean();
                    break;
                case "string":
                    append = false;
                    var str = reader.ReadAlignedString();
                    sb.AppendFormat("{0}{1} {2} = \"{3}\"\r\n", (new string('\t', level)), varTypeStr, varNameStr, str);
                    var toSkip = GetNodes(m_Nodes, i);
                    i += toSkip.Count - 1;
                    break;
                case "map":
                    {
                        if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                            align = true;
                        append = false;
                        sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level)), varTypeStr, varNameStr);
                        sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level + 1)), "Array", "Array");
                        var size = reader.ReadInt32();
                        sb.AppendFormat("{0}{1} {2} = {3}\r\n", (new string('\t', level + 1)), "int", "size", size);
                        var map = GetNodes(m_Nodes, i);
                        i += map.Count - 1;
                        var first = GetNodes(map, 4);
                        var next = 4 + first.Count;
                        var second = GetNodes(map, next);
                        for (int j = 0; j < size; j++)
                        {
                            sb.AppendFormat("{0}[{1}]\r\n", (new string('\t', level + 2)), j);
                            sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level + 2)), "pair", "data");
                            int tmp1 = 0;
                            int tmp2 = 0;
                            ReadStringValue(sb, first, reader, ctx, ref tmp1);
                            ReadStringValue(sb, second, reader, ctx, ref tmp2);
                        }
                        break;
                    }
                case "TypelessData":
                    {
                        append = false;
                        var size = reader.ReadInt32();
                        reader.ReadBytes(size);
                        i += 2;
                        sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level)), varTypeStr, varNameStr);
                        sb.AppendFormat("{0}{1} {2} = {3}\r\n", (new string('\t', level)), "int", "size", size);
                        break;
                    }
                default:
                    {
                        if (i < m_Nodes.Count - 1 && m_Nodes[i + 1].m_Type == "Array") //Array
                        {
                            if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                                align = true;
                            append = false;
                            sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level)), varTypeStr, varNameStr);
                            sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level + 1)), "Array", "Array");
                            var size = reader.ReadInt32();
                            sb.AppendFormat("{0}{1} {2} = {3}\r\n", (new string('\t', level + 1)), "int", "size", size);
                            var vector = GetNodes(m_Nodes, i);
                            i += vector.Count - 1;
                            for (int j = 0; j < size; j++)
                            {
                                sb.AppendFormat("{0}[{1}]\r\n", (new string('\t', level + 2)), j);
                                int tmp = 3;
                                ReadStringValue(sb, vector, reader, ctx, ref tmp);
                            }
                            break;
                        }
                        else //Class
                        {
                            append = false;
                            sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level)), varTypeStr, varNameStr);
                            var @class = GetNodes(m_Nodes, i);
                            i += @class.Count - 1;
                            ReadStringClassMembers(sb, @class, reader, ctx);
                            break;
                        }
                    }
            }
            if (append)
                sb.AppendFormat("{0}{1} {2} = {3}\r\n", (new string('\t', level)), varTypeStr, varNameStr, value);
            if (align)
                reader.AlignStream();
        }

        private static void ReadStringClassMembers(StringBuilder sb, List<TypeTreeNode> @class, EndianBinaryReader reader, Context ctx)
        {
            if (IsRegistryV1(@class))
            {
                ReadStringRegistryV1(sb, @class, reader, ctx);
                return;
            }
            ManagedType managedType = null;
            for (int j = 1; j < @class.Count; j++)
            {
                var member = @class[j];
                if (member.m_Type == "ManagedReferencesRegistry" && ctx.InRefType)
                {
                    j += GetNodes(@class, j).Count - 1;
                    continue;
                }
                if (ctx.IsRegistryFrameAt(member))
                {
                    ReadStringRegistryV3(sb, member.m_Level, reader, ctx);
                }
                if (member.m_Type == "ReferencedManagedType")
                {
                    managedType = PeekManagedType(reader);
                }
                else if (member.m_Type == "ReferencedObjectData")
                {
                    j += GetNodes(@class, j).Count - 1;
                    var dataNodes = GetRefTypeNodes(ctx, managedType, member);
                    if (dataNodes == null)
                    {
                        sb.AppendFormat("{0}{1} {2} = null\r\n", new string('\t', member.m_Level), member.m_Type, member.m_Name);
                    }
                    else
                    {
                        int k = 0;
                        ReadStringValue(sb, dataNodes, reader, ctx.ForRefType(), ref k);
                    }
                    continue;
                }
                ReadStringValue(sb, @class, reader, ctx, ref j);
            }
        }

        // ManagedReferencesRegistry version 1 (2019.3 - 2021.1): the referenced objects follow each other
        // until a "Terminus" type, their ids are the positions in the list.
        private static void ReadStringRegistryV1(StringBuilder sb, List<TypeTreeNode> registry, EndianBinaryReader reader, Context ctx)
        {
            int j = 1;
            ReadStringValue(sb, registry, reader, ctx, ref j); // version
            var refObject = GetNodes(registry, j + 1);
            for (int n = 0; ; n++)
            {
                var managedType = PeekManagedType(reader);
                sb.AppendFormat("{0}[{1}]\r\n", new string('\t', refObject[0].m_Level), n);
                ReadStringClassMembers(sb, refObject, reader, ctx);
                if (managedType.IsTerminus)
                {
                    break;
                }
            }
        }

        private static void ReadStringRegistryV3(StringBuilder sb, int level, EndianBinaryReader reader, Context ctx)
        {
            string Indent(int n) => new string('\t', level + n);
            var version = reader.ReadInt32();
            sb.AppendFormat("{0}ManagedReferencesRegistry references\r\n", Indent(0));
            sb.AppendFormat("{0}int version = {1}\r\n", Indent(1), version);
            var entries = ReadRegistryV3Header(reader, out var dataStart, out var end);
            sb.AppendFormat("{0}vector RefIds\r\n", Indent(1));
            sb.AppendFormat("{0}Array Array\r\n", Indent(2));
            sb.AppendFormat("{0}int size = {1}\r\n", Indent(2), entries.Count);
            var position = dataStart;
            for (int n = 0; n < entries.Count; n++)
            {
                var entry = entries[n];
                sb.AppendFormat("{0}[{1}]\r\n", Indent(3), n);
                sb.AppendFormat("{0}ReferencedObject data\r\n", Indent(3));
                sb.AppendFormat("{0}SInt64 rid = {1}\r\n", Indent(4), entry.Rid);
                sb.AppendFormat("{0}ReferencedManagedType type\r\n", Indent(4));
                sb.AppendFormat("{0}string class = \"{1}\"\r\n", Indent(5), entry.Type?.ClassName);
                sb.AppendFormat("{0}string ns = \"{1}\"\r\n", Indent(5), entry.Type?.NameSpace);
                sb.AppendFormat("{0}string asm = \"{1}\"\r\n", Indent(5), entry.Type?.AssemblyName);
                var dataNodes = GetRefTypeNodes(ctx, entry.Type, new TypeTreeNode("ReferencedObjectData", "data", level + 4, false));
                if (dataNodes == null)
                {
                    sb.AppendFormat("{0}ReferencedObjectData data = null\r\n", Indent(4));
                }
                else
                {
                    reader.Position = position;
                    int k = 0;
                    ReadStringValue(sb, dataNodes, reader, ctx.ForRefType(), ref k);
                }
                position += entry.DataSize;
            }
            reader.Position = end;
        }

        private static OrderedDictionary ReadRegistryV3(int level, EndianBinaryReader reader, Context ctx)
        {
            var registry = new OrderedDictionary();
            registry["version"] = reader.ReadInt32();
            var entries = ReadRegistryV3Header(reader, out var dataStart, out var end);
            var refIds = new List<object>();
            var position = dataStart;
            foreach (var entry in entries)
            {
                var refObject = new OrderedDictionary();
                refObject["rid"] = entry.Rid;
                var type = new OrderedDictionary();
                type["class"] = entry.Type?.ClassName ?? "";
                type["ns"] = entry.Type?.NameSpace ?? "";
                type["asm"] = entry.Type?.AssemblyName ?? "";
                refObject["type"] = type;
                var dataNodes = GetRefTypeNodes(ctx, entry.Type, new TypeTreeNode("ReferencedObjectData", "data", level + 4, false));
                if (dataNodes == null)
                {
                    refObject["data"] = null;
                }
                else
                {
                    reader.Position = position;
                    int k = 0;
                    refObject["data"] = ReadValue(dataNodes, reader, ctx.ForRefType(), ref k);
                }
                position += entry.DataSize;
                refIds.Add(refObject);
            }
            registry["RefIds"] = refIds;
            reader.Position = end;
            return registry;
        }

        // int size; ReferencedManagedType[]; { SInt64 rid; int type; int dataSize }[]; then the data of each object
        private static List<RegistryV3Entry> ReadRegistryV3Header(EndianBinaryReader reader, out long dataStart, out long end)
        {
            var size = reader.ReadInt32();
            end = reader.Position + size;
            var typeCount = reader.ReadInt32();
            var types = new List<ManagedType>(typeCount);
            for (int n = 0; n < typeCount; n++)
            {
                types.Add(new ManagedType
                {
                    ClassName = reader.ReadAlignedString(),
                    NameSpace = reader.ReadAlignedString(),
                    AssemblyName = reader.ReadAlignedString(),
                });
            }
            var count = reader.ReadInt32();
            var entries = new List<RegistryV3Entry>(count);
            for (int n = 0; n < count; n++)
            {
                var rid = reader.ReadInt64();
                var typeIndex = reader.ReadInt32();
                var dataSize = reader.ReadInt32();
                entries.Add(new RegistryV3Entry { Rid = rid, Type = typeIndex >= 0 && typeIndex < types.Count ? types[typeIndex] : null, DataSize = dataSize });
            }
            dataStart = reader.Position;
            return entries;
        }

        public static OrderedDictionary ReadType(TypeTree m_Types, ObjectReader reader)
        {
            reader.Reset();
            var obj = new OrderedDictionary();
            var m_Nodes = m_Types.m_Nodes;
            var ctx = Context.Create(m_Nodes, reader, m_Types.m_RefTypeResolver);
            for (int i = 1; i < m_Nodes.Count; i++)
            {
                var m_Node = m_Nodes[i];
                var varNameStr = m_Node.m_Name;
                try
                {
                    if (ctx.IsRegistryFrameAt(m_Node))
                    {
                        obj["references"] = ReadRegistryV3(m_Node.m_Level, reader, ctx);
                    }
                    obj[varNameStr] = ReadValue(m_Nodes, reader, ctx, ref i);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Error reading field '{varNameStr}': {ex.Message}");
                    // Store null for failed fields and continue
                    obj[varNameStr] = null;
                    break; // Stop processing further fields as we're out of sync
                }
            }
            var readed = reader.Position - reader.byteStart;
            if (readed != reader.byteSize)
            {
                Logger.Info($"Error while read type, read {readed} bytes but expected {reader.byteSize} bytes");
            }
            return obj;
        }

        private static object ReadValue(List<TypeTreeNode> m_Nodes, EndianBinaryReader reader, Context ctx, ref int i)
        {
            var m_Node = m_Nodes[i];
            var varTypeStr = m_Node.m_Type;
            Logger.Verbose($"Reading {m_Node.m_Name} of type {varTypeStr}");
            object value;
            var align = (m_Node.m_MetaFlag & 0x4000) != 0;
            switch (varTypeStr)
            {
                case "SInt8":
                    value = reader.ReadSByte();
                    break;
                case "UInt8":
                    value = reader.ReadByte();
                    break;
                case "char":
                    value = BitConverter.ToChar(reader.ReadBytes(2), 0);
                    break;
                case "short":
                case "SInt16":
                    value = reader.ReadInt16();
                    break;
                case "UInt16":
                case "unsigned short":
                    value = reader.ReadUInt16();
                    break;
                case "int":
                case "SInt32":
                    value = reader.ReadInt32();
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    value = reader.ReadUInt32();
                    break;
                case "long long":
                case "SInt64":
                    value = reader.ReadInt64();
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    value = reader.ReadUInt64();
                    break;
                case "float":
                    value = reader.ReadSingle();
                    break;
                case "double":
                    value = reader.ReadDouble();
                    break;
                case "bool":
                    value = reader.ReadBoolean();
                    break;
                case "string":
                    value = reader.ReadAlignedString();
                    var toSkip = GetNodes(m_Nodes, i);
                    i += toSkip.Count - 1;
                    break;
                case "map":
                    {
                        if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                            align = true;
                        var map = GetNodes(m_Nodes, i);
                        i += map.Count - 1;
                        var first = GetNodes(map, 4);
                        var next = 4 + first.Count;
                        var second = GetNodes(map, next);
                        var size = reader.ReadInt32();
                        var dic = new List<KeyValuePair<object, object>>();
                        for (int j = 0; j < size; j++)
                        {
                            int tmp1 = 0;
                            int tmp2 = 0;
                            dic.Add(new KeyValuePair<object, object>(ReadValue(first, reader, ctx, ref tmp1), ReadValue(second, reader, ctx, ref tmp2)));
                        }
                        value = dic;
                        break;
                    }
                case "TypelessData":
                    {
                        var size = reader.ReadInt32();
                        value = reader.ReadBytes(size);
                        i += 2;
                        break;
                    }
                default:
                    {
                        if (i < m_Nodes.Count - 1 && m_Nodes[i + 1].m_Type == "Array") //Array
                        {
                            if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                                align = true;
                            var vector = GetNodes(m_Nodes, i);
                            i += vector.Count - 1;
                            var size = reader.ReadInt32();
                            var list = new List<object>();
                            for (int j = 0; j < size; j++)
                            {
                                int tmp = 3;
                                list.Add(ReadValue(vector, reader, ctx, ref tmp));
                            }
                            value = list;
                            break;
                        }
                        else //Class
                        {
                            var @class = GetNodes(m_Nodes, i);
                            i += @class.Count - 1;
                            value = ReadClassMembers(@class, reader, ctx);
                            break;
                        }
                    }
            }
            if (align)
                reader.AlignStream();
            return value;
        }

        private static OrderedDictionary ReadClassMembers(List<TypeTreeNode> @class, EndianBinaryReader reader, Context ctx)
        {
            var obj = new OrderedDictionary();
            if (IsRegistryV1(@class))
            {
                int v = 1;
                obj[@class[1].m_Name] = ReadValue(@class, reader, ctx, ref v); // version
                var refObject = GetNodes(@class, v + 1);
                var refIds = new List<object>();
                ManagedType managedType;
                do
                {
                    managedType = PeekManagedType(reader);
                    refIds.Add(ReadClassMembers(refObject, reader, ctx));
                } while (!managedType.IsTerminus);
                obj["RefIds"] = refIds;
                return obj;
            }
            ManagedType refType = null;
            for (int j = 1; j < @class.Count; j++)
            {
                var member = @class[j];
                if (member.m_Type == "ManagedReferencesRegistry" && ctx.InRefType)
                {
                    j += GetNodes(@class, j).Count - 1;
                    continue;
                }
                if (ctx.IsRegistryFrameAt(member))
                {
                    obj["references"] = ReadRegistryV3(member.m_Level, reader, ctx);
                }
                if (member.m_Type == "ReferencedManagedType")
                {
                    refType = PeekManagedType(reader);
                }
                else if (member.m_Type == "ReferencedObjectData")
                {
                    j += GetNodes(@class, j).Count - 1;
                    var dataNodes = GetRefTypeNodes(ctx, refType, member);
                    if (dataNodes == null)
                    {
                        obj[member.m_Name] = null;
                    }
                    else
                    {
                        int k = 0;
                        obj[member.m_Name] = ReadValue(dataNodes, reader, ctx.ForRefType(), ref k);
                    }
                    continue;
                }
                obj[member.m_Name] = ReadValue(@class, reader, ctx, ref j);
            }
            return obj;
        }

        private static bool IsRegistryV1(List<TypeTreeNode> @class)
        {
            if (@class[0].m_Type != "ManagedReferencesRegistry" || @class.Count < 3)
                return false;
            var level = @class[0].m_Level + 1;
            var members = @class.FindAll(x => x.m_Level == level);
            return members.Count == 2 && members[1].m_Type == "ReferencedObject";
        }

        // ReferencedManagedType is { string class; string ns; string asm }, read it ahead to know the type of the data that follows.
        private static ManagedType PeekManagedType(EndianBinaryReader reader)
        {
            var position = reader.Position;
            var managedType = new ManagedType
            {
                ClassName = reader.ReadAlignedString(),
                NameSpace = reader.ReadAlignedString(),
                AssemblyName = reader.ReadAlignedString(),
            };
            reader.Position = position;
            return managedType;
        }

        // The type tree of the referenced class, re-rooted at the ReferencedObjectData node. Null for a null reference.
        private static List<TypeTreeNode> GetRefTypeNodes(Context ctx, ManagedType managedType, TypeTreeNode dataNode)
        {
            if (managedType == null || managedType.IsNull || managedType.IsTerminus)
                return null;
            TypeTree typeTree = null;
            var refTypes = ctx.AssetsFile?.m_RefTypes;
            if (refTypes != null)
            {
                foreach (var refType in refTypes)
                {
                    if (refType.m_Type?.m_Nodes?.Count > 0 && refType.m_KlassName == managedType.ClassName && refType.m_NameSpace == managedType.NameSpace && refType.m_AsmName == managedType.AssemblyName)
                    {
                        typeTree = refType.m_Type;
                        break;
                    }
                }
            }
            typeTree ??= ctx.Resolver?.Invoke(managedType.ClassName, managedType.NameSpace, managedType.AssemblyName);
            if (typeTree?.m_Nodes == null || typeTree.m_Nodes.Count == 0)
            {
                throw new Exception($"No type tree for the referenced type {managedType}");
            }
            var source = typeTree.m_Nodes;
            var shift = dataNode.m_Level - source[0].m_Level;
            var nodes = new List<TypeTreeNode>(source.Count)
            {
                new TypeTreeNode(source[0].m_Type, dataNode.m_Name, dataNode.m_Level, false) { m_MetaFlag = dataNode.m_MetaFlag }
            };
            for (int i = 1; i < source.Count; i++)
            {
                var node = source[i];
                nodes.Add(new TypeTreeNode(node.m_Type, node.m_Name, node.m_Level + shift, false) { m_MetaFlag = node.m_MetaFlag });
            }
            return nodes;
        }

        private static List<TypeTreeNode> GetNodes(List<TypeTreeNode> m_Nodes, int index)
        {
            var nodes = new List<TypeTreeNode>();
            nodes.Add(m_Nodes[index]);
            var level = m_Nodes[index].m_Level;
            for (int i = index + 1; i < m_Nodes.Count; i++)
            {
                var member = m_Nodes[i];
                var level2 = member.m_Level;
                if (level2 <= level)
                {
                    return nodes;
                }
                nodes.Add(member);
            }
            return nodes;
        }
    }
}
