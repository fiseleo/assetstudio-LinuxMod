using System.IO;

namespace AssetStudio
{
    public static class ModelExporter
    {
        // The FBX SDK is not thread-safe (global plugin/IO state); concurrent exports from the
        // parallel exporter crash inside the native library, so FBX exports run one at a time.
        private static readonly object fbxLock = new object();

        public static void ExportFbx(string path, IImported imported, Fbx.ExportOptions exportOptions)
        {
            lock (fbxLock)
            {
                Fbx.Exporter.Export(path, imported, exportOptions);
            }
        }

        public static string GetExtension(ModelFormat format) => format switch
        {
            ModelFormat.Gltf => ".gltf",
            ModelFormat.Glb => ".glb",
            _ => ".fbx",
        };

        /// <summary>glTF images are PNG or JPEG: other texture formats are exported as PNG for glTF.</summary>
        public static ImageFormat GetTextureFormat(ModelFormat format, ImageFormat imageFormat)
        {
            return format == ModelFormat.Fbx || imageFormat == ImageFormat.Png || imageFormat == ImageFormat.Jpeg ? imageFormat : ImageFormat.Png;
        }

        /// <summary>Exports a model in a format; the path's extension is replaced by the format's. Returns the path written.</summary>
        public static string ExportModel(string path, IImported imported, Fbx.ExportOptions exportOptions, ModelFormat format)
        {
            path = Path.ChangeExtension(path, GetExtension(format));
            if (format == ModelFormat.Fbx)
            {
                ExportFbx(path, imported, exportOptions);
            }
            else
            {
                GltfExporter.Export(path, imported, exportOptions, format == ModelFormat.Glb);
            }
            return path;
        }
    }
}
