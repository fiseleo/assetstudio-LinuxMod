using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static AssetStudio.ImportHelper;

namespace AssetStudio
{
    public class VersionPromptEventArgs : EventArgs
    {
        public string FileName { get; set; }
        public string UserProvidedVersion { get; set; }
        public bool Cancelled { get; set; }
    }

    public class AssetsManager
    {
        public Game Game;
        public event EventHandler<VersionPromptEventArgs> OnVersionPrompt;
        public bool Silent = false;
        public bool SkipProcess = false;
        public bool ResolveDependencies = false;
        public string SpecifyUnityVersion;
        public string DefaultVersion; // Fallback version for stripped files
        private string detectedFolderVersion; // Version detected from globalgamemanagers or bundles
        public CancellationTokenSource tokenSource = new CancellationTokenSource();
        public List<SerializedFile> assetsFileList = new List<SerializedFile>();

        // Thread-safe collections for parallel file loading
        private readonly object assetsFileListLock = new object();
        private readonly object importFilesLock = new object();
        private readonly object versionDetectionLock = new object();

        internal Dictionary<string, int> assetsFileIndexCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal ConcurrentDictionary<string, BinaryReader> resourceFileReaders = new ConcurrentDictionary<string, BinaryReader>(StringComparer.OrdinalIgnoreCase);

        internal List<string> importFiles = new List<string>();
        internal ConcurrentDictionary<string, byte> importFilesHash = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        internal ConcurrentDictionary<string, byte> noexistFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        internal ConcurrentDictionary<string, byte> assetsFileListHash = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        public void LoadFiles(params string[] files)
        {
            if (Silent)
            {
                Logger.Silent = true;
                Progress.Silent = true;
            }

            var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
            // Priority 3: Scan for globalgamemanagers or data.unity3d to detect Unity version
            DetectUnityVersionFromFolder(path);

            MergeSplitAssets(path);
            var toReadFile = ProcessingSplitFiles(files.ToList());
            if (ResolveDependencies)
                toReadFile = AssetsHelper.ProcessDependencies(toReadFile);
            Load(toReadFile);

            if (Silent)
            {
                Logger.Silent = false;
                Progress.Silent = false;
            }
        }

        public void LoadFolder(string path)
        {
            if (Silent)
            {
                Logger.Silent = true;
                Progress.Silent = true;
            }

            // Priority 3: Scan for globalgamemanagers or data.unity3d to detect Unity version
            DetectUnityVersionFromFolder(path);

            MergeSplitAssets(path, true);
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).ToList();
            var toReadFile = ProcessingSplitFiles(files);
            Load(toReadFile);

            if (Silent)
            {
                Logger.Silent = false;
                Progress.Silent = false;
            }
        }

        private void Load(string[] files)
        {
            foreach (var file in files)
            {
                Logger.Verbose($"caching {file} path and name to filter out duplicates");
                importFiles.Add(file);
                importFilesHash.TryAdd(Path.GetFileName(file), 0);
            }

            Progress.Reset();

            // Parallel file loading with dynamic dependency handling
            // Files are processed in waves: load current batch in parallel, 
            // then process any new dependencies that were discovered
            int processedCount = 0;
            int totalProgress = 0;

            while (processedCount < importFiles.Count)
            {
                if (tokenSource.IsCancellationRequested)
                {
                    Logger.Info("Loading files has been aborted !!");
                    break;
                }

                // Get current batch of files to process
                int currentBatchEnd;
                lock (importFilesLock)
                {
                    currentBatchEnd = importFiles.Count;
                }

                // Process this batch in parallel
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = tokenSource.Token
                };

                try
                {
                    Parallel.For(processedCount, currentBatchEnd, options, i =>
                    {
                        string fileToLoad;
                        lock (importFilesLock)
                        {
                            fileToLoad = importFiles[i];
                        }
                        LoadFile(fileToLoad);

                        int progress = Interlocked.Increment(ref totalProgress);
                        // Use approximate count since it may grow
                        Progress.Report(progress, Math.Max(progress, importFiles.Count));
                    });
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("Loading files has been aborted !!");
                    break;
                }

                processedCount = currentBatchEnd;
            }

            importFiles.Clear();
            importFilesHash.Clear();
            noexistFiles.Clear();
            assetsFileListHash.Clear();
            AssetsHelper.ClearOffsets();

