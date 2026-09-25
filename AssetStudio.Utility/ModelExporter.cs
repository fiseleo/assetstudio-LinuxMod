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
    }
}
