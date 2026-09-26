using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudio.Avalonia
{
    internal static class Exporter
    {
        public static bool ExportTexture2D(AssetItem item, string exportPath)
        {
            var m_Texture2D = (Texture2D)item.Asset;
            if (Settings.Default.convertTexture)
            {
                var type = Settings.Default.convertType;
                if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                    return false;
                var image = m_Texture2D.ConvertToImage(true);
                if (image == null)
                    return false;
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    return true;
                }
            }
            else
            {
                if (!TryExportFile(exportPath, item, ".tex", out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Texture2D.image_data.GetData());
                return true;
            }
        }

        // Cubemap: one horizontal cross. Texture2DArray, Texture3D: a folder with an image per slice. CubemapArray: a folder with a cross per cube.
        public static bool ExportTextureImages(AssetItem item, string exportPath)
        {
            var m_Texture = (Texture)item.Asset;
            if (!Settings.Default.convertTexture)
            {
                if (!TryExportFile(exportPath, item, ".tex", out var rawPath))
                    return false;
                File.WriteAllBytes(rawPath, m_Texture.GetImageData().GetData());
                return true;
            }
            var type = Settings.Default.convertType;
            var extension = "." + type.ToString().ToLower();
            if (m_Texture is Cubemap)
            {
                if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                    return false;
                var (_, cross) = m_Texture.ConvertToExportImages(true).FirstOrDefault();
                if (cross == null)
                    return false;
                using (cross)
                using (var file = File.OpenWrite(exportFullPath))
                {
                    cross.WriteToStream(file, type);
                }
                return true;
            }
            if (!TryExportFolder(exportPath, item, out var folder))
                return false;
            var exported = false;
            foreach (var (index, image) in m_Texture.ConvertToExportImages(true))
            {
                using (image)
                {
                    Directory.CreateDirectory(folder);
                    using (var file = File.OpenWrite(Path.Combine(folder, index + extension)))
                    {
                        image.WriteToStream(file, type);
                    }
                    exported = true;
                }
            }
            return exported;
        }

        public static bool ExportAudioClip(AssetItem item, string exportPath)
        {
            var m_AudioClip = (AudioClip)item.Asset;
            var m_AudioData = m_AudioClip.m_AudioData.GetData();
            if (m_AudioData == null || m_AudioData.Length == 0)
                return false;
            var converter = new AudioClipConverter(m_AudioClip);
            if (Settings.Default.convertAudio && converter.IsSupport && NativeLibraries.FmodAvailable)
            {
                if (!TryExportFile(exportPath, item, ".wav", out var exportFullPath))
                    return false;
                var buffer = converter.ConvertToWav();
                if (buffer == null)
                    return false;
                File.WriteAllBytes(exportFullPath, buffer);
            }
            else if (Settings.Default.convertAudio && Fsb5Decoder.TryConvert(m_AudioClip, out var converted, out var extension))
            {
                if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, converted);
            }
            else
            {
                if (!TryExportFile(exportPath, item, converter.GetExtensionName(), out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_AudioData);
            }
            return true;
        }

        public static bool ExportShader(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".shader", out var exportFullPath))
                return false;
            var m_Shader = (Shader)item.Asset;
            var str = m_Shader.Convert();
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static bool ExportTextAsset(AssetItem item, string exportPath)
        {
            var m_TextAsset = (TextAsset)(item.Asset);
            var extension = ".txt";
            if (Settings.Default.restoreExtensionName)
            {
                if (!string.IsNullOrEmpty(item.Container))
                {
                    extension = Path.GetExtension(item.Container);
                }
            }
            if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_TextAsset.m_Script);
            return true;
        }

        public static bool ExportMonoBehaviour(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                return false;
            var m_MonoBehaviour = (MonoBehaviour)item.Asset;
            var type = m_MonoBehaviour.ToType();
            if (type == null)
            {
                var m_Type = Studio.MonoBehaviourToTypeTree(m_MonoBehaviour);
                type = m_MonoBehaviour.ToType(m_Type);
            }
            var str = JsonConvert.SerializeObject(type, Formatting.Indented);
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static bool ExportMiHoYoBinData(AssetItem item, string exportPath)
        {
            string exportFullPath;
            if (item.Asset is MiHoYoBinData m_MiHoYoBinData)
            {
                switch (m_MiHoYoBinData.Type)
                {
                    case MiHoYoBinDataType.JSON:
                        
                        if (!TryExportFile(exportPath, item, ".json", out exportFullPath))
                            return false;
                        var json = m_MiHoYoBinData.Dump() as string;
                        if (json.Length != 0)
                        {
                            File.WriteAllText(exportFullPath, json);
                            return true;
                        }
                        break;
                    case MiHoYoBinDataType.Bytes:
                        var extension = ".bin";
                        if (Settings.Default.restoreExtensionName)
                        {
                            if (!string.IsNullOrEmpty(item.Container))
                            {
                                extension = Path.GetExtension(item.Container);
                            }
                        }
                        if (!TryExportFile(exportPath, item, extension, out exportFullPath))
                            return false;
                        var bytes = m_MiHoYoBinData.Dump() as byte[];
                        if (!bytes.IsNullOrEmpty())
                        {
                            File.WriteAllBytes(exportFullPath, bytes);
                            return true;
                        }
                        break;
                }
            }
            return false;
        }

        public static bool ExportFont(AssetItem item, string exportPath)
        {
            var m_Font = (Font)item.Asset;
            if (m_Font.m_FontData != null)
            {
                var extension = ".ttf";
                if (m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79)
                {
                    extension = ".otf";
                }
                if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Font.m_FontData);
                return true;
            }
            return false;
        }

        public static bool ExportMesh(AssetItem item, string exportPath)
        {
            var m_Mesh = (Mesh)item.Asset;
            if (m_Mesh.m_VertexCount <= 0)
                return false;
            if (!TryExportFile(exportPath, item, ".obj", out var exportFullPath))
                return false;
            var sb = new StringBuilder();
            sb.AppendLine("g " + m_Mesh.m_Name);
            #region Vertices
            if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
            {
                return false;
            }
            int c = 3;
            if (m_Mesh.m_Vertices.Length == m_Mesh.m_VertexCount * 4)
            {
                c = 4;
            }
            for (int v = 0; v < m_Mesh.m_VertexCount; v++)
            {
                sb.AppendFormat("v {0} {1} {2}\r\n", -m_Mesh.m_Vertices[v * c], m_Mesh.m_Vertices[v * c + 1], m_Mesh.m_Vertices[v * c + 2]);
            }
            #endregion

            #region UV
            if (m_Mesh.m_UV0?.Length > 0)
            {
                c = 4;
                if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 2)
                {
                    c = 2;
                }
                else if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 3)
                {
                    c = 3;
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat("vt {0} {1}\r\n", m_Mesh.m_UV0[v * c], m_Mesh.m_UV0[v * c + 1]);
                }
            }
            #endregion

            #region Normals
            if (m_Mesh.m_Normals?.Length > 0)
            {
                if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 3)
                {
                    c = 3;
                }
                else if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 4)
                {
                    c = 4;
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat("vn {0} {1} {2}\r\n", -m_Mesh.m_Normals[v * c], m_Mesh.m_Normals[v * c + 1], m_Mesh.m_Normals[v * c + 2]);
                }
            }
            #endregion

            #region Face
            int sum = 0;
            for (var i = 0; i < m_Mesh.m_SubMeshes.Count; i++)
            {
                sb.AppendLine($"g {m_Mesh.m_Name}_{i}");
                int indexCount = (int)m_Mesh.m_SubMeshes[i].indexCount;
                var end = sum + indexCount / 3;
                for (int f = sum; f < end; f++)
                {
                    sb.AppendFormat("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\r\n", m_Mesh.m_Indices[f * 3 + 2] + 1, m_Mesh.m_Indices[f * 3 + 1] + 1, m_Mesh.m_Indices[f * 3] + 1);
                }
                sum = end;
            }
            #endregion

            sb.Replace("NaN", "0");
            File.WriteAllText(exportFullPath, sb.ToString());
            return true;
        }

        public static bool ExportVideoClip(AssetItem item, string exportPath)
        {
            var m_VideoClip = (VideoClip)item.Asset;
            if (m_VideoClip.m_ExternalResources.m_Size > 0)
            {
                if (!TryExportFile(exportPath, item, Path.GetExtension(m_VideoClip.m_OriginalPath), out var exportFullPath))
                    return false;
                m_VideoClip.m_VideoData.WriteData(exportFullPath);
                return true;
            }
            return false;
        }

        public static bool ExportMovieTexture(AssetItem item, string exportPath)
        {
            var m_MovieTexture = (MovieTexture)item.Asset;
            if (!TryExportFile(exportPath, item, ".ogv", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_MovieTexture.m_MovieData);
            return true;
        }

        public static bool ExportSprite(AssetItem item, string exportPath)
        {
            var type = Settings.Default.convertType;
            if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                return false;
            var image = ((Sprite)item.Asset).GetImage();
            if (image != null)
            {
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    return true;
                }
            }
            return false;
        }

        public static bool ExportRawFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".dat", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, item.Asset.GetRawData());
            return true;
        }

        private static bool TryExportFile(string dir, AssetItem item, string extension, out string fullPath)
        {
            var fileName = FixFileName(item.Text);
            fullPath = Path.Combine(dir, $"{fileName}{extension}");
            if (!File.Exists(fullPath))
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            if (Settings.Default.allowDuplicates)
            {
                for (int i = 1; i < int.MaxValue; i++)
                {
                    fullPath = Path.Combine(dir, $"{fileName} ({i}){extension}");
                    if (!File.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        private static bool TryExportFolder(string dir, AssetItem item, out string fullPath)
        {
            var fileName = FixFileName(item.Text);
            fullPath = Path.Combine(dir, fileName);
            if (!Directory.Exists(fullPath))
            {
                return true;
            }
            if (Settings.Default.allowDuplicates)
            {
                for (int i = 1; i < int.MaxValue; i++)
                {
                    fullPath = Path.Combine(dir, $"{fileName} ({i})");
                    if (!Directory.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        public static bool ExportAnimationClip(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".anim", out var exportFullPath))
                return false;
            var m_AnimationClip = (AnimationClip)item.Asset;
            var str = m_AnimationClip.Convert();
            if (string.IsNullOrEmpty(str))
                return false;
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static bool ExportAnimator(AssetItem item, string exportPath, List<AssetItem> animationList = null)
        {
            if (!TryExportFolder(exportPath, item, out var exportFullPath))
                return false;

            exportFullPath = Path.Combine(exportFullPath, item.Text + ".fbx");
            var m_Animator = (Animator)item.Asset;
            var options = new ModelConverter.Options()
            {
                imageFormat = ModelExporter.GetTextureFormat((ModelFormat)Settings.Default.modelFormat, Settings.Default.convertType),
                game = Studio.Game,
                collectAnimations = Settings.Default.collectAnimations,
                exportMaterials = Settings.Default.exportMaterials,
                materials = new HashSet<Material>(),
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(Settings.Default.uvs),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(Settings.Default.texs),
            };
            var convert = animationList != null
                ? new ModelConverter(m_Animator, options, animationList.Select(x => (AnimationClip)x.Asset).ToArray())
                : new ModelConverter(m_Animator, options);
            if (options.exportMaterials)
            {
                var materialExportPath = Path.Combine(Path.GetDirectoryName(exportFullPath), "Materials");
                Directory.CreateDirectory(materialExportPath);
                foreach (var material in options.materials)
                {
                    var matItem = new AssetItem(material);
                    ExportJSONFile(matItem, materialExportPath);
                }
            }
            ExportFbx(convert, exportFullPath);
            return true;
        }

        public static bool ExportGameObject(AssetItem item, string exportPath, List <AssetItem> animationList = null)
        {
            if (!TryExportFolder(exportPath, item, out var exportFullPath))
                return false;

            var m_GameObject = (GameObject)item.Asset;
            return ExportGameObject(m_GameObject, exportFullPath + Path.DirectorySeparatorChar, animationList);
        }

        public static bool ExportGameObject(GameObject gameObject, string exportPath, List<AssetItem> animationList = null)
        {
            var options = new ModelConverter.Options()
            {
                imageFormat = ModelExporter.GetTextureFormat((ModelFormat)Settings.Default.modelFormat, Settings.Default.convertType),
                game = Studio.Game,
                collectAnimations = Settings.Default.collectAnimations,
                exportMaterials = Settings.Default.exportMaterials,
                materials = new HashSet<Material>(),
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(Settings.Default.uvs),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(Settings.Default.texs),
            };
            var convert = animationList != null
                ? new ModelConverter(gameObject, options, animationList.Select(x => (AnimationClip)x.Asset).ToArray())
                : new ModelConverter(gameObject, options);

            if (convert.MeshList.Count == 0)
            {
                Logger.Info($"GameObject {gameObject.m_Name} has no mesh, skipping...");
                return false;
            }
            if (options.exportMaterials)
            {
                var materialExportPath = Path.Combine(exportPath, "Materials");
                Directory.CreateDirectory(materialExportPath);
                foreach (var material in options.materials)
                {
                    var matItem = new AssetItem(material);
                    ExportJSONFile(matItem, materialExportPath);
                }
            }
            exportPath = exportPath + FixFileName(gameObject.m_Name) + ".fbx";
            ExportFbx(convert, exportPath);
            return true;
        }

        public static void ExportGameObjectMerge(List<GameObject> gameObject, string exportPath, List<AssetItem> animationList = null)
        {
            var rootName = Path.GetFileNameWithoutExtension(exportPath);
            var options = new ModelConverter.Options()
            {
                imageFormat = ModelExporter.GetTextureFormat((ModelFormat)Settings.Default.modelFormat, Settings.Default.convertType),
                game = Studio.Game,
                collectAnimations = Settings.Default.collectAnimations,
                exportMaterials = Settings.Default.exportMaterials,
                materials = new HashSet<Material>(),
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(Settings.Default.uvs),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(Settings.Default.texs),
            };
            var convert = animationList != null
                ? new ModelConverter(rootName, gameObject, options, animationList.Select(x => (AnimationClip)x.Asset).ToArray())
                : new ModelConverter(rootName, gameObject, options);
            if (options.exportMaterials)
            {
                var materialExportPath = Path.Combine(Path.GetDirectoryName(exportPath), "Materials");
                Directory.CreateDirectory(materialExportPath);
                foreach (var material in options.materials)
                {
                    var matItem = new AssetItem(material);
                    ExportJSONFile(matItem, materialExportPath);
                }
            }
            ExportFbx(convert, exportPath);
        }

        private static void ExportFbx(IImported convert, string exportPath)
        {
            var exportOptions = new Fbx.ExportOptions()
            {
                eulerFilter = Settings.Default.eulerFilter,
                filterPrecision = (float)Settings.Default.filterPrecision,
                exportAllNodes = Settings.Default.exportAllNodes,
                exportSkins = Settings.Default.exportSkins,
                exportAnimations = Settings.Default.exportAnimations,
                exportBlendShape = Settings.Default.exportBlendShape,
                castToBone = Settings.Default.castToBone,
                boneSize = (int)Settings.Default.boneSize,
                scaleFactor = (float)Settings.Default.scaleFactor,
                fbxVersion = Settings.Default.fbxVersion,
                fbxFormat = Settings.Default.fbxFormat
            };
            var format = (ModelFormat)Settings.Default.modelFormat;
            if (format == ModelFormat.Fbx && !NativeLibraries.FbxAvailable)
            {
                throw new NotSupportedException($"FBX export requires the native library {NativeLibraries.FbxLibraryFileName} in {NativeLibraries.NativeDirectory} (glTF export does not: Export options > Model format)");
            }
            ModelExporter.ExportModel(exportPath, convert, exportOptions, format);
        }

        public static bool ExportDumpFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".txt", out var exportFullPath))
                return false;
            var str = item.Asset.Dump();
            if (str == null && item.Asset is MonoBehaviour m_MonoBehaviour)
            {
                var m_Type = Studio.MonoBehaviourToTypeTree(m_MonoBehaviour);
                str = m_MonoBehaviour.Dump(m_Type);
            }
            if (str != null)
            {
                File.WriteAllText(exportFullPath, str);
                return true;
            }
            return false;
        }

        public static bool ExportConvertFile(AssetItem item, string exportPath)
        {
            switch (item.Type)
            {
                case ClassIDType.GameObject:
                    return ExportGameObject(item, exportPath);
                case ClassIDType.Texture2D:
                    return ExportTexture2D(item, exportPath);
                case ClassIDType.Cubemap:
                case ClassIDType.Texture2DArray:
                case ClassIDType.Texture3D:
                case ClassIDType.CubemapArray:
                    return ExportTextureImages(item, exportPath);
                case ClassIDType.AudioClip:
                    return ExportAudioClip(item, exportPath);
                case ClassIDType.Shader:
                    return ExportShader(item, exportPath);
                case ClassIDType.TextAsset:
                    return ExportTextAsset(item, exportPath);
                case ClassIDType.MonoBehaviour:
                    return ExportMonoBehaviour(item, exportPath);
                case ClassIDType.Font:
                    return ExportFont(item, exportPath);
                case ClassIDType.Mesh:
                    return ExportMesh(item, exportPath);
                case ClassIDType.VideoClip:
                    return ExportVideoClip(item, exportPath);
                case ClassIDType.MovieTexture:
                    return ExportMovieTexture(item, exportPath);
                case ClassIDType.Sprite:
                    return ExportSprite(item, exportPath);
                case ClassIDType.Animator:
                    return ExportAnimator(item, exportPath);
                case ClassIDType.AnimationClip:
                    return ExportAnimationClip(item, exportPath);
                case ClassIDType.MiHoYoBinData:
                    return ExportMiHoYoBinData(item, exportPath);
                case ClassIDType.Material:
                    return ExportJSONFile(item, exportPath);
                default:
                    return item.Type == ClassIDType.TerrainData ? ExportTerrainData(item, exportPath) : ExportTypeTreeOrRawFile(item, exportPath);
            }
        }

        // The heightmap as a 16-bit PNG and as Unity's terrain RAW, and all the fields as JSON
        public static bool ExportTerrainData(AssetItem item, string exportPath)
        {
            var type = item.Asset.ToType();
            if (!TerrainDataConverter.TryGetHeightmap(type, out var heights, out var width, out var height))
                return ExportTypeTreeOrRawFile(item, exportPath);
            if (!TryExportFile(exportPath, item, ".png", out var exportFullPath))
                return false;
            TerrainDataConverter.SaveAsPng(heights, width, height, exportFullPath);
            var basePath = Path.ChangeExtension(exportFullPath, null);
            File.WriteAllBytes(basePath + ".raw", TerrainDataConverter.ToRaw(heights));
            File.WriteAllText(basePath + ".json", JsonConvert.SerializeObject(type, Formatting.Indented));
            return true;
        }

        // The classes without a converter: their fields from the type tree (of the file, or the type tree database) as JSON
        private static bool ExportTypeTreeOrRawFile(AssetItem item, string exportPath)
        {
            var type = item.Asset is MonoBehaviour ? null : item.Asset.ToType();
            if (type == null)
            {
                return ExportRawFile(item, exportPath);
            }
            return ExportTypeTreeJSON(item, type, exportPath);
        }

        private static bool ExportTypeTreeJSON(AssetItem item, System.Collections.Specialized.OrderedDictionary type, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                return false;
            File.WriteAllText(exportFullPath, JsonConvert.SerializeObject(type, Formatting.Indented));
            return true;
        }

        public static bool ExportJSONFile(AssetItem item, string exportPath)
        {
            if (item.Asset.GetType() == typeof(Object)) //a class without its own reader: its type tree has the content
            {
                var type = item.Asset.ToType();
                if (type != null)
                {
                    return ExportTypeTreeJSON(item, type, exportPath);
                }
            }
            if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                return false;

            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new StringEnumConverter());
            var str = JsonConvert.SerializeObject(item.Asset, Formatting.Indented, settings);
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static string FixFileName(string str)
        {
            if (str.Length >= 260) return Path.GetRandomFileName();
            return Path.GetInvalidFileNameChars().Aggregate(str, (current, c) => current.Replace(c, '_'));
        }
    }
}
