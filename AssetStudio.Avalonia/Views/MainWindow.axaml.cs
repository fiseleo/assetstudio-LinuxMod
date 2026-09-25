using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static AssetStudio.Avalonia.Studio;

namespace AssetStudio.Avalonia.Views
{
    public partial class MainWindow : Window
    {
        private static readonly string AppTitle = $"AssetStudio v{Assembly.GetExecutingAssembly().GetName().Version} (Avalonia)";
        private const int MaxLogLines = 5000;

        private AssetItem lastSelectedItem;
        private WriteableBitmap imageTexture;
        private GUILogger logger;
        private readonly StringBuilder logBuffer = new StringBuilder();
        private readonly global::Avalonia.Collections.AvaloniaList<string> logLines = new();
        private bool logFlushScheduled;
        private int errorDialogOpen;
        private string pendingStatus;
        private bool statusFlushScheduled;
        private int pendingProgress = -1;
        private bool progressFlushScheduled;

        private static readonly char[] textureChannelNames = new[] { 'B', 'G', 'R', 'A' };
        private readonly bool[] textureChannels = new[] { true, true, true, true };

        private List<SceneNode> sceneRoots = new List<SceneNode>();
        private readonly List<SceneNode> treeSrcResults = new List<SceneNode>();
        private int nextGObject;

        private string sortMember;
        private bool reverseSort;

        private bool suppressGameChange;
        private bool suppressAIChange;
        private MeshRenderer meshRenderer;
        private WriteableBitmap meshBitmap;
        private Point lastPointer;
        private bool meshDragging;
        private bool meshRenderScheduled;
        private Process audioProcess;
        private string audioTempFile;

        public MainWindow()
        {
            InitializeComponent();
            Title = AppTitle;

            AddHandler(DragDrop.DropEvent, Window_Drop);
            AddHandler(DragDrop.DragOverEvent, Window_DragOver);
            AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
            assetListView.AddHandler(KeyDownEvent, AssetList_KeyDown, RoutingStrategies.Tunnel);
            sceneTreeView.ItemsSource = sceneRoots;
            logList.ItemsSource = logLines;

            InitializeExportOptions();
            InitializeProgressBar();
            InitializeLogger();
            InitializeOptions();

            Studio.RequestAssemblyFolder = () => Dialogs.PickFolderAsync(this, "Select Assembly Folder (Managed DLLs) - cancel to skip", Settings.Default.lastOpenDirectory);
            assetsManager.OnVersionPrompt += AssetsManager_OnVersionPrompt;

            Closing += (_, _) => StopAudio();
            ReportNativeLibraries();
        }

        #region Initialization

        private void InitializeExportOptions()
        {
            var s = Settings.Default;
            enableConsole.IsChecked = s.enableConsole;
            enableFileLogging.IsChecked = s.enableFileLogging;
            displayAll.IsChecked = s.displayAll;
            displayInfo.IsChecked = s.displayInfo;
            enablePreview.IsChecked = s.enablePreview;
            enableModelPreview.IsChecked = s.enableModelPreview;
            modelsOnly.IsChecked = s.modelsOnly;
            enableResolveDependencies.IsChecked = s.enableResolveDependencies;
            allowDuplicates.IsChecked = s.allowDuplicates;
            skipContainer.IsChecked = s.skipContainer;
            assetsManager.ResolveDependencies = s.enableResolveDependencies;
            SkipContainer = s.skipContainer;
            MiHoYoBinData.Encrypted = s.encrypted;
            MiHoYoBinData.Key = s.key;
            AssetsHelper.Minimal = s.minimalAssetMap;
        }

        private void InitializeLogger()
        {
            logger = new GUILogger(OnLog) { WriteToConsole = Settings.Default.enableConsole, ShowErrorMessage = showErrorMessage.IsChecked };
            Logger.Default = logger;
            var loggerEventType = (LoggerEvent)Settings.Default.loggerEventType;
            loggedEventsMenu.Items.Clear();
            foreach (var loggerEvent in Enum.GetValues<LoggerEvent>().ToArray()[1..^1])
            {
                var menuItem = new MenuItem { Header = loggerEvent.ToString(), ToggleType = MenuItemToggleType.CheckBox, IsChecked = loggerEventType.HasFlag(loggerEvent), Tag = (int)loggerEvent };
                menuItem.Click += LoggedEvent_Click;
                loggedEventsMenu.Items.Add(menuItem);
            }
            Logger.Flags = loggerEventType;
            Logger.FileLogging = Settings.Default.enableFileLogging;
        }

        private void InitializeProgressBar()
        {
            Progress.Default = new SyncProgress(SetProgressBarValue);
            Studio.StatusStripUpdate = StatusStripUpdate;
        }

        private void InitializeOptions()
        {
            var assetMapType = (ExportListType)Settings.Default.assetMapType;
            assetMapTypeMenu.Items.Clear();
            foreach (var mapType in Enum.GetValues<ExportListType>().ToArray()[1..])
            {
                var menuItem = new MenuItem { Header = mapType.ToString(), ToggleType = MenuItemToggleType.CheckBox, IsChecked = assetMapType.HasFlag(mapType), Tag = (int)mapType };
                menuItem.Click += AssetMapType_Click;
                assetMapTypeMenu.Items.Add(menuItem);
            }

            var games = GameManager.GetGames();
            suppressGameChange = true;
            gameComboBox.ItemsSource = games.Select(x => x.ToString()).ToArray();
            var selectedGame = Math.Clamp(Settings.Default.selectedGame, 0, games.Length - 1);
            gameComboBox.SelectedIndex = selectedGame;
            suppressGameChange = false;

            Studio.Game = GameManager.GetGame(selectedGame);
            TypeFlags.SetTypes(JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(Settings.Default.types));
            Logger.Info($"Target Game type is {Studio.Game.Type}");

            if (Studio.Game.Type.IsUnityCN())
            {
                UnityCNManager.SetKey(Settings.Default.selectedUnityCNKey);
            }
            UpdateAIVersionVisibility();
            UpdateVersionList();

            if (!string.IsNullOrEmpty(Settings.Default.selectedCABMapName))
            {
                if (!AssetsHelper.LoadCABMapInternal(Settings.Default.selectedCABMapName))
                {
                    Settings.Default.selectedCABMapName = "";
                    Settings.Default.Save();
                }
            }
            UpdateCABMapLabel();
        }

        private void ReportNativeLibraries()
        {
            if (!NativeLibraries.FmodAvailable)
            {
                Logger.Info($"FMOD not found ({NativeLibraries.FmodLibraryFileName}); AudioClips are decoded with the built-in FSB5 decoder (OGG/WAV).");
            }
            if (!NativeLibraries.FbxAvailable)
            {
                Logger.Info($"FBX exporter not found ({Path.Combine(NativeLibraries.NativeDirectory, NativeLibraries.FbxLibraryFileName)}); FBX model export is unavailable.");
            }
            StatusStripUpdate("Ready - drop Unity files here or use File > Load file / Load folder");
        }

        #endregion

        #region Logging / status

