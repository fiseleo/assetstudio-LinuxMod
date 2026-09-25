using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static AssetStudio.Avalonia.Studio;

namespace AssetStudio.Avalonia.Views
{
    /// <summary>
    /// Browse an AssetMap (.map, built with Misc. > Build AssetMap) without loading the game files,
    /// then load or export the files / assets of the selected entries.
    /// </summary>
    public class AssetBrowserWindow : Window
    {
        private static readonly string[] Columns = { nameof(AssetEntry.Name), nameof(AssetEntry.Container), nameof(AssetEntry.Source), nameof(AssetEntry.PathID), nameof(AssetEntry.Type) };

        private readonly MainWindow parent;
        private readonly DataGrid grid;
        private readonly Dictionary<string, TextBox> filterBoxes = new Dictionary<string, TextBox>();
        private readonly TextBlock countLabel;
        private readonly Button loadMapButton;
        private readonly Button loadSelectedButton;
        private readonly Button exportSelectedButton;

        public AssetBrowserWindow(MainWindow parent)
        {
            this.parent = parent;
            Title = "Asset Browser";
            Width = 1000;
            Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            loadMapButton = new Button { Content = "Load AssetMap..." };
            loadMapButton.Click += LoadAssetMap_Click;
            var clearButton = new Button { Content = "Clear" };
            clearButton.Click += (_, _) =>
            {
                Clear();
                Logger.Info("Cleared !!");
            };
            countLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
            var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { loadMapButton, clearButton, countLabel } };

