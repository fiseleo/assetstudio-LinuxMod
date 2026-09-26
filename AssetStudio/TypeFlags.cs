using System;
using System.Collections.Generic;

namespace AssetStudio;
public static class TypeFlags
{
    private static Dictionary<ClassIDType, (bool, bool)> Types;

    // Types supported after the type settings were first saved: settings from older versions don't list them
    private static readonly Dictionary<ClassIDType, (bool, bool)> AddedTypes = new()
    {
        { ClassIDType.Cubemap, (true, true) },
        { ClassIDType.Texture2DArray, (true, true) },
        { ClassIDType.Texture3D, (true, true) },
        { ClassIDType.CubemapArray, (true, true) },
    };

    /// <summary>
    /// Adds the types that settings saved by an older version don't know, with their defaults.
    /// </summary>
    public static Dictionary<ClassIDType, (bool, bool)> WithAddedTypes(Dictionary<ClassIDType, (bool, bool)> types)
    {
        types ??= new Dictionary<ClassIDType, (bool, bool)>();
        foreach (var (type, flags) in AddedTypes)
        {
            types.TryAdd(type, flags);
        }
        return types;
    }

    public static void SetTypes(Dictionary<ClassIDType, (bool, bool)> types)
    {
        Types = WithAddedTypes(types);
    }

    public static void SetType(ClassIDType type, bool parse, bool export)
    {
        Types ??= new Dictionary<ClassIDType, (bool, bool)>();
        Types[type] = (parse, export);
    }

    public static bool CanParse(this ClassIDType type)
    {
        if (Types == null)
        {
            return true;
        }
        else if (Types.TryGetValue(type, out var param))
        {
            return param.Item1;
        }

        return false;
    }

    public static bool CanExport(this ClassIDType type)
    {
        if (Types == null)
        {
            return true;
        }
        else if (Types.TryGetValue(type, out var param))
        {
            return param.Item2;
        }

        return false;
    }
}

[Flags]
public enum TypeFlag
{
    None,
    Parse,
    Export,
    Both = Parse | Export,
}