            if (!SkipProcess)
            {
                ReadAssets();
                ProcessAssets();
            }
        }

        /// <summary>
        /// Priority 3: Detect Unity version from globalgamemanagers or data.unity3d files
        /// </summary>
        private void DetectUnityVersionFromFolder(string path)
        {
            if (!string.IsNullOrEmpty(detectedFolderVersion))
            {
                return; // Already detected
            }

            try
            {
                // Look for globalgamemanagers file (common in PC builds)
                var globalGameManagersFiles = Directory.GetFiles(path, "globalgamemanagers", SearchOption.AllDirectories);
                if (globalGameManagersFiles.Length > 0)
                {
                    Logger.Verbose($"Found globalgamemanagers at {globalGameManagersFiles[0]}, attempting to extract version");
                    detectedFolderVersion = TryExtractVersionFromFile(globalGameManagersFiles[0]);
                    if (!string.IsNullOrEmpty(detectedFolderVersion))
                    {
                        Logger.Info($"Detected Unity version from globalgamemanagers: {detectedFolderVersion}");
                        return;
                    }
                }

                // Look for data.unity3d bundle (common in WebGL/mobile builds)
                var dataUnity3dFiles = Directory.GetFiles(path, "data.unity3d", SearchOption.AllDirectories);
                if (dataUnity3dFiles.Length > 0)
                {
                    Logger.Verbose($"Found data.unity3d at {dataUnity3dFiles[0]}, attempting to extract version");
                    detectedFolderVersion = TryExtractVersionFromBundle(dataUnity3dFiles[0]);
                    if (!string.IsNullOrEmpty(detectedFolderVersion))
                    {
                        Logger.Info($"Detected Unity version from data.unity3d: {detectedFolderVersion}");
                        return;
                    }
                }

                // Try any .unity3d bundle files
                var bundleFiles = Directory.GetFiles(path, "*.unity3d", SearchOption.TopDirectoryOnly);
                foreach (var bundleFile in bundleFiles.Take(3)) // Check first 3 bundles
                {
                    Logger.Verbose($"Checking bundle {bundleFile} for Unity version");
                    detectedFolderVersion = TryExtractVersionFromBundle(bundleFile);
                    if (!string.IsNullOrEmpty(detectedFolderVersion))
                    {
                        Logger.Info($"Detected Unity version from {Path.GetFileName(bundleFile)}: {detectedFolderVersion}");
                        return;
                    }
                }

                // Try .bundle files (common in Addressables/modern Unity games)
                var dotBundleFiles = Directory.GetFiles(path, "*.bundle", SearchOption.AllDirectories);
                foreach (var bundleFile in dotBundleFiles.Take(5)) // Check first 5 bundles
                {
                    Logger.Verbose($"Checking .bundle file {bundleFile} for Unity version");
                    detectedFolderVersion = TryExtractVersionFromBundle(bundleFile);
                    if (!string.IsNullOrEmpty(detectedFolderVersion))
                    {
                        Logger.Info($"Detected Unity version from {Path.GetFileName(bundleFile)}: {detectedFolderVersion}");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Verbose($"Error detecting Unity version from folder: {e.Message}");
            }
        }

        private string TryExtractVersionFromFile(string filePath)
        {
            try
            {
                using (var reader = new FileReader(filePath))
                {
                    if (reader.FileType == FileType.AssetsFile)
                    {
                        var tempFile = new SerializedFile(reader, this);
                        if (!tempFile.IsVersionStripped)
                        {
                            return tempFile.unityVersion;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Verbose($"Error reading {filePath}: {e.Message}");
            }
            return null;
        }

        private string TryExtractVersionFromBundle(string filePath)
        {
            try
            {
                using (var reader = new FileReader(filePath))
                {
                    if (reader.FileType == FileType.BundleFile)
                    {
                        var bundleFile = new BundleFile(reader, Game);
                        if (!string.IsNullOrEmpty(bundleFile.m_Header.unityRevision))
                        {
                            return bundleFile.m_Header.unityRevision;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Verbose($"Error reading bundle {filePath}: {e.Message}");
            }
            return null;
        }

        private void LoadFile(string fullName)
        {
            var reader = new FileReader(fullName);
            reader = reader.PreProcessing(Game);
            LoadFile(reader);
        }

        private void LoadFile(FileReader reader)
        {
            switch (reader.FileType)
            {
                case FileType.AssetsFile:
                    LoadAssetsFile(reader);
                    break;
                case FileType.BundleFile:
                    LoadBundleFile(reader);
                    break;
                case FileType.WebFile:
                    LoadWebFile(reader);
                    break;
                case FileType.GZipFile:
                    LoadFile(DecompressGZip(reader));
                    break;
                case FileType.BrotliFile:
                    LoadFile(DecompressBrotli(reader));
                    break;
                case FileType.ZipFile:
                    LoadZipFile(reader);
                    break;
                case FileType.BlockFile:
                    LoadBlockFile(reader);
                    break;
                case FileType.BlkFile:
                    LoadBlkFile(reader);
                    break;
                case FileType.MhyFile:
                    LoadMhyFile(reader);
                    break;
            }
        }

        private void LoadAssetsFile(FileReader reader)
        {
            // Skip loading standalone CAB files from _unpacked folders
            // These are cached extracts from bundles with stripped/incompatible structure
            // The bundle version will be loaded anyway and has the correct structure
            if (reader.FullPath.Contains("_unpacked") && reader.FileName.StartsWith("CAB-"))
            {
                Logger.Verbose($"Skipping unpacked CAB file {reader.FullPath} (will load from bundle instead)");
                reader.Dispose();
                return;
            }

            Logger.Info($"Loading {reader.FullPath}");
            try
            {
                var assetsFile = new SerializedFile(reader, this);
                CheckStrippedVersion(assetsFile);

                lock (assetsFileListLock)
                {
                    // Check if this file already exists in the list
                    var existingIndex = assetsFileList.FindIndex(f => f.fileName.Equals(reader.FileName, StringComparison.OrdinalIgnoreCase));
                    if (existingIndex >= 0)
                    {
                        var existing = assetsFileList[existingIndex];
                        // Replace standalone version with bundle version
                        // Bundle versions have correct object data while standalone CAB files may be stripped/incomplete
                        if (existing.IsFromBundle && !assetsFile.IsFromBundle)
                        {
                            // New file is standalone, existing is from bundle - keep bundle version
                            Logger.Info($"Skipping standalone version of {reader.FileName} (already have bundle version)");
                            reader.Dispose();
                            return;
                        }
                        else if (!existing.IsFromBundle && assetsFile.IsFromBundle)
                        {
                            // New file is from bundle, existing is standalone - replace with bundle
                            Logger.Info($"Replacing standalone version of {reader.FileName} with bundle version");
                            assetsFileList[existingIndex] = assetsFile;
                            existing.reader?.Dispose();
                        }
                        else
                        {
                            // Both from same source type - keep existing
                            Logger.Info($"Skipping {reader.FullPath} (already have {(existing.IsFromBundle ? "bundle" : "standalone")} version)");
                            reader.Dispose();
                            return; // Don't process externals for skipped file
                        }
                    }
                    else
                    {
                        // First time seeing this file - add to list
                        assetsFileListHash.TryAdd(reader.FileName, 0);
                        assetsFileList.Add(assetsFile);
                    }
                }

                foreach (var sharedFile in assetsFile.m_Externals)
                {
                    Logger.Verbose($"{assetsFile.fileName} needs external file {sharedFile.fileName}, attempting to look it up...");
                    var sharedFileName = sharedFile.fileName;

                    // Thread-safe check - TryAdd returns false if key already exists
                    if (importFilesHash.TryAdd(sharedFileName, 0))
                    {
                        var sharedFilePath = Path.Combine(Path.GetDirectoryName(reader.FullPath), sharedFileName);
                        if (!noexistFiles.ContainsKey(sharedFilePath))
                        {
                            if (!File.Exists(sharedFilePath))
                            {
                                var findFiles = Directory.GetFiles(Path.GetDirectoryName(reader.FullPath), sharedFileName, SearchOption.AllDirectories);
                                if (findFiles.Length > 0)
                                {
                                    Logger.Verbose($"Found {findFiles.Length} matching files, picking first file {findFiles[0]} !!");
                                    sharedFilePath = findFiles[0];
                                }
                            }
                            if (File.Exists(sharedFilePath))
                            {
                                lock (importFilesLock)
                                {
                                    importFiles.Add(sharedFilePath);
                                }
                            }
                            else
                            {
                                Logger.Verbose("Nothing was found, caching into non existant files to avoid repeated searching !!");
                                noexistFiles.TryAdd(sharedFilePath, 0);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading assets file {reader.FullPath}", e);
                reader.Dispose();
            }
        }

        private void LoadAssetsFromMemory(FileReader reader, string originalPath, string unityVersion = null, long originalOffset = 0)
        {
            Logger.Verbose($"Loading asset file {reader.FileName} with version {unityVersion} from {originalPath} at offset 0x{originalOffset:X8}");

            try
            {
                var assetsFile = new SerializedFile(reader, this);
                assetsFile.originalPath = originalPath;
                assetsFile.offset = originalOffset;
                assetsFile.IsFromBundle = true; // Mark as loaded from bundle/archive
                if (!string.IsNullOrEmpty(unityVersion) && assetsFile.header.m_Version < SerializedFileFormatVersion.Unknown_7)
                {
                    assetsFile.SetVersion(unityVersion);
                }
                CheckStrippedVersion(assetsFile);

                lock (assetsFileListLock)
                {

                    // Check if this file already exists in the list
                    var existingIndex = assetsFileList.FindIndex(f => f.fileName.Equals(reader.FileName, StringComparison.OrdinalIgnoreCase));

                    if (existingIndex >= 0)
                    {
                        var existing = assetsFileList[existingIndex];
                        // Replace standalone version with bundle version
                        // Bundle versions have correct object data while standalone CAB files may be stripped/incomplete
                        if (!existing.IsFromBundle && assetsFile.IsFromBundle)
                        {
                            Logger.Info($"Replacing standalone version of {reader.FileName} with bundle version");
                            assetsFileList[existingIndex] = assetsFile;
                            existing.reader?.Dispose();
                        }
                        else
                        {
                            // Existing is from bundle or both from same source - keep existing
                            Logger.Info($"Skipping {originalPath} ({reader.FileName}) - already have {(existing.IsFromBundle ? "bundle" : "standalone")} version");
                            return; // Don't add duplicate
                        }
                    }
                    else
                    {
                        // First time seeing this file
                        assetsFileListHash.TryAdd(reader.FileName, 0);
                        assetsFileList.Add(assetsFile);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading assets file {reader.FullPath} from {Path.GetFileName(originalPath)}", e);
                resourceFileReaders.TryAdd(reader.FileName, reader);
            }
        }

        private void LoadBundleFile(FileReader reader, string originalPath = null, long originalOffset = 0, bool log = true)
        {
            if (log)
            {
                Logger.Info("Loading " + reader.FullPath);
            }
            try
            {
                var bundleFile = new BundleFile(reader, Game);

                // Priority 2: Extract Unity version from bundle header if available (thread-safe)
                if (!string.IsNullOrEmpty(bundleFile.m_Header.unityRevision))
                {
                    lock (versionDetectionLock)
                    {
                        if (string.IsNullOrEmpty(detectedFolderVersion))
                        {
                            detectedFolderVersion = bundleFile.m_Header.unityRevision;
                            Logger.Info($"Detected Unity version from bundle: {detectedFolderVersion}");
                        }
                    }
                }

                foreach (var file in bundleFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    if (subReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(subReader, originalPath ?? reader.FullPath, bundleFile.m_Header.unityRevision, originalOffset);
                    }
                    else
                    {
                        Logger.Verbose("Caching resource stream");
                        resourceFileReaders.TryAdd(file.fileName, subReader); //TODO
                    }
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error($"Game type mismatch, Expected {nameof(Mr0k)} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            catch (Exception e)
            {
                var str = $"Error while reading bundle file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadWebFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                var webFile = new WebFile(reader);
                foreach (var file in webFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    switch (subReader.FileType)
                    {
                        case FileType.AssetsFile:
                            LoadAssetsFromMemory(subReader, reader.FullPath);
                            break;
                        case FileType.BundleFile:
                            LoadBundleFile(subReader, reader.FullPath);
                            break;
                        case FileType.WebFile:
                            LoadWebFile(subReader);
                            break;
                        case FileType.ResourceFile:
                            Logger.Verbose("Caching resource stream");
                            resourceFileReaders.TryAdd(file.fileName, subReader); //TODO
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading web file {reader.FullPath}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadZipFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FileName);
            try
            {
                using (ZipArchive archive = new ZipArchive(reader.BaseStream, ZipArchiveMode.Read))
                {
                    List<string> splitFiles = new List<string>();
                    Logger.Verbose("Register all files before parsing the assets so that the external references can be found and find split files");
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Name.Contains(".split"))
                        {
                            string baseName = Path.GetFileNameWithoutExtension(entry.Name);
                            string basePath = Path.Combine(Path.GetDirectoryName(entry.FullName), baseName);
                            if (!splitFiles.Contains(basePath))
                            {
                                splitFiles.Add(basePath);
                                importFilesHash.TryAdd(baseName, 0);
                            }
                        }
                        else
                        {
                            importFilesHash.TryAdd(entry.Name, 0);
                        }
                    }

                    Logger.Verbose("Merge split files and load the result");
                    foreach (string basePath in splitFiles)
                    {
                        try
                        {
                            Stream splitStream = new MemoryStream();
                            int i = 0;
                            while (true)
                            {
                                string path = $"{basePath}.split{i++}";
                                ZipArchiveEntry entry = archive.GetEntry(path);
                                if (entry == null)
                                    break;
                                using (Stream entryStream = entry.Open())
                                {
                                    entryStream.CopyTo(splitStream);
                                }
                            }
                            splitStream.Seek(0, SeekOrigin.Begin);
                            FileReader entryReader = new FileReader(basePath, splitStream);
                            entryReader = entryReader.PreProcessing(Game);
                            LoadFile(entryReader);
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip split file {basePath}", e);
                        }
                    }

                    Logger.Verbose("Load all entries");
                    Logger.Verbose($"Found {archive.Entries.Count} entries");
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        try
                        {
                            string dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), reader.FileName, entry.FullName);
                            Logger.Verbose("Create a new stream to store the deflated stream in and keep the data for later extraction");
                            Stream streamReader = new MemoryStream();
                            using (Stream entryStream = entry.Open())
                            {
                                entryStream.CopyTo(streamReader);
                            }
                            streamReader.Position = 0;

                            FileReader entryReader = new FileReader(dummyPath, streamReader);
                            entryReader = entryReader.PreProcessing(Game);
                            LoadFile(entryReader);
                            if (entryReader.FileType == FileType.ResourceFile)
                            {
                                entryReader.Position = 0;
                                Logger.Verbose("Caching resource file");
                                resourceFileReaders.TryAdd(entry.Name, entryReader);
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip entry {entry.FullName}", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading zip file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }
        private void LoadBlockFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                using var stream = new OffsetStream(reader.BaseStream, 0);
                foreach (var offset in stream.GetOffsets(reader.FullPath))
                {
                    var name = offset.ToString("X8");
                    Logger.Info($"Loading Block {name}");

                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), name);
                    var subReader = new FileReader(dummyPath, stream, true);
                    switch (subReader.FileType)
                    {
                        case FileType.ENCRFile:
                        case FileType.BundleFile:
                            LoadBundleFile(subReader, reader.FullPath, offset, false);
                            break;
                        case FileType.BlbFile:
                            LoadBlbFile(subReader, reader.FullPath, offset, false);
                            break;
                        case FileType.MhyFile:
                            LoadMhyFile(subReader, reader.FullPath, offset, false);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading block file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }
        private void LoadBlkFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                using var stream = BlkUtils.Decrypt(reader, (Blk)Game);
                foreach (var offset in stream.GetOffsets(reader.FullPath))
                {
                    var name = offset.ToString("X8");
                    Logger.Info($"Loading Block {name}");

                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), name);
                    var subReader = new FileReader(dummyPath, stream, true);
                    switch (subReader.FileType)
                    {
                        case FileType.BundleFile:
                            LoadBundleFile(subReader, reader.FullPath, offset, false);
                            break;
                        case FileType.MhyFile:
                            LoadMhyFile(subReader, reader.FullPath, offset, false);
                            break;
                    }
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error($"Game type mismatch, Expected {nameof(Blk)} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading blk file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }
        private void LoadMhyFile(FileReader reader, string originalPath = null, long originalOffset = 0, bool log = true)
        {
            if (log)
            {
                Logger.Info("Loading " + reader.FullPath);
            }
            try
            {
                var mhyFile = new MhyFile(reader, (Mhy)Game);
                Logger.Verbose($"mhy total size: {mhyFile.m_Header.size:X8}");
                foreach (var file in mhyFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var cabReader = new FileReader(dummyPath, file.stream);
                    if (cabReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(cabReader, originalPath ?? reader.FullPath, mhyFile.m_Header.unityRevision, originalOffset);
                    }
                    else
                    {
                        Logger.Verbose("Caching resource stream");
                        resourceFileReaders.TryAdd(file.fileName, cabReader); //TODO
                    }
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error($"Game type mismatch, Expected {nameof(Mhy)} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            catch (Exception e)
            {
                var str = $"Error while reading mhy file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadBlbFile(FileReader reader, string originalPath = null, long originalOffset = 0, bool log = true)
        {
            if (log)
            {
                Logger.Info("Loading " + reader.FullPath);
            }
            try
            {
                var blbFile = new BlbFile(reader, reader.FullPath);
                foreach (var file in blbFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var cabReader = new FileReader(dummyPath, file.stream);
                    if (cabReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(cabReader, originalPath ?? reader.FullPath, blbFile.m_Header.unityRevision, originalOffset);
                    }
                    else
                    {
                        Logger.Verbose("Caching resource stream");
                        resourceFileReaders.TryAdd(file.fileName, cabReader); //TODO
                    }
                }
            }
            catch (Exception e)
            {
                var str = $"Error while reading Blb file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        // Lock for version prompting - ensures only one thread prompts user
        private readonly object versionPromptLock = new object();
        private bool versionPrompted = false;

        public void CheckStrippedVersion(SerializedFile assetsFile)
        {
            if (assetsFile.IsVersionStripped && string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                // Priority 1 & 3: Use detected folder version or DefaultVersion as fallback
                string version;
                lock (versionDetectionLock)
                {
                    version = detectedFolderVersion;
                }

                if (!string.IsNullOrEmpty(version))
                {
                    Logger.Info($"Using detected Unity version for {assetsFile.fileName}: {version}");
                    assetsFile.SetVersion(version);
                    return;
                }
                else if (!string.IsNullOrEmpty(DefaultVersion))
                {
                    Logger.Info($"Using default Unity version for {assetsFile.fileName}: {DefaultVersion}");
                    assetsFile.SetVersion(DefaultVersion);
                    return;
                }

                // Try to prompt user for version if event is subscribed (thread-safe)
                if (OnVersionPrompt != null)
                {
                    lock (versionPromptLock)
                    {
                        // Check again if another thread already got the version
                        if (!string.IsNullOrEmpty(SpecifyUnityVersion))
                        {
                            assetsFile.SetVersion(SpecifyUnityVersion);
                            return;
                        }

                        // Only prompt once
                        if (!versionPrompted)
                        {
                            versionPrompted = true;
                            var eventArgs = new VersionPromptEventArgs
                            {
                                FileName = assetsFile.fileName
                            };
                            OnVersionPrompt(this, eventArgs);

                            if (!eventArgs.Cancelled && !string.IsNullOrEmpty(eventArgs.UserProvidedVersion))
                            {
                                SpecifyUnityVersion = eventArgs.UserProvidedVersion;
                                assetsFile.SetVersion(eventArgs.UserProvidedVersion);
                                return;
                            }
                        }
                        else if (!string.IsNullOrEmpty(SpecifyUnityVersion))
                        {
                            assetsFile.SetVersion(SpecifyUnityVersion);
                            return;
                        }
                    }
                }

                throw new Exception("The Unity version has been stripped, please set the version in the options");
            }
            if (!string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                assetsFile.SetVersion(SpecifyUnityVersion);
            }
        }

        public void Clear()
        {
            Logger.Verbose("Cleaning up...");

            foreach (var assetsFile in assetsFileList)
            {
                assetsFile.Objects.Clear();
                assetsFile.reader.Close();
            }
            assetsFileList.Clear();

            foreach (var resourceFileReader in resourceFileReaders)
            {
                resourceFileReader.Value.Close();
            }
            resourceFileReaders.Clear();

            assetsFileIndexCache.Clear();

            // Reset parallel loading state
            versionPrompted = false;
            detectedFolderVersion = null;

            tokenSource.Dispose();
            tokenSource = new CancellationTokenSource();

            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private void ReadAssets()
        {
            Logger.Info("Read assets...");

            var progressCount = assetsFileList.Sum(x => x.m_Objects.Count);
            int i = 0;
            Progress.Reset();
            foreach (var assetsFile in assetsFileList)
            {
                foreach (var objectInfo in assetsFile.m_Objects)
                {
                    if (tokenSource.IsCancellationRequested)
                    {
                        Logger.Info("Reading assets has been cancelled !!");
                        return;
                    }
                    var objectReader = new ObjectReader(assetsFile.reader, assetsFile, objectInfo, Game);

                    try
                    {
                        Object obj = objectReader.type switch
                        {
                            ClassIDType.Animation when ClassIDType.Animation.CanParse() => new Animation(objectReader),
                            ClassIDType.AnimationClip when ClassIDType.AnimationClip.CanParse() => new AnimationClip(objectReader),
                            ClassIDType.Animator when ClassIDType.Animator.CanParse() => new Animator(objectReader),
                            ClassIDType.AnimatorController when ClassIDType.AnimatorController.CanParse() => new AnimatorController(objectReader),
                            ClassIDType.AnimatorOverrideController when ClassIDType.AnimatorOverrideController.CanParse() => new AnimatorOverrideController(objectReader),
                            ClassIDType.AssetBundle when ClassIDType.AssetBundle.CanParse() => new AssetBundle(objectReader),
                            ClassIDType.AudioClip when ClassIDType.AudioClip.CanParse() => new AudioClip(objectReader),
                            ClassIDType.Avatar when ClassIDType.Avatar.CanParse() => new Avatar(objectReader),
                            ClassIDType.Font when ClassIDType.Font.CanParse() => new Font(objectReader),
                            ClassIDType.GameObject when ClassIDType.GameObject.CanParse() => new GameObject(objectReader),
                            ClassIDType.IndexObject when ClassIDType.IndexObject.CanParse() => new IndexObject(objectReader),
                            ClassIDType.Material when ClassIDType.Material.CanParse() => new Material(objectReader),
                            ClassIDType.Mesh when ClassIDType.Mesh.CanParse() => new Mesh(objectReader),
                            ClassIDType.MeshFilter when ClassIDType.MeshFilter.CanParse() => new MeshFilter(objectReader),
                            ClassIDType.MeshRenderer when ClassIDType.MeshRenderer.CanParse() => new MeshRenderer(objectReader),
                            ClassIDType.MiHoYoBinData when ClassIDType.MiHoYoBinData.CanParse() => new MiHoYoBinData(objectReader),
                            ClassIDType.MonoBehaviour when ClassIDType.MonoBehaviour.CanParse() => new MonoBehaviour(objectReader),
                            ClassIDType.MonoScript when ClassIDType.MonoScript.CanParse() => new MonoScript(objectReader),
                            ClassIDType.MovieTexture when ClassIDType.MovieTexture.CanParse() => new MovieTexture(objectReader),
                            ClassIDType.PlayerSettings when ClassIDType.PlayerSettings.CanParse() => new PlayerSettings(objectReader),
                            ClassIDType.RectTransform when ClassIDType.RectTransform.CanParse() => new RectTransform(objectReader),
                            ClassIDType.Shader when ClassIDType.Shader.CanParse() => new Shader(objectReader),
                            ClassIDType.SkinnedMeshRenderer when ClassIDType.SkinnedMeshRenderer.CanParse() => new SkinnedMeshRenderer(objectReader),
                            ClassIDType.Sprite when ClassIDType.Sprite.CanParse() => new Sprite(objectReader),
                            ClassIDType.SpriteAtlas when ClassIDType.SpriteAtlas.CanParse() => new SpriteAtlas(objectReader),
                            ClassIDType.TextAsset when ClassIDType.TextAsset.CanParse() => new TextAsset(objectReader),
                            ClassIDType.Texture2D when ClassIDType.Texture2D.CanParse() => new Texture2D(objectReader),
                            ClassIDType.Transform when ClassIDType.Transform.CanParse() => new Transform(objectReader),
                            ClassIDType.VideoClip when ClassIDType.VideoClip.CanParse() => new VideoClip(objectReader),
                            ClassIDType.ResourceManager when ClassIDType.ResourceManager.CanParse() => new ResourceManager(objectReader),
                            _ => new Object(objectReader),
                        };
                        assetsFile.AddObject(obj);
                    }
                    catch (Exception e)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("Unable to load object")
                            .AppendLine($"Assets {assetsFile.fileName}")
                            .AppendLine($"Path {assetsFile.originalPath}")
                            .AppendLine($"Type {objectReader.type}")
                            .AppendLine($"PathID {objectInfo.m_PathID}")
                            .Append(e);
                        Logger.Error(sb.ToString());
                    }

                    Progress.Report(++i, progressCount);
                }
            }
        }

        private void ProcessAssets()
        {
            Logger.Info("Process Assets...");

            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (tokenSource.IsCancellationRequested)
                    {
                        Logger.Info("Processing assets has been cancelled !!");
                        return;
                    }
                    if (obj is GameObject m_GameObject)
                    {
                        Logger.Verbose($"GameObject with {m_GameObject.m_PathID} in file {m_GameObject.assetsFile.fileName} has {m_GameObject.m_Components.Count} components, Attempting to fetch them...");
                        foreach (var pptr in m_GameObject.m_Components)
                        {
                            if (pptr.TryGet(out var m_Component))
                            {
                                switch (m_Component)
                                {
                                    case Transform m_Transform:
                                        Logger.Verbose($"Fetched Transform component with {m_Transform.m_PathID} in file {m_Transform.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_Transform = m_Transform;
                                        break;
                                    case MeshRenderer m_MeshRenderer:
                                        Logger.Verbose($"Fetched MeshRenderer component with {m_MeshRenderer.m_PathID} in file {m_MeshRenderer.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_MeshRenderer = m_MeshRenderer;
                                        break;
                                    case MeshFilter m_MeshFilter:
                                        Logger.Verbose($"Fetched MeshFilter component with {m_MeshFilter.m_PathID} in file {m_MeshFilter.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_MeshFilter = m_MeshFilter;
                                        break;
                                    case SkinnedMeshRenderer m_SkinnedMeshRenderer:
                                        Logger.Verbose($"Fetched SkinnedMeshRenderer component with {m_SkinnedMeshRenderer.m_PathID} in file {m_SkinnedMeshRenderer.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_SkinnedMeshRenderer = m_SkinnedMeshRenderer;
                                        break;
                                    case Animator m_Animator:
                                        Logger.Verbose($"Fetched Animator component with {m_Animator.m_PathID} in file {m_Animator.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_Animator = m_Animator;
                                        break;
                                    case Animation m_Animation:
                                        Logger.Verbose($"Fetched Animation component with {m_Animation.m_PathID} in file {m_Animation.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_Animation = m_Animation;
                                        break;
                                }
                            }
                        }
                    }
                    else if (obj is SpriteAtlas m_SpriteAtlas)
                    {
                        if (m_SpriteAtlas.m_RenderDataMap.Count > 0)
                        {
                            Logger.Verbose($"SpriteAtlas with {m_SpriteAtlas.m_PathID} in file {m_SpriteAtlas.assetsFile.fileName} has {m_SpriteAtlas.m_PackedSprites.Count} packed sprites, Attempting to fetch them...");
                            foreach (var m_PackedSprite in m_SpriteAtlas.m_PackedSprites)
                            {
                                if (m_PackedSprite.TryGet(out var m_Sprite))
                                {
                                    if (m_Sprite.m_SpriteAtlas.IsNull)
                                    {
                                        Logger.Verbose($"Fetched Sprite with {m_Sprite.m_PathID} in file {m_Sprite.assetsFile.fileName}, assigning to parent SpriteAtlas...");
                                        m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                    }
                                    else
                                    {
                                        m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlaOld);
                                        if (m_SpriteAtlaOld.m_IsVariant)
                                        {
                                            Logger.Verbose($"Fetched Sprite with {m_Sprite.m_PathID} in file {m_Sprite.assetsFile.fileName} has a variant of the origianl SpriteAtlas, disposing of the variant and assinging to the parent SpriteAtlas...");
                                            m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 6000.6.0a3 and up: atlases no longer list their sprites, match them by render data key instead
            var atlasesByKey = new Dictionary<KeyValuePair<Guid, long>, SpriteAtlas>();
            foreach (var atlas in assetsFileList.SelectMany(x => x.Objects).OfType<SpriteAtlas>().Where(x => x.m_PackedSprites.Count == 0 && !x.m_IsVariant))
            {
                foreach (var key in atlas.m_RenderDataMap.Keys)
                {
                    atlasesByKey.TryAdd(key, atlas);
                }
            }
            if (atlasesByKey.Count > 0)
            {
                foreach (var sprite in assetsFileList.SelectMany(x => x.Objects).OfType<Sprite>().Where(x => x.m_SpriteAtlas?.IsNull == true))
                {
                    if (atlasesByKey.TryGetValue(sprite.m_RenderDataKey, out var atlas))
                    {
                        sprite.m_SpriteAtlas.Set(atlas);
                    }
                }
            }
        }
    }
}