        private void OnLog(LoggerEvent loggerEvent, string message)
        {
            lock (logBuffer)
            {
                logBuffer.Append('[').Append(loggerEvent).Append("] ").Append(message).Append('\n');
                if (!logFlushScheduled)
                {
                    logFlushScheduled = true;
                    // batch log lines: re-laying out a large TextBox for every line would freeze the UI
                    Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(FlushLog, TimeSpan.FromMilliseconds(250)), DispatcherPriority.Background);
                }
            }
            if (loggerEvent == LoggerEvent.Error && logger.ShowErrorMessage)
            {
                // Only one error dialog at a time; everything else is in the log panel.
                if (Interlocked.CompareExchange(ref errorDialogOpen, 1, 0) == 0)
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        try
                        {
                            await Dialogs.MessageAsync(this, "Error", message + "\n\n(Further errors while this dialog is open are only written to the log panel.)");
                        }
                        finally
                        {
                            Interlocked.Exchange(ref errorDialogOpen, 0);
                        }
                    });
                }
                StatusStripUpdate("Error: " + message);
            }
            else if (loggerEvent == LoggerEvent.Info || loggerEvent == LoggerEvent.Warning)
            {
                StatusStripUpdate(message);
            }
        }

        private void FlushLog()
        {
            string text;
            lock (logBuffer)
            {
                text = logBuffer.ToString();
                logBuffer.Clear();
                logFlushScheduled = false;
            }
            var lines = text.TrimEnd('\n').Split('\n');
            // keep only what will survive the cap
            var start = Math.Max(0, lines.Length - MaxLogLines);
            var overflow = logLines.Count + (lines.Length - start) - MaxLogLines;
            if (overflow > 0)
                logLines.RemoveRange(0, Math.Min(overflow, logLines.Count));
            logLines.AddRange(lines.Skip(start));
            if (logLines.Count > 0)
                logList.ScrollIntoView(logLines.Count - 1);
        }

        private void LogList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                var selected = logList.SelectedItems?.OfType<string>().ToList();
                var text = string.Join("\n", selected?.Count > 0 ? selected : logLines);
                _ = Clipboard?.SetTextAsync(text);
            }
        }

        private void StatusStripUpdate(string statusText)
        {
            var firstLine = statusText?.Split('\n')[0] ?? "";
            lock (logBuffer)
            {
                pendingStatus = firstLine;
                if (statusFlushScheduled)
                    return;
                statusFlushScheduled = true;
            }
            Dispatcher.UIThread.Post(() =>
            {
                lock (logBuffer)
                {
                    statusLabel.Text = pendingStatus;
                    statusFlushScheduled = false;
                }
            }, DispatcherPriority.Background);
        }

        private void SetProgressBarValue(int value)
        {
            lock (logBuffer)
            {
                pendingProgress = value;
                if (progressFlushScheduled)
                    return;
                progressFlushScheduled = true;
            }
            Dispatcher.UIThread.Post(() =>
            {
                lock (logBuffer)
                {
                    progressBar.Value = pendingProgress;
                    progressFlushScheduled = false;
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>IProgress that reports synchronously (System.Progress posts to a captured context).</summary>
        private sealed class SyncProgress : IProgress<int>
        {
            private readonly Action<int> handler;
            public SyncProgress(Action<int> handler) => this.handler = handler;
            public void Report(int value) => handler(value);
        }

        private void UpdateAssetCountStatus()
        {
            var selectedCount = assetListView.SelectedItems?.Count ?? 0;
            var filteredCount = visibleAssets.Count;
            var totalCount = exportableAssets.Count;
            string statusText;
            if (selectedCount > 0)
            {
                statusText = $"Selected: {selectedCount}";
                if (filteredCount != totalCount)
                    statusText += $" | Filtered: {filteredCount}";
                statusText += $" | Total: {totalCount}";
            }
            else if (filteredCount != totalCount)
            {
                statusText = $"Filtered: {filteredCount} | Total: {totalCount}";
            }
            else
            {
                statusText = totalCount > 0 ? $"Total: {totalCount}" : "";
            }
            assetCountLabel.Text = statusText;
        }

        #endregion

        #region Loading

        private void AssetsManager_OnVersionPrompt(object sender, VersionPromptEventArgs e)
        {
            // Raised from a worker thread while loading; block it until the user answers.
            string version = null;
            Func<Task> ask = async () =>
            {
                version = await Dialogs.InputAsync(this, "Unity Version Required",
                    $"Unable to detect Unity version for:\n{e.FileName}\n\nPlease enter the Unity version:",
                    "2020.3.48f1", "Example: 2020.3.48f1 or 6000.0.58f2");
            };
            if (Dispatcher.UIThread.CheckAccess())
            {
                e.Cancelled = true;
                return;
            }
            Dispatcher.UIThread.InvokeAsync(ask).GetAwaiter().GetResult();

            if (!string.IsNullOrWhiteSpace(version))
            {
                version = version.Trim();
                e.UserProvidedVersion = version;
                e.Cancelled = false;
                assetsManager.SpecifyUnityVersion = version;
                Dispatcher.UIThread.Post(() => unityVersionTextBox.Text = version);
            }
            else
            {
                e.Cancelled = true;
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = Dialogs.IsFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(x => x.TryGetLocalPath()).Where(x => x != null).ToArray();
            if (paths?.Length > 0)
            {
                LoadPaths(paths);
            }
        }

        public async void LoadPaths(params string[] paths)
        {
            ResetForm();
            PrepareAssetsManager();
            if (paths.Length == 1 && Directory.Exists(paths[0]))
            {
                await Task.Run(() => assetsManager.LoadFolder(paths[0]));
            }
            else
            {
                await Task.Run(() => assetsManager.LoadFiles(paths));
            }
            await BuildAssetStructures();
        }

        private void PrepareAssetsManager()
        {
            assetsManager.SpecifyUnityVersion = unityVersionTextBox.Text?.Trim() ?? "";
            assetsManager.Game = Studio.Game;
        }

        private async void LoadFile_Click(object sender, RoutedEventArgs e)
        {
            var paths = await Dialogs.PickFilesAsync(this, "Load file(s)", true, Settings.Default.lastOpenDirectory);
            if (paths.Length == 0)
                return;
            ResetForm();
            RememberOpenDirectory(Path.GetDirectoryName(paths[0]));
            PrepareAssetsManager();
            if (paths.Length == 1 && File.Exists(paths[0]) && Path.GetExtension(paths[0]) == ".txt")
            {
                paths = File.ReadAllLines(paths[0]).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            }
            await Task.Run(() => assetsManager.LoadFiles(paths));
            await BuildAssetStructures();
        }

        private async void LoadFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = await Dialogs.PickFolderAsync(this, "Load folder", Settings.Default.lastOpenDirectory);
            if (folder == null)
                return;
            ResetForm();
            RememberOpenDirectory(folder);
            PrepareAssetsManager();
            await Task.Run(() => assetsManager.LoadFolder(folder));
            await BuildAssetStructures();
        }

        private void RememberOpenDirectory(string dir)
        {
            Settings.Default.lastOpenDirectory = dir;
            Settings.Default.Save();
        }

        private void RememberSaveDirectory(string dir)
        {
            Settings.Default.lastSaveDirectory = dir;
            Settings.Default.Save();
        }

        private async void ExtractFile_Click(object sender, RoutedEventArgs e)
        {
            var files = await Dialogs.PickFilesAsync(this, "Select file(s) to extract", true, Settings.Default.lastOpenDirectory);
            if (files.Length == 0)
                return;
            var savePath = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (savePath == null)
                return;
            RememberSaveDirectory(savePath);
            var extractedCount = await Task.Run(() => ExtractFile(files, savePath));
            StatusStripUpdate($"Finished extracting {extractedCount} files.");
        }

        private async void ExtractFolder_Click(object sender, RoutedEventArgs e)
        {
            var path = await Dialogs.PickFolderAsync(this, "Select folder to extract", Settings.Default.lastOpenDirectory);
            if (path == null)
                return;
            var savePath = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (savePath == null)
                return;
            RememberSaveDirectory(savePath);
            var extractedCount = await Task.Run(() => ExtractFolder(path, savePath));
            StatusStripUpdate($"Finished extracting {extractedCount} files.");
        }

        private async Task BuildAssetStructures()
        {
            if (assetsManager.assetsFileList.Count == 0)
            {
                StatusStripUpdate("No Unity file can be loaded.");
                return;
            }

            (var productName, var treeNodeCollection) = await Task.Run(BuildAssetData);
            var typeMap = await Task.Run(BuildClassStructure);

            if (string.IsNullOrEmpty(productName))
            {
                if (!Studio.Game.Type.IsNormal())
                {
                    productName = Studio.Game.Name;
                }
                else if (Studio.Game.Type.IsUnityCN() && UnityCNManager.TryGetEntry(Settings.Default.selectedUnityCNKey, out var unityCN))
                {
                    productName = unityCN.Name;
                }
                else
                {
                    productName = "no productName";
                }
            }

            Title = $"{AppTitle} - {productName} - {assetsManager.assetsFileList[0].unityVersion} - {assetsManager.assetsFileList[0].m_TargetPlatform}";

            assetListView.ItemsSource = visibleAssets;

            sceneRoots = treeNodeCollection;
            sceneTreeView.ItemsSource = sceneRoots;

            classesListView.ItemsSource = typeMap.SelectMany(v => v.Value.Values).ToList();

            RebuildFilterTypeMenu();

            var log = $"Finished loading {assetsManager.assetsFileList.Count} files with {exportableAssets.Count} exportable assets";
            var m_ObjectsCount = assetsManager.assetsFileList.Sum(x => x.m_Objects.Count);
            var objectsCount = assetsManager.assetsFileList.Sum(x => x.Objects.Count);
            if (m_ObjectsCount != objectsCount)
            {
                log += $" and {m_ObjectsCount - objectsCount} assets failed to read";
            }
            if (Settings.Default.modelsOnly)
            {
                FilterAssetList();
            }
            StatusStripUpdate(log);
            UpdateAssetCountStatus();
        }

        private void RebuildFilterTypeMenu()
        {
            while (filterTypeMenu.Items.Count > 1)
            {
                filterTypeMenu.Items.RemoveAt(1);
            }
            filterAll.IsChecked = true;
            var types = exportableAssets.Select(x => x.Type).Distinct().OrderBy(x => x.ToString()).ToArray();
            foreach (var type in types)
            {
                var typeItem = new MenuItem { Header = type.ToString(), ToggleType = MenuItemToggleType.CheckBox, Tag = type };
                typeItem.Click += FilterType_Click;
                filterTypeMenu.Items.Add(typeItem);
            }
        }

        public void ResetForm()
        {
            Title = AppTitle;
            // unbind views before the underlying lists are cleared
            assetListView.ItemsSource = null;
            classesListView.ItemsSource = null;
            assetsManager.Clear();
            assemblyLoader.Clear();
            exportableAssets.Clear();
            visibleAssets = exportableAssets;
            assetListView.ItemsSource = null;
            sceneRoots = new List<SceneNode>();
            sceneTreeView.ItemsSource = sceneRoots;
            classesListView.ItemsSource = null;
            treeSrcResults.Clear();
            nextGObject = 0;
            ClearPreview();
            imagePreview.Source = null;
            imageTexture?.Dispose();
            imageTexture = null;
            dumpTextBox.Text = "";
            lastSelectedItem = null;
            sortMember = null;
            reverseSort = false;
            listSearch.Text = string.Empty;
            assetCountLabel.Text = "";
            progressBar.Value = 0;
            while (filterTypeMenu.Items.Count > 1)
            {
                filterTypeMenu.Items.RemoveAt(1);
            }
            filterAll.IsChecked = true;
            StatusStripUpdate("Ready");
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            ResetForm();
            AssetsHelper.Clear();
            UpdateCABMapLabel();
            PrepareAssetsManager();
        }

        private void Abort_Click(object sender, RoutedEventArgs e)
        {
            Logger.Info("Aborting....");
            assetsManager.tokenSource.Cancel();
            AssetsHelper.tokenSource.Cancel();
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        #endregion

        #region Options

        private void DisplayAll_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.displayAll = displayAll.IsChecked;
            Settings.Default.Save();
        }

        private void EnablePreview_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.enablePreview = enablePreview.IsChecked;
            Settings.Default.Save();
            ClearPreview();
            if (lastSelectedItem != null && enablePreview.IsChecked)
            {
                PreviewAsset(lastSelectedItem);
            }
        }

        private void EnableModelPreview_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.enableModelPreview = enableModelPreview.IsChecked;
            Settings.Default.Save();
        }

        private void DisplayInfo_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.displayInfo = displayInfo.IsChecked;
            Settings.Default.Save();
            assetInfoBorder.IsVisible = displayInfo.IsChecked && !string.IsNullOrEmpty(assetInfoLabel.Text);
        }

        private void ModelsOnly_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.modelsOnly = modelsOnly.IsChecked;
            Settings.Default.Save();
            if (exportableAssets.Count > 0)
            {
                FilterAssetList();
            }
        }

        private void ResolveDependencies_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.enableResolveDependencies = enableResolveDependencies.IsChecked;
            Settings.Default.Save();
            assetsManager.ResolveDependencies = enableResolveDependencies.IsChecked;
        }

        private void AllowDuplicates_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.allowDuplicates = allowDuplicates.IsChecked;
            Settings.Default.Save();
        }

        private void SkipContainer_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.skipContainer = skipContainer.IsChecked;
            Settings.Default.Save();
            SkipContainer = skipContainer.IsChecked;
        }

        private async void ExportOptions_Click(object sender, RoutedEventArgs e)
        {
            var window = new ExportOptionsWindow();
            await window.ShowDialog(this);
            if (window.Resetted)
            {
                InitializeExportOptions();
                InitializeLogger();
                InitializeOptions();
            }
        }

        private async void UnityCNKeys_Click(object sender, RoutedEventArgs e)
        {
            await new UnityCNWindow().ShowDialog(this);
        }

        private async void LoadAssemblyFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = await Dialogs.PickFolderAsync(this, "Select Assembly Folder (Managed DLLs)", Settings.Default.lastOpenDirectory);
            if (folder != null)
            {
                assemblyLoader.Clear();
                await Task.Run(() => assemblyLoader.Load(folder));
                Logger.Info($"Loaded assemblies from {folder}");
            }
        }

        private void Game_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressGameChange || gameComboBox.SelectedIndex < 0)
                return;
            Settings.Default.selectedGame = gameComboBox.SelectedIndex;
            Settings.Default.Save();

            ResetForm();

            Studio.Game = GameManager.GetGame(Settings.Default.selectedGame);
            Logger.Info($"Target Game is {Studio.Game.Name}");

            if (Studio.Game.Type.IsUnityCN())
            {
                UnityCNManager.SetKey(Settings.Default.selectedUnityCNKey);
            }
            UpdateAIVersionVisibility();
            PrepareAssetsManager();
        }

        private void UpdateAIVersionVisibility()
        {
            var visible = Studio.Game.Type.IsGISubGroup();
            aiVersionComboBox.IsVisible = visible;
            aiVersionLabel.IsVisible = visible;
        }

        private async void AIVersion_DropDownOpened(object sender, EventArgs e)
        {
            if (await AIVersionManager.FetchVersions())
            {
                UpdateVersionList();
            }
        }

        private void UpdateVersionList()
        {
            suppressAIChange = true;
            var selectedIndex = Math.Max(0, aiVersionComboBox.SelectedIndex);
            var items = new List<string> { "None" };
            items.AddRange(AIVersionManager.GetVersions().Select(v => v.Item1 + (v.Item2 ? " (cached)" : "")));
            aiVersionComboBox.ItemsSource = items;
            aiVersionComboBox.SelectedIndex = Math.Min(selectedIndex, items.Count - 1);
            suppressAIChange = false;
        }

        private async void AIVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressAIChange || aiVersionComboBox.SelectedIndex <= 0)
                return;
            if (skipContainer.IsChecked)
            {
                Logger.Info("Skip container is enabled, aborting...");
                return;
            }
            var version = aiVersionComboBox.SelectedItem.ToString();
            if (version.Contains(' '))
            {
                version = version.Split(' ')[0];
            }
            Logger.Info($"Loading AI v{version}");
            aiVersionComboBox.IsEnabled = false;
            try
            {
                var path = await AIVersionManager.FetchAI(version);
                await Task.Run(() => ResourceIndex.FromFile(path));
                UpdateContainers();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load AI v{version}: {ex.Message}");
            }
            UpdateVersionList();
            aiVersionComboBox.IsEnabled = true;
        }

        #endregion

        #region Misc (maps / AI)

        private void UpdateCABMapLabel()
        {
            var name = Settings.Default.selectedCABMapName;
            cabMapLabel.Text = string.IsNullOrEmpty(name) ? "" : $"CABMap: {name}";
        }

        private async Task<string> AskMapName(string title)
        {
            var maps = AssetsHelper.GetMaps();
            var name = await Dialogs.InputAsync(this, title, "CABMap name:", Settings.Default.selectedCABMapName, "Existing maps: " + (maps.Length > 0 ? string.Join(", ", maps) : "none"), maps);
            if (name == null)
                return null;
            name = name.Trim();
            if (string.IsNullOrEmpty(name))
            {
                Logger.Error("Map name is empty");
                return null;
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) != -1)
            {
                Logger.Warning("Name has invalid characters !!");
                return null;
            }
            return name;
        }

        private ExportListType SelectedAssetMapType => (ExportListType)assetMapTypeMenu.Items.OfType<MenuItem>().Where(x => x.IsChecked).Sum(x => (int)x.Tag);

        private async void SelectCABMap_Click(object sender, RoutedEventArgs e)
        {
            var maps = AssetsHelper.GetMaps();
            if (maps.Length == 0)
            {
                await Dialogs.MessageAsync(this, "CABMap", $"No CABMap found in {Path.GetFullPath(AssetsHelper.MapName)}. Build one first.");
                return;
            }
            var name = await Dialogs.InputAsync(this, "Select CABMap", "CABMap name:", Settings.Default.selectedCABMapName, "Available: " + string.Join(", ", maps), maps);
            if (string.IsNullOrWhiteSpace(name))
                return;
            miscMenu.IsEnabled = false;
            ResetForm();
            var ok = await Task.Run(() => AssetsHelper.LoadCABMapInternal(name.Trim()));
            if (ok)
            {
                Settings.Default.selectedCABMapName = name.Trim();
                Settings.Default.Save();
            }
            UpdateCABMapLabel();
            PrepareAssetsManager();
            miscMenu.IsEnabled = true;
        }

        private async void BuildMap_Click(object sender, RoutedEventArgs e)
        {
            var name = await AskMapName("Build CABMap");
            if (name == null)
                return;
            if (File.Exists(Path.Combine(AssetsHelper.MapName, $"{name}.bin")) &&
                !await Dialogs.ConfirmAsync(this, "Warning", "Map already exist, Do you want to override it ?"))
                return;
            var folder = await Dialogs.PickFolderAsync(this, "Select Game Folder", Settings.Default.lastOpenDirectory);
            if (folder == null)
                return;
            miscMenu.IsEnabled = false;
            Logger.Info("Scanning for files...");
            var files = await Task.Run(() => Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories));
            Logger.Info($"Found {files.Length} files");
            AssetsHelper.SetUnityVersion(unityVersionTextBox.Text?.Trim() ?? "");
            await Task.Run(() => AssetsHelper.BuildCABMap(files, name, folder, Studio.Game));
            miscMenu.IsEnabled = true;
        }

        private async void BuildBoth_Click(object sender, RoutedEventArgs e)
        {
            var name = await AskMapName("Build CABMap + AssetMap");
            if (name == null)
                return;
            if (File.Exists(Path.Combine(AssetsHelper.MapName, $"{name}.bin")) &&
                !await Dialogs.ConfirmAsync(this, "Warning", "Map already exist, Do you want to override it ?"))
                return;
            var folder = await Dialogs.PickFolderAsync(this, "Select Game Folder", Settings.Default.lastOpenDirectory);
            if (folder == null)
                return;
            var savePath = await Dialogs.PickFolderAsync(this, "Select Output Folder", Settings.Default.lastSaveDirectory);
            if (savePath == null)
                return;
            RememberSaveDirectory(savePath);
            miscMenu.IsEnabled = false;
            var exportListType = SelectedAssetMapType;
            Logger.Info("Scanning for files...");
            var files = await Task.Run(() => Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories));
            Logger.Info($"Found {files.Length} files");
            AssetsHelper.SetUnityVersion(unityVersionTextBox.Text?.Trim() ?? "");
            await Task.Run(() => AssetsHelper.BuildBoth(files, name, folder, Studio.Game, savePath, exportListType));
            miscMenu.IsEnabled = true;
        }

        private async void ClearMap_Click(object sender, RoutedEventArgs e)
        {
            var maps = AssetsHelper.GetMaps();
            var name = await Dialogs.InputAsync(this, "Delete CABMap", "CABMap to delete:", Settings.Default.selectedCABMapName, null, maps);
            if (string.IsNullOrWhiteSpace(name))
                return;
            var path = Path.Combine(AssetsHelper.MapName, $"{name.Trim()}.bin");
            if (!File.Exists(path))
            {
                Logger.Warning($"{name} does not exist");
                return;
            }
            if (!await Dialogs.ConfirmAsync(this, "Warning", "Map will be deleted, this can't be undone, continue ?"))
                return;
            File.Delete(path);
            Logger.Info($"{name} deleted successfully !!");
            if (Settings.Default.selectedCABMapName == name.Trim())
            {
                Settings.Default.selectedCABMapName = "";
                Settings.Default.Save();
                AssetsHelper.Clear();
                UpdateCABMapLabel();
            }
        }

        private async void LoadCABMap_Click(object sender, RoutedEventArgs e)
        {
            var files = await Dialogs.PickFilesAsync(this, "Load CABMap", false, null, new FilePickerFileType("CABMap File") { Patterns = new[] { "*.bin" } });
            if (files.Length == 0)
                return;
            miscMenu.IsEnabled = false;
            await Task.Run(() => AssetsHelper.LoadCABMap(files[0]));
            miscMenu.IsEnabled = true;
        }

        private async void BuildAssetMap_Click(object sender, RoutedEventArgs e)
        {
            var name = await Dialogs.InputAsync(this, "Build AssetMap", "AssetMap name:", "assets_map");
            if (name == null)
                return;
            name = string.IsNullOrWhiteSpace(name) ? "assets_map" : name.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) != -1)
            {
                Logger.Warning("Name has invalid characters !!");
                return;
            }
            var folder = await Dialogs.PickFolderAsync(this, "Select Game Folder", Settings.Default.lastOpenDirectory);
            if (folder == null)
                return;
            var savePath = await Dialogs.PickFolderAsync(this, "Select Output Folder", Settings.Default.lastSaveDirectory);
            if (savePath == null)
                return;
            RememberSaveDirectory(savePath);
            miscMenu.IsEnabled = false;
            var exportListType = SelectedAssetMapType;
            Logger.Info("Scanning for files...");
            var files = await Task.Run(() => Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories));
            Logger.Info($"Found {files.Length} files");
            AssetsHelper.SetUnityVersion(unityVersionTextBox.Text?.Trim() ?? "");
            await Task.Run(() => AssetsHelper.BuildAssetMap(files, name, Studio.Game, savePath, exportListType));
            miscMenu.IsEnabled = true;
        }

        private void AssetMapType_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.assetMapType = (int)SelectedAssetMapType;
            Settings.Default.Save();
        }

        private async void LoadAI_Click(object sender, RoutedEventArgs e)
        {
            if (skipContainer.IsChecked)
            {
                Logger.Info("Skip container is enabled, aborting...");
                return;
            }
            var files = await Dialogs.PickFilesAsync(this, "Load asset index", false, null, new FilePickerFileType("Asset Index JSON File") { Patterns = new[] { "*.json" } });
            if (files.Length == 0)
                return;
            Logger.Info("Loading AI...");
            await Task.Run(() => ResourceIndex.FromFile(files[0]));
            UpdateContainers();
        }

        private AssetBrowserWindow assetBrowser;

        private void AssetBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (assetBrowser == null)
            {
                assetBrowser = new AssetBrowserWindow(this);
                assetBrowser.Closed += (_, _) => assetBrowser = null;
                assetBrowser.Show(this);
            }
            else
            {
                assetBrowser.Activate();
            }
        }

        private void UpdateContainers()
        {
            // AssetItem raises PropertyChanged, so the grid refreshes itself.
            Studio.UpdateContainers();
        }

        #endregion

        #region Debug

        private void ShowErrorMessage_Click(object sender, RoutedEventArgs e)
        {
            logger.ShowErrorMessage = showErrorMessage.IsChecked;
        }

        private void EnableConsole_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.enableConsole = enableConsole.IsChecked;
            Settings.Default.Save();
            logger.WriteToConsole = enableConsole.IsChecked;
        }

        private void EnableFileLogging_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.enableFileLogging = enableFileLogging.IsChecked;
            Settings.Default.Save();
            Logger.FileLogging = enableFileLogging.IsChecked;
        }

        private void LoggedEvent_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.loggerEventType = loggedEventsMenu.Items.OfType<MenuItem>().Where(x => x.IsChecked).Sum(x => (int)x.Tag);
            Settings.Default.Save();
            Logger.Flags = (LoggerEvent)Settings.Default.loggerEventType;
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            logLines.Clear();
        }

        private async void ExportClassStructures_Click(object sender, RoutedEventArgs e)
        {
            if (classesListView.ItemsSource is not List<TypeTreeItem> items || items.Count == 0)
                return;
            var savePath = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (savePath == null)
                return;
            await Task.Run(() =>
            {
                var count = items.Count;
                int i = 0;
                Progress.Reset();
                foreach (var item in items)
                {
                    var versionPath = Path.Combine(savePath, item.Version);
                    Directory.CreateDirectory(versionPath);
                    var saveFile = Path.Combine(versionPath, Exporter.FixFileName($"{item.TypeID} {item.Text}.txt"));
                    File.WriteAllText(saveFile, item.Dump());
                    Progress.Report(++i, count);
                }
            });
            StatusStripUpdate("Finished exporting class structures");
        }

        #endregion

        #region Tabs, tree & list

        private void LeftTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != leftTabs)
                return;
            switch (leftTabs.SelectedIndex)
            {
                case 0:
                    treeSearch?.Focus();
                    break;
                case 1:
                    listSearch?.Focus();
                    UpdateAssetCountStatus();
                    break;
            }
        }

        private void RightTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != rightTabs)
                return;
            if (rightTabs.SelectedIndex == 1 && lastSelectedItem != null)
            {
                UpdateDump();
            }
        }

        private async void UpdateDump()
        {
            var item = lastSelectedItem;
            if (item == null)
                return;
            if (item.Asset is MonoBehaviour m_MonoBehaviour && m_MonoBehaviour.Dump() == null)
            {
                await EnsureAssemblyLoaderAsync();
            }
            try
            {
                var text = await Task.Run(() => DumpAsset(item.Asset));
                if (item == lastSelectedItem)
                {
                    dumpTextBox.Text = text;
                }
            }
            catch (Exception ex)
            {
                dumpTextBox.Text = $"Unable to dump asset: {ex.Message}";
            }
        }

        private void TreeSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            treeSrcResults.Clear();
            nextGObject = 0;
        }

        private void TreeSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || string.IsNullOrEmpty(treeSearch.Text))
                return;
            e.Handled = true;
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

            if (treeSrcResults.Count == 0)
            {
                Regex regex;
                try
                {
                    regex = new Regex(treeSearch.Text, RegexOptions.IgnoreCase);
                }
                catch (Exception ex)
                {
                    Logger.Error("Invalid Regex.\n" + ex.Message);
                    return;
                }
                foreach (var node in sceneRoots)
                {
                    TreeNodeSearch(regex, node);
                }
                if (treeSrcResults.Count == 0)
                {
                    StatusStripUpdate("No match found.");
                    return;
                }
            }

            if (shift)
            {
                foreach (var node in treeSrcResults)
                {
                    var tempNode = node;
                    if (alt)
                    {
                        while (tempNode.Parent != null)
                            tempNode = tempNode.Parent;
                    }
                    tempNode.EnsureVisible();
                    tempNode.IsChecked = control;
                }
                SelectTreeNode(treeSrcResults[0]);
                StatusStripUpdate($"{treeSrcResults.Count} matches.");
            }
            else
            {
                if (nextGObject >= treeSrcResults.Count)
                    nextGObject = 0;
                var node = treeSrcResults[nextGObject];
                if (alt)
                {
                    while (node.Parent != null)
                        node = node.Parent;
                }
                node.EnsureVisible();
                node.IsChecked = control;
                SelectTreeNode(treeSrcResults[nextGObject]);
                StatusStripUpdate($"Match {nextGObject + 1} / {treeSrcResults.Count}");
                nextGObject++;
            }
        }

        private void TreeNodeSearch(Regex regex, SceneNode treeNode)
        {
            if (regex.IsMatch(treeNode.Text))
                treeSrcResults.Add(treeNode);
            foreach (var node in treeNode.Nodes)
                TreeNodeSearch(regex, node);
        }

        private void SelectTreeNode(SceneNode node)
        {
            node.EnsureVisible();
            sceneTreeView.SelectedItem = node;
            Dispatcher.UIThread.Post(() =>
            {
                var container = sceneTreeView.GetVisualDescendants().OfType<TreeViewItem>().FirstOrDefault(x => x.DataContext == node);
                container?.BringIntoView();
            }, DispatcherPriority.Loaded);
        }

        private void ListSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                FilterAssetList();
            }
        }

        private void FilterType_Click(object sender, RoutedEventArgs e)
        {
            var typeItem = (MenuItem)sender;
            if (typeItem != filterAll)
            {
                filterAll.IsChecked = !filterTypeMenu.Items.OfType<MenuItem>().Skip(1).Any(x => x.IsChecked);
            }
            else
            {
                filterAll.IsChecked = true;
                foreach (var item in filterTypeMenu.Items.OfType<MenuItem>().Skip(1))
                    item.IsChecked = false;
            }
            FilterAssetList();
        }

        private void FilterAssetList()
        {
            List<AssetItem> result;
            if (!filterAll.IsChecked)
            {
                var show = filterTypeMenu.Items.OfType<MenuItem>().Skip(1).Where(x => x.IsChecked).Select(x => (ClassIDType)x.Tag).ToHashSet();
                result = exportableAssets.FindAll(x => show.Contains(x.Type));
            }
            else
            {
                result = exportableAssets;
            }
            if (Settings.Default.modelsOnly)
            {
                result = result.FindAll(x => x.Asset switch
                {
                    GameObject m_GameObject => m_GameObject.HasModel(),
                    Animator m_Animator => m_Animator.m_GameObject.TryGet(out var gameObject) && gameObject.HasModel(),
                    _ => true,
                } || (x.Type != ClassIDType.Animator && x.Type != ClassIDType.GameObject));
            }
            if (!string.IsNullOrEmpty(listSearch.Text))
            {
                Regex regex;
                try
                {
                    regex = new Regex(listSearch.Text, RegexOptions.IgnoreCase);
                }
                catch (Exception ex)
                {
                    Logger.Error("Invalid Regex.\n" + ex.Message);
                    listSearch.Text = "";
                    return;
                }
                result = result.FindAll(x => regex.IsMatch(x.Text) || regex.IsMatch(x.Container) || regex.IsMatch(x.TypeString) || regex.IsMatch(x.m_PathID.ToString()));
            }
            visibleAssets = result;
            ApplySort();
            assetListView.ItemsSource = visibleAssets;
            UpdateAssetCountStatus();
        }

        private void AssetList_Sorting(object sender, DataGridColumnEventArgs e)
        {
            e.Handled = true;
            var member = e.Column.SortMemberPath;
            reverseSort = sortMember == member && !reverseSort;
            sortMember = member;
            if (ReferenceEquals(visibleAssets, exportableAssets))
            {
                // keep the unsorted master list untouched
                visibleAssets = new List<AssetItem>(exportableAssets);
            }
            ApplySort();
            assetListView.ItemsSource = null;
            assetListView.ItemsSource = visibleAssets;
            foreach (var column in assetListView.Columns)
            {
                column.Tag = null;
            }
            StatusStripUpdate($"Sorted by {sortMember} {(reverseSort ? "descending" : "ascending")}");
        }

        private void ApplySort()
        {
            if (sortMember == null)
                return;
            Comparison<AssetItem> comparison = sortMember switch
            {
                "FullSize" => (a, b) => a.FullSize.CompareTo(b.FullSize),
                "PathID" => (a, b) => a.m_PathID.CompareTo(b.m_PathID),
                "Container" => (a, b) => string.CompareOrdinal(a.Container, b.Container),
                "TypeString" => (a, b) => string.CompareOrdinal(a.TypeString, b.TypeString),
                _ => (a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase),
            };
            if (ReferenceEquals(visibleAssets, exportableAssets))
            {
                visibleAssets = new List<AssetItem>(exportableAssets);
            }
            if (reverseSort)
                visibleAssets.Sort((a, b) => comparison(b, a));
            else
                visibleAssets.Sort(comparison);
        }

        private void AssetList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                assetListView.SelectAll();
                UpdateAssetCountStatus();
            }
            else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && assetListView.SelectedItem is AssetItem item)
            {
                e.Handled = true;
                _ = Clipboard?.SetTextAsync(item.Text);
            }
        }

        private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAssetCountStatus();
            var item = assetListView.SelectedItem as AssetItem;
            if (item == null || item == lastSelectedItem)
                return;
            // Only preview the most recently selected item, like the WinForms ListView
            if (e.AddedItems.Count > 0 && e.AddedItems[e.AddedItems.Count - 1] is AssetItem added)
            {
                item = added;
            }
            SelectAsset(item);
        }

        private void SelectAsset(AssetItem item)
        {
            ClearPreview();
            lastSelectedItem = item;
            StatusStripUpdate($"{item.TypeString}: {item.Text}");
            if (rightTabs.SelectedIndex == 1)
            {
                UpdateDump();
            }
            if (enablePreview.IsChecked)
            {
                PreviewAsset(item);
            }
        }

        private void Classes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (classesListView.SelectedItem is TypeTreeItem item)
            {
                ClearPreview();
                rightTabs.SelectedIndex = 0;
                classTextBox.Text = item.Dump();
                classTextBox.IsVisible = true;
            }
        }

        private List<AssetItem> GetSelectedAssets()
        {
            return assetListView.SelectedItems?.OfType<AssetItem>().ToList() ?? new List<AssetItem>();
        }

        private void AssetContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var selected = GetSelectedAssets();
            if (selected.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            goToSceneHierarchyItem.IsVisible = selected.Count == 1 && selected[0].TreeNode != null;
            showOriginalFileItem.IsVisible = selected.Count == 1;
            copyContainerMenuItem.IsVisible = selected.Count == 1 && !string.IsNullOrEmpty(selected[0].Container);
            exportAnimatorContextItem.IsVisible = selected.Any(x => x.Type == ClassIDType.Animator) && selected.Any(x => x.Type == ClassIDType.AnimationClip);
        }

        private void CopyName_Click(object sender, RoutedEventArgs e)
        {
            var text = string.Join("\n", GetSelectedAssets().Select(x => x.Text));
            _ = Clipboard?.SetTextAsync(text);
        }

        private void CopyContainer_Click(object sender, RoutedEventArgs e)
        {
            var text = string.Join("\n", GetSelectedAssets().Select(x => x.Container));
            _ = Clipboard?.SetTextAsync(text);
        }

        private void GoToSceneHierarchy_Click(object sender, RoutedEventArgs e)
        {
            var asset = GetSelectedAssets().FirstOrDefault();
            if (asset?.TreeNode != null)
            {
                leftTabs.SelectedIndex = 0;
                SelectTreeNode(asset.TreeNode);
            }
        }

        private void ShowOriginalFile_Click(object sender, RoutedEventArgs e)
        {
            var asset = GetSelectedAssets().FirstOrDefault();
            if (asset == null)
                return;
            var path = asset.SourceFile.originalPath ?? asset.SourceFile.fullName;
            // Try a file manager that can highlight the file, otherwise open the containing folder.
            try
            {
                var info = new ProcessStartInfo("dbus-send") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var arg in new[] { "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", $"array:string:{new Uri(Path.GetFullPath(path)).AbsoluteUri}", "string:" })
                    info.ArgumentList.Add(arg);
                using var process = Process.Start(info);
                process.WaitForExit(3000);
                if (process.ExitCode == 0)
                    return;
            }
            catch
            {
                // fall back below
            }
            OpenFolderInExplorer(Path.GetDirectoryName(path));
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.O)
            {
                e.Handled = true;
                LoadFile_Click(this, null);
            }
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.O)
            {
                e.Handled = true;
                LoadFolder_Click(this, null);
            }
        }

        #endregion

        #region Preview

        private void ClearPreview()
        {
            StopAudio();
            imagePreviewHost.IsVisible = false;
            meshPreviewHost.IsVisible = false;
            meshRenderer?.Dispose();
            meshRenderer = null;
            textPreviewBox.IsVisible = false;
            textPreviewBox.Text = "";
            classTextBox.IsVisible = false;
            audioPanel.IsVisible = false;
            assetInfoBorder.IsVisible = false;
            assetInfoLabel.Text = null;
        }

        private void ShowInfo(AssetItem assetItem)
        {
            if (displayInfo.IsChecked && !string.IsNullOrEmpty(assetItem.InfoText))
            {
                assetInfoLabel.Text = assetItem.InfoText;
                assetInfoBorder.IsVisible = true;
            }
        }

        private void Preview_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.W && meshRenderer != null && meshPreviewHost.IsVisible)
            {
                e.Handled = true;
                meshRenderer.WireframeMode = (meshRenderer.WireframeMode + 1) % 3;
                ScheduleMeshRender();
                return;
            }
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || !imagePreviewHost.IsVisible)
                return;
            var index = e.Key switch
            {
                Key.B => 0,
                Key.G => 1,
                Key.R => 2,
                Key.A => 3,
                _ => -1,
            };
            if (index >= 0 && lastSelectedItem != null)
            {
                e.Handled = true;
                textureChannels[index] = !textureChannels[index];
                ClearPreview();
                PreviewAsset(lastSelectedItem);
            }
        }

        private async void PreviewAsset(AssetItem assetItem)
        {
            if (assetItem == null)
                return;
            try
            {
                switch (assetItem.Asset)
                {
                    case Texture2D m_Texture2D:
                        await PreviewTexture2D(assetItem, m_Texture2D);
                        break;
                    case AudioClip m_AudioClip:
                        PreviewAudioClip(assetItem, m_AudioClip);
                        break;
                    case Shader m_Shader:
                        await PreviewTextAsync(assetItem, () =>
                        {
                            if (m_Shader.byteSize > 0xFFFFFFF)
                                return "Shader is too large to parse";
                            var str = m_Shader.Convert();
                            return str ?? "Serialized Shader can't be read";
                        });
                        break;
                    case TextAsset m_TextAsset:
                        await PreviewTextAsync(assetItem, () => Encoding.UTF8.GetString(m_TextAsset.m_Script).Replace("\0", ""));
                        break;
                    case MonoBehaviour m_MonoBehaviour:
                        if (m_MonoBehaviour.ToType() == null)
                        {
                            await EnsureAssemblyLoaderAsync();
                        }
                        await PreviewTextAsync(assetItem, () =>
                        {
                            var obj = m_MonoBehaviour.ToType();
                            if (obj == null)
                            {
                                var type = MonoBehaviourToTypeTree(m_MonoBehaviour);
                                obj = m_MonoBehaviour.ToType(type);
                            }
                            return JsonConvert.SerializeObject(obj, Formatting.Indented);
                        });
                        break;
                    case Font m_Font:
                        await PreviewFont(assetItem, m_Font);
                        break;
                    case Mesh m_Mesh:
                        PreviewMesh(assetItem, m_Mesh);
                        break;
                    case VideoClip _:
                    case MovieTexture _:
                        StatusStripUpdate("Only supported export.");
                        break;
                    case Sprite m_Sprite:
                        await PreviewSprite(assetItem, m_Sprite);
                        break;
                    case AnimationClip m_AnimationClip:
                        await PreviewTextAsync(assetItem, () =>
                        {
                            var str = m_AnimationClip.Convert();
                            return string.IsNullOrEmpty(str) ? "Legacy animation is not supported" : str;
                        });
                        break;
                    case MiHoYoBinData m_MiHoYoBinData:
                        await PreviewTextAsync(assetItem, () => m_MiHoYoBinData.AsString);
                        StatusStripUpdate("Can be exported/previewed as JSON if data is a valid JSON (check XOR).");
                        break;
                    case GameObject m_GameObject when Settings.Default.enableModelPreview:
                        await PreviewModel(assetItem, () => new ModelConverter(m_GameObject, PreviewModelOptions(), Array.Empty<AnimationClip>()));
                        break;
                    case Animator m_Animator when Settings.Default.enableModelPreview:
                        await PreviewModel(assetItem, () => new ModelConverter(m_Animator, PreviewModelOptions(), Array.Empty<AnimationClip>()));
                        break;
                    case GameObject m_GameObject:
                        PreviewGameObject(assetItem, m_GameObject);
                        break;
                    default:
                        await PreviewTextAsync(assetItem, () => assetItem.Asset.Dump());
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Preview {assetItem.Type}:{assetItem.Text} error\n{e.Message}\n{e.StackTrace}");
            }
        }

        private async Task PreviewTextAsync(AssetItem assetItem, Func<string> produce)
        {
            var text = await Task.Run(produce);
            if (assetItem != lastSelectedItem || text == null)
                return;
            const int limit = 5_000_000;
            if (text.Length > limit)
            {
                text = text[..limit] + "\n\n... (truncated, export the asset to see the full content)";
            }
            textPreviewBox.Text = text;
            textPreviewBox.IsVisible = true;
            ShowInfo(assetItem);
        }

        private async Task PreviewTexture2D(AssetItem assetItem, Texture2D m_Texture2D)
        {
            var channels = (bool[])textureChannels.Clone();
            var result = await Task.Run(() =>
            {
                var image = m_Texture2D.ConvertToImage(true);
                if (image == null)
                    return ((byte[])null, 0, 0);
                using (image)
                {
                    return (image.ConvertToBytes(), image.Width, image.Height);
                }
            });
            if (assetItem != lastSelectedItem)
                return;
            var (bytes, width, height) = result;
            if (bytes == null)
            {
                StatusStripUpdate("Unsupported image for preview");
                return;
            }
            var info = new StringBuilder();
            info.Append($"Width: {m_Texture2D.m_Width}\nHeight: {m_Texture2D.m_Height}\nFormat: {m_Texture2D.m_TextureFormat}");
            switch (m_Texture2D.m_TextureSettings.m_FilterMode)
            {
                case 0: info.Append("\nFilter Mode: Point "); break;
                case 1: info.Append("\nFilter Mode: Bilinear "); break;
                case 2: info.Append("\nFilter Mode: Trilinear "); break;
            }
            info.Append($"\nAnisotropic level: {m_Texture2D.m_TextureSettings.m_Aniso}\nMip map bias: {m_Texture2D.m_TextureSettings.m_MipBias}");
            switch (m_Texture2D.m_TextureSettings.m_WrapMode)
            {
                case 0: info.Append("\nWrap mode: Repeat"); break;
                case 1: info.Append("\nWrap mode: Clamp"); break;
            }
            info.Append("\nChannels: ");
            int validChannel = 0;
            for (int i = 0; i < 4; i++)
            {
                if (channels[i])
                {
                    info.Append(textureChannelNames[i]);
                    validChannel++;
                }
            }
            if (validChannel == 0)
                info.Append("None");
            if (validChannel != 4)
            {
                ApplyChannelMask(bytes, channels, validChannel);
            }
            assetItem.InfoText = info.ToString();
            ShowBitmap(bytes, width, height);
            ShowInfo(assetItem);
            StatusStripUpdate("'Ctrl'+'R'/'G'/'B'/'A' (click the preview first) for Channel Toggle");
        }

        private static void ApplyChannelMask(byte[] bytes, bool[] channels, int validChannel)
        {
            var fill = validChannel == 1 && channels[3] ? byte.MaxValue : byte.MinValue;
            for (int offset = 0; offset + 3 < bytes.Length; offset += 4)
            {
                if (!channels[0]) bytes[offset] = fill;
                if (!channels[1]) bytes[offset + 1] = fill;
                if (!channels[2]) bytes[offset + 2] = fill;
                if (!channels[3]) bytes[offset + 3] = byte.MaxValue;
            }
        }

        private async Task PreviewSprite(AssetItem assetItem, Sprite m_Sprite)
        {
            var result = await Task.Run(() =>
            {
                var image = m_Sprite.GetImage();
                if (image == null)
                    return ((byte[])null, 0, 0);
                using (image)
                {
                    return (image.ConvertToBytes(), image.Width, image.Height);
                }
            });
            if (assetItem != lastSelectedItem)
                return;
            var (bytes, width, height) = result;
            if (bytes == null)
            {
                StatusStripUpdate("Unsupported sprite for preview.");
                return;
            }
            assetItem.InfoText = $"Width: {width}\nHeight: {height}";
            ShowBitmap(bytes, width, height);
            ShowInfo(assetItem);
        }

        private void ShowBitmap(byte[] bgra, int width, int height)
        {
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var fb = bitmap.Lock())
            {
                var rowBytes = width * 4;
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
                }
            }
            imagePreview.Source = bitmap;
            imageTexture?.Dispose();
            imageTexture = bitmap;
            imagePreviewHost.IsVisible = true;
            previewGrid.Focus();
        }

        private void PreviewAudioClip(AssetItem assetItem, AudioClip m_AudioClip)
        {
            var info = new StringBuilder("Compression format: ");
            if (m_AudioClip.version[0] < 5)
            {
                info.Append(m_AudioClip.m_Type switch
                {
                    FMODSoundType.ACC => "Acc",
                    FMODSoundType.AIFF => "AIFF",
                    FMODSoundType.IT => "Impulse tracker",
                    FMODSoundType.MOD => "Protracker / Fasttracker MOD",
                    FMODSoundType.MPEG => "MP2/MP3 MPEG",
                    FMODSoundType.OGGVORBIS => "Ogg vorbis",
                    FMODSoundType.S3M => "ScreamTracker 3",
                    FMODSoundType.WAV => "Microsoft WAV",
                    FMODSoundType.XM => "FastTracker 2 XM",
                    FMODSoundType.XMA => "Xbox360 XMA",
                    FMODSoundType.VAG => "PlayStation Portable ADPCM",
                    FMODSoundType.AUDIOQUEUE => "iPhone",
                    _ => "Unknown",
                });
            }
            else
            {
                info.Append(m_AudioClip.m_CompressionFormat switch
                {
                    AudioCompressionFormat.PCM => "PCM",
                    AudioCompressionFormat.Vorbis => "Vorbis",
                    AudioCompressionFormat.ADPCM => "ADPCM",
                    AudioCompressionFormat.MP3 => "MP3",
                    AudioCompressionFormat.PSMVAG => "PlayStation Portable ADPCM",
                    AudioCompressionFormat.HEVAG => "PSVita ADPCM",
                    AudioCompressionFormat.XMA => "Xbox360 XMA",
                    AudioCompressionFormat.AAC => "AAC",
                    AudioCompressionFormat.GCADPCM => "Nintendo 3DS/Wii DSP",
                    AudioCompressionFormat.ATRAC9 => "PSVita ATRAC9",
                    _ => "Unknown",
                });
                info.Append($"\nChannels: {m_AudioClip.m_Channels}\nFrequency: {m_AudioClip.m_Frequency} Hz\nLength: {TimeSpan.FromSeconds(m_AudioClip.m_Length):m\\:ss\\.f}");
            }
            info.Append($"\nSize: {m_AudioClip.m_Size} bytes");
            assetItem.InfoText = info.ToString();
            audioInfoLabel.Text = info.ToString();
            var player = FindAudioPlayer();
            audioPlayButton.IsEnabled = player != null;
            audioStopButton.IsEnabled = player != null;
            audioHintLabel.Text = player == null
                ? "Install pw-play, paplay or ffplay to play audio."
                : NativeLibraries.FmodAvailable ? $"Player: {player} (FMOD)" : $"Player: {player} (decoded with Fmod5Sharp; FMOD not installed)";
            ShowInfo(assetItem);
            audioPanel.IsVisible = true;
        }

        private static string FindAudioPlayer()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var player in new[] { "pw-play", "paplay", "ffplay" })
            {
                foreach (var dir in path.Split(Path.PathSeparator))
                {
                    if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, player)))
                        return player;
                }
            }
            return null;
        }

        private async void AudioPlay_Click(object sender, RoutedEventArgs e)
        {
            if (lastSelectedItem?.Asset is not AudioClip m_AudioClip)
                return;
            StopAudio();
            var item = lastSelectedItem;
            var (data, extension) = await Task.Run(() =>
            {
                if (NativeLibraries.FmodAvailable)
                {
                    var wav = new AudioClipConverter(m_AudioClip).ConvertToWav();
                    if (wav != null)
                        return (wav, ".wav");
                }
                if (Fsb5Decoder.TryConvert(m_AudioClip, out var converted, out var ext))
                    return (converted, ext);
                return ((byte[])null, (string)null);
            });
            if (item != lastSelectedItem)
                return;
            if (data == null)
            {
                StatusStripUpdate("Unable to decode this audio clip.");
                return;
            }
            var player = FindAudioPlayer();
            if (player == null)
                return;
            audioTempFile = Path.Combine(Path.GetTempPath(), $"assetstudio_preview_{Environment.ProcessId}{extension}");
            await File.WriteAllBytesAsync(audioTempFile, data);
            var info = new ProcessStartInfo(player) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (player == "ffplay")
            {
                info.ArgumentList.Add("-nodisp");
                info.ArgumentList.Add("-autoexit");
                info.ArgumentList.Add("-loglevel");
                info.ArgumentList.Add("quiet");
            }
            info.ArgumentList.Add(audioTempFile);
            try
            {
                audioProcess = Process.Start(info);
                StatusStripUpdate($"Playing with {player}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to start {player}: {ex.Message}");
            }
        }

        private void AudioStop_Click(object sender, RoutedEventArgs e) => StopAudio();

        private void StopAudio()
        {
            try
            {
                if (audioProcess != null && !audioProcess.HasExited)
                {
                    audioProcess.Kill();
                }
            }
            catch
            {
                // ignored
            }
            audioProcess?.Dispose();
            audioProcess = null;
            try
            {
                if (audioTempFile != null && File.Exists(audioTempFile))
                    File.Delete(audioTempFile);
            }
            catch
            {
                // ignored
            }
        }

        private async Task PreviewFont(AssetItem assetItem, Font m_Font)
        {
            if (m_Font.m_FontData == null || m_Font.m_FontData.Length < 4)
            {
                StatusStripUpdate("Unsupported font for preview. Try to export.");
                return;
            }
            var isOtf = m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79;
            try
            {
                var (bytes, width, height, familyName) = await Task.Run(() => FontPreview.Render(m_Font.m_FontData));
                if (assetItem != lastSelectedItem)
                    return;
                assetItem.InfoText = $"Family: {familyName}\nType: {(isOtf ? "OpenType (otf)" : "TrueType (ttf)")}\nSize: {m_Font.m_FontData.Length} bytes";
                ShowBitmap(bytes, width, height);
                ShowInfo(assetItem);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to preview font: {ex.Message}");
                StatusStripUpdate("Unsupported font for preview. Try to export.");
            }
        }

        private void PreviewMesh(AssetItem assetItem, Mesh m_Mesh)
        {
            var renderer = m_Mesh.m_VertexCount > 0 ? MeshRenderer.FromMesh(m_Mesh) : null;
            if (renderer == null)
            {
                StatusStripUpdate("Unable to preview this mesh");
                return;
            }
            assetItem.InfoText = $"Vertices: {m_Mesh.m_VertexCount}\nTriangles: {renderer.TriangleCount}\nSub meshes: {m_Mesh.m_SubMeshes.Count}" +
                $"\nUV0: {(m_Mesh.m_UV0?.Length > 0 ? "yes" : "no")}  Normals: {(m_Mesh.m_Normals?.Length > 0 ? "yes" : "no")}";
            ShowMesh(assetItem, renderer);
        }

        private ModelConverter.Options PreviewModelOptions() => new ModelConverter.Options()
        {
            imageFormat = Settings.Default.convertType,
            game = Studio.Game,
            collectAnimations = Settings.Default.collectAnimations,
            exportMaterials = false,
            materials = new HashSet<Material>(),
            uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(Settings.Default.uvs),
            texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(Settings.Default.texs),
        };

        private async Task PreviewModel(AssetItem assetItem, Func<ModelConverter> convert)
        {
            StatusStripUpdate("Building model preview...");
            var renderer = await Task.Run(() => MeshRenderer.FromModel(convert()));
            if (assetItem != lastSelectedItem)
                return;
            if (renderer == null)
            {
                StatusStripUpdate("Unable to preview this model");
                return;
            }
            assetItem.InfoText = $"Vertices: {renderer.VertexCount}\nTriangles: {renderer.TriangleCount}";
            ShowMesh(assetItem, renderer);
        }

        private void ShowMesh(AssetItem assetItem, MeshRenderer renderer)
        {
            if (meshRenderer != renderer)
                meshRenderer?.Dispose();
            meshRenderer = renderer;
            meshPreviewHost.IsVisible = true;
            ShowInfo(assetItem);
            ScheduleMeshRender();
            meshPreviewHost.Focus();
            StatusStripUpdate($"{renderer.BackendName} | Left drag = rotate | Right drag = move | Wheel = zoom | Ctrl+W = wireframe");
        }

        private void ScheduleMeshRender()
        {
            if (meshRenderScheduled)
                return;
            meshRenderScheduled = true;
            Dispatcher.UIThread.Post(RenderMesh, DispatcherPriority.Render);
        }

        private void RenderMesh()
        {
            meshRenderScheduled = false;
            if (meshRenderer == null || !meshPreviewHost.IsVisible)
                return;
            var scaling = VisualRoot?.RenderScaling ?? 1;
            var width = Math.Max(1, (int)(meshPreviewHost.Bounds.Width * scaling));
            var height = Math.Max(1, (int)(meshPreviewHost.Bounds.Height * scaling));
            if (meshPreviewHost.Bounds.Width < 2 || meshPreviewHost.Bounds.Height < 2)
            {
                width = 640;
                height = 480;
            }
            var pixels = meshRenderer.Render(width, height);
            if (meshBitmap == null || meshBitmap.PixelSize.Width != width || meshBitmap.PixelSize.Height != height)
            {
                meshBitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }
            using (var fb = meshBitmap.Lock())
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(pixels, y * width * 4, fb.Address + y * fb.RowBytes, width * 4);
                }
            }
            meshImage.Source = null;
            meshImage.Source = meshBitmap;
        }

        private void Mesh_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleMeshRender();

        private void Mesh_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            meshDragging = true;
            lastPointer = e.GetPosition(meshPreviewHost);
            meshPreviewHost.Focus();
            e.Pointer.Capture(meshPreviewHost);
        }

        private void Mesh_PointerMoved(object sender, PointerEventArgs e)
        {
            if (!meshDragging || meshRenderer == null)
                return;
            var point = e.GetCurrentPoint(meshPreviewHost);
            var delta = point.Position - lastPointer;
            lastPointer = point.Position;
            if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
            {
                var size = Math.Max(1, Math.Min(meshPreviewHost.Bounds.Width, meshPreviewHost.Bounds.Height));
                meshRenderer.Pan += new System.Numerics.Vector2((float)(delta.X / size * 2.2 / meshRenderer.Zoom), (float)(-delta.Y / size * 2.2 / meshRenderer.Zoom));
            }
            else
            {
                meshRenderer.Yaw -= (float)delta.X * 0.01f;
                meshRenderer.Pitch = Math.Clamp(meshRenderer.Pitch - (float)delta.Y * 0.01f, -MathF.PI / 2, MathF.PI / 2);
            }
            ScheduleMeshRender();
        }

        private void Mesh_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            meshDragging = false;
            e.Pointer.Capture(null);
        }

        private void Mesh_PointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            if (meshRenderer == null)
                return;
            meshRenderer.Zoom = Math.Clamp(meshRenderer.Zoom * (e.Delta.Y > 0 ? 1.1f : 1 / 1.1f), 0.05f, 50f);
            ScheduleMeshRender();
            e.Handled = true;
        }

        private void PreviewGameObject(AssetItem assetItem, GameObject m_GameObject)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"GameObject: {m_GameObject.m_Name}");
            sb.AppendLine($"Has model: {(m_GameObject.HasModel() ? "yes" : "no")}");
            sb.AppendLine("Components:");
            foreach (var pptr in m_GameObject.m_Components)
            {
                if (pptr.TryGet(out var component))
                {
                    sb.AppendLine($"  {component.type} {component.Name}");
                }
            }
            textPreviewBox.Text = sb.ToString();
            textPreviewBox.IsVisible = true;
        }

        #endregion

        #region Export

        private List<AssetItem> GetAssets(ExportFilter type) => type switch
        {
            ExportFilter.All => exportableAssets,
            ExportFilter.Selected => GetSelectedAssets(),
            _ => visibleAssets,
        };

        private async Task ExportAssets(ExportFilter type, ExportType exportType)
        {
            if (exportableAssets.Count == 0)
            {
                StatusStripUpdate("No exportable assets loaded");
                return;
            }
            var toExportAssets = GetAssets(type);
            if (toExportAssets.Count == 0)
            {
                StatusStripUpdate("Nothing to export");
                return;
            }
            var savePath = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (savePath == null)
                return;
            RememberSaveDirectory(savePath);
            await Studio.ExportAssets(savePath, toExportAssets, exportType, Settings.Default.openAfterExport);
        }

        private async void ExportAllConvert_Click(object sender, RoutedEventArgs e) => await ExportAssets(ExportFilter.All, ExportType.Convert);
        private async void ExportSelectedConvert_Click(object sender, RoutedEventArgs e) => await ExportAssets(ExportFilter.Selected, ExportType.Convert);
        private async void ExportFilteredConvert_Click(object sender, RoutedEventArgs e) => await ExportAssets(ExportFilter.Filtered, ExportType.Convert);
        private async void ExportRaw_Click(object sender, RoutedEventArgs e) => await ExportAssets(TagFilter(sender), ExportType.Raw);
        private async void ExportDump_Click(object sender, RoutedEventArgs e) => await ExportAssets(TagFilter(sender), ExportType.Dump);
        private async void ExportJson_Click(object sender, RoutedEventArgs e) => await ExportAssets(TagFilter(sender), ExportType.JSON);

        private static ExportFilter TagFilter(object sender) => Enum.Parse<ExportFilter>((string)((MenuItem)sender).Tag);

        private async void ExportList_Click(object sender, RoutedEventArgs e)
        {
            if (exportableAssets.Count == 0)
            {
                StatusStripUpdate("No exportable assets loaded");
                return;
            }
            var toExportAssets = GetAssets(TagFilter(sender));
            var savePath = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (savePath == null)
                return;
            RememberSaveDirectory(savePath);
            await Studio.ExportAssetsList(savePath, toExportAssets, ExportListType.XML);
        }

        private async void ExportSceneHierarchy_Click(object sender, RoutedEventArgs e)
        {
            if (sceneRoots.Count == 0)
            {
                StatusStripUpdate("No scene hierarchy loaded");
                return;
            }
            var path = await Dialogs.SaveFileAsync(this, "Save scene hierarchy", "scene.json", Settings.Default.lastSaveDirectory, new FilePickerFileType("Scene Hierarchy dump") { Patterns = new[] { "*.json" } });
            if (path == null)
                return;
            var nodes = new Dictionary<string, object>();
            foreach (var node in sceneRoots)
            {
                nodes.TryAdd(node.Text, GetNode(node));
            }
            File.WriteAllText(path, JsonConvert.SerializeObject(nodes, Formatting.Indented));
            Logger.Info("Scene Hierarchy dumped sucessfully !!");
        }

        private static object GetNode(SceneNode treeNode)
        {
            var nodes = new Dictionary<string, object>();
            foreach (var node in treeNode.Nodes)
            {
                if (HasGameObjectNode(node))
                {
                    nodes.TryAdd(node.Text, GetNode(node));
                }
            }
            return nodes.Count == 0 ? string.Empty : nodes;
        }

        private static bool HasGameObjectNode(SceneNode treeNode)
        {
            if (treeNode.gameObject != null && treeNode.gameObject.m_Transform?.m_Father.IsNull == false)
            {
                return treeNode.gameObject.m_Animator != null;
            }
            return treeNode.Nodes.Count > 0 && HasGameObjectNode(treeNode.Nodes[0]);
        }

        private async Task<bool> CheckFbx()
        {
            if (NativeLibraries.FbxAvailable)
                return true;
            await Dialogs.MessageAsync(this, "FBX export unavailable",
                $"Model export needs the native FBX exporter library:\n{Path.Combine(NativeLibraries.NativeDirectory, NativeLibraries.FbxLibraryFileName)}\n\n" +
                "Build AssetStudio.FBXNative for Linux against the Autodesk FBX SDK and copy it there (see LINUX.md).\n" +
                "Meshes can still be exported as OBJ via Export > Selected assets.");
            return false;
        }

        private List<AssetItem> SelectedAnimationClips(bool animation)
        {
            if (!animation)
                return null;
            var list = GetSelectedAssets().Where(x => x.Type == ClassIDType.AnimationClip).ToList();
            return list.Count == 0 ? null : list;
        }

        private async void ExportAnimatorWithClips_Click(object sender, RoutedEventArgs e)
        {
            var selectedAssets = GetSelectedAssets();
            var animator = selectedAssets.LastOrDefault(x => x.Type == ClassIDType.Animator);
            var animationList = selectedAssets.Where(x => x.Type == ClassIDType.AnimationClip).ToList();
            if (animator == null)
            {
                StatusStripUpdate("Select an Animator (and AnimationClips) in the asset list first.");
                return;
            }
            if (!await CheckFbx())
                return;
            var folder = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (folder == null)
                return;
            RememberSaveDirectory(folder);
            var exportPath = Path.Combine(folder, "Animator") + Path.DirectorySeparatorChar;
            await ExportAnimatorWithAnimationClip(animator, animationList, exportPath);
        }

        private async void ExportAllObjectsSplit_Click(object sender, RoutedEventArgs e)
        {
            if (sceneRoots.Count == 0)
            {
                StatusStripUpdate("No Objects available for export");
                return;
            }
            if (!await CheckFbx())
                return;
            var folder = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (folder == null)
                return;
            RememberSaveDirectory(folder);
            await ExportSplitObjects(folder + Path.DirectorySeparatorChar, sceneRoots);
        }

        private async void ExportSelectedObjects_Click(object sender, RoutedEventArgs e) => await ExportObjects(false);
        private async void ExportSelectedObjectsWithClips_Click(object sender, RoutedEventArgs e) => await ExportObjects(true);

        private async Task ExportObjects(bool animation)
        {
            if (sceneRoots.Count == 0)
            {
                StatusStripUpdate("No Objects available for export");
                return;
            }
            if (!await CheckFbx())
                return;
            var folder = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (folder == null)
                return;
            RememberSaveDirectory(folder);
            var exportPath = Path.Combine(folder, "GameObject") + Path.DirectorySeparatorChar;
            await ExportObjectsWithAnimationClip(exportPath, sceneRoots, SelectedAnimationClips(animation));
        }

        private async void ExportMergeObjects_Click(object sender, RoutedEventArgs e) => await ExportMergeObjects(false);
        private async void ExportMergeObjectsWithClips_Click(object sender, RoutedEventArgs e) => await ExportMergeObjects(true);

        private async Task ExportMergeObjects(bool animation)
        {
            if (sceneRoots.Count == 0)
                return;
            var gameObjects = new List<GameObject>();
            GetSelectedParentNode(sceneRoots, gameObjects);
            if (gameObjects.Count == 0)
            {
                StatusStripUpdate("No Object selected for export. Tick objects in the Scene Hierarchy first.");
                return;
            }
            if (!await CheckFbx())
                return;
            var path = await Dialogs.SaveFileAsync(this, "Export merged FBX", gameObjects[0].m_Name + " (merge).fbx", Settings.Default.lastSaveDirectory, new FilePickerFileType("Fbx file") { Patterns = new[] { "*.fbx" } });
            if (path == null)
                return;
            RememberSaveDirectory(Path.GetDirectoryName(path));
            await ExportObjectsMergeWithAnimationClip(path, gameObjects, SelectedAnimationClips(animation));
        }

        private async void ExportNodes_Click(object sender, RoutedEventArgs e) => await ExportNodes(false);
        private async void ExportNodesWithClips_Click(object sender, RoutedEventArgs e) => await ExportNodes(true);

        private async Task ExportNodes(bool animation)
        {
            if (sceneRoots.Count == 0)
                return;
            var roots = sceneRoots.Where(x => x.IsChecked).ToList();
            if (roots.Count == 0)
            {
                Logger.Info("No root nodes found selected.");
                return;
            }
            if (!await CheckFbx())
                return;
            var folder = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (folder == null)
                return;
            RememberSaveDirectory(folder);
            var exportPath = Path.Combine(folder, "GameObject") + Path.DirectorySeparatorChar;
            await ExportNodesWithAnimationClip(exportPath, roots, SelectedAnimationClips(animation));
        }

        #endregion
    }
}