            // one regex filter per column, applied with Enter (all filters must match)
            var filterRow = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Columns.Select(_ => "*"))), Margin = new Thickness(0, 8) };
            for (int i = 0; i < Columns.Length; i++)
            {
                var box = new TextBox { Watermark = $"{Columns[i]} (regex, Enter)", Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        ApplyFilters();
                        e.Handled = true;
                    }
                };
                Grid.SetColumn(box, i);
                filterRow.Children.Add(box);
                filterBoxes[Columns[i]] = box;
            }

            grid = new DataGrid
            {
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                CanUserSortColumns = true,
                CanUserResizeColumns = true,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
            };
            foreach (var column in Columns)
            {
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = column,
                    Binding = new Binding(column),
                    SortMemberPath = column,
                    Width = column is nameof(AssetEntry.Name) or nameof(AssetEntry.Container) or nameof(AssetEntry.Source) ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
                    MinWidth = column == nameof(AssetEntry.Type) ? 120 : 80,
                });
            }
            grid.SelectionChanged += (_, _) => UpdateButtons();
            grid.DoubleTapped += (_, _) => LoadSelected();

            loadSelectedButton = new Button { Content = "Load selected files" };
            loadSelectedButton.Click += (_, _) => LoadSelected();
            exportSelectedButton = new Button { Content = "Export selected assets..." };
            exportSelectedButton.Click += ExportSelected_Click;
            var hint = new TextBlock { Text = "Double-click loads the file of the entry.", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { loadSelectedButton, exportSelectedButton, hint } };

            var root = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(topRow, Dock.Top);
            DockPanel.SetDock(filterRow, Dock.Top);
            DockPanel.SetDock(bottomRow, Dock.Bottom);
            root.Children.Add(topRow);
            root.Children.Add(filterRow);
            root.Children.Add(bottomRow);
            root.Children.Add(grid);
            Content = root;

            ShowEntries(ResourceMap.GetEntries());
        }

        private List<AssetEntry> SelectedEntries => grid.SelectedItems.OfType<AssetEntry>().ToList();

        private void UpdateButtons()
        {
            var any = grid.SelectedItems.Count > 0;
            loadSelectedButton.IsEnabled = any;
            exportSelectedButton.IsEnabled = any;
        }

        private void ShowEntries(List<AssetEntry> entries)
        {
            grid.ItemsSource = entries;
            var total = ResourceMap.GetEntries().Count;
            countLabel.Text = total == 0 ? "No AssetMap loaded" : entries.Count == total ? $"{total} entries" : $"{entries.Count} of {total} entries";
            UpdateButtons();
        }

        private async void LoadAssetMap_Click(object sender, RoutedEventArgs e)
        {
            var files = await Dialogs.PickFilesAsync(this, "Load AssetMap", false, Settings.Default.lastSaveDirectory, new FilePickerFileType("MessagePack AssetMap File") { Patterns = new[] { "*.map" } });
            if (files.Length == 0)
                return;
            loadMapButton.IsEnabled = false;
            Logger.Info("Loading AssetMap...");
            await Task.Run(() => ResourceMap.FromFile(files[0]));
            foreach (var box in filterBoxes.Values)
            {
                box.Text = string.Empty;
            }
            ShowEntries(ResourceMap.GetEntries());
            loadMapButton.IsEnabled = true;
        }

        private void ApplyFilters()
        {
            var filters = new Dictionary<string, Regex>();
            foreach (var (name, box) in filterBoxes)
            {
                if (string.IsNullOrEmpty(box.Text))
                    continue;
                try
                {
                    filters[name] = new Regex(box.Text, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException)
                {
                    Logger.Error($"Invalid regex {box.Text}");
                    return;
                }
            }
            ShowEntries(filters.Count == 0 ? ResourceMap.GetEntries() : ResourceMap.GetEntries().FindAll(x => x.Matches(filters)));
        }

        public string[] ExistingSourceFiles(IEnumerable<AssetEntry> entries)
        {
            var files = new List<string>();
            foreach (var file in entries.Select(x => x.Source).Where(x => !string.IsNullOrEmpty(x)).Distinct())
            {
                if (File.Exists(file))
                    files.Add(file);
                else
                    Logger.Warning($"Unable to find file {file}, skipping...");
            }
            return files.ToArray();
        }

        private void LoadSelected()
        {
            var files = ExistingSourceFiles(SelectedEntries);
            if (files.Length != 0)
            {
                Logger.Info("Loading...");
                parent.LoadPaths(files);
            }
        }

        private async void ExportSelected_Click(object sender, RoutedEventArgs e)
        {
            var entries = SelectedEntries;
            var files = ExistingSourceFiles(entries);
            if (files.Length == 0)
                return;
            var savePath = await Dialogs.PickFolderAsync(this, "Select the save folder", Settings.Default.lastSaveDirectory);
            if (savePath == null)
                return;
            await ExportEntriesAsync(entries, files, savePath);
        }

        /// <summary>
        /// Loads the source files one at a time (so a whole game never has to be in memory)
        /// and exports the assets of <paramref name="entries"/> found in them.
        /// </summary>
        public async Task ExportEntriesAsync(List<AssetEntry> entries, string[] files, string savePath)
        {
            parent.ResetForm();
            assetsManager.Game = Studio.Game;
            exportSelectedButton.IsEnabled = false;
            loadSelectedButton.IsEnabled = false;
            var statusStripUpdate = StatusStripUpdate;
            StatusStripUpdate = Logger.Info;
            try
            {
                for (int i = 0; i < files.Length; i++)
                {
                    var file = files[i];
                    var wanted = entries.Where(x => x.Source == file).Select(x => (x.PathID, x.Type)).ToHashSet();
                    var toExport = await Task.Run(() =>
                    {
                        assetsManager.LoadFiles(file);
                        if (assetsManager.assetsFileList.Count == 0)
                            return new List<AssetItem>();
                        BuildAssetData();
                        // match by file + PathID + type: names can differ between the map and the loaded assets
                        var fullPath = Path.GetFullPath(file);
                        return exportableAssets.Where(x => wanted.Contains((x.m_PathID, x.Type))
                            && Path.GetFullPath(x.SourceFile.originalPath ?? x.SourceFile.fullName) == fullPath).ToList();
                    });
                    if (toExport.Count > 0)
                    {
                        await ExportAssets(savePath, toExport, ExportType.Convert, i == files.Length - 1);
                    }
                    else
                    {
                        Logger.Warning($"No matching assets found in {file}");
                    }
                    exportableAssets.Clear();
                    visibleAssets = exportableAssets;
                    assetsManager.Clear();
                }
            }
            finally
            {
                StatusStripUpdate = statusStripUpdate;
                UpdateButtons();
            }
        }

        public void Clear()
        {
            ResourceMap.Clear();
            ShowEntries(ResourceMap.GetEntries());
        }

        protected override void OnClosed(EventArgs e)
        {
            Clear();
            base.OnClosed(e);
        }
    }
}
