using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AssetStudio.Avalonia.Views
{
    /// <summary>
    /// Edit the UnityCN key list (Keys.json) and select the active key.
    /// </summary>
    public class UnityCNWindow : Window
    {
        private readonly ListBox list;
        private readonly TextBox nameBox;
        private readonly TextBox keyBox;
        private readonly ObservableCollection<UnityCN.Entry> entries;

        public UnityCNWindow()
        {
            Title = "UnityCN keys";
            Width = 760;
            Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            entries = new ObservableCollection<UnityCN.Entry>(UnityCNManager.GetEntries());
            list = new ListBox { ItemsSource = entries };
            list.ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<UnityCN.Entry>((entry, _) =>
                new TextBlock { Text = entry == null ? "" : $"{entry.Name}   {entry.Key}" });
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is UnityCN.Entry entry)
                {
                    nameBox.Text = entry.Name;
                    keyBox.Text = entry.Key;
                }
            };

            nameBox = new TextBox { Watermark = "Name", Width = 180 };
            keyBox = new TextBox { Watermark = "Key (32 hex chars)", Width = 280 };
            var add = new Button { Content = "Add / Update" };
            add.Click += (_, _) => AddOrUpdate();
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                if (list.SelectedItem is UnityCN.Entry entry)
                    entries.Remove(entry);
            };
            var select = new Button { Content = "Use selected key", IsDefault = true };
            select.Click += (_, _) => SaveAndSelect();
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            cancel.Click += (_, _) => Close();

            var editRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { nameBox, keyBox, add, remove } };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { select, cancel } };
            var hint = new TextBlock { Text = "Select a key and press \"Use selected key\". The list is saved to Keys.json.", Foreground = Brushes.Gray };

            var root = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            DockPanel.SetDock(editRow, Dock.Bottom);
            DockPanel.SetDock(hint, Dock.Top);
            buttons.Margin = new Thickness(0, 8, 0, 0);
            editRow.Margin = new Thickness(0, 8, 0, 0);
            hint.Margin = new Thickness(0, 0, 0, 8);
            root.Children.Add(hint);
            root.Children.Add(buttons);
            root.Children.Add(editRow);
            root.Children.Add(list);
            Content = root;

            var index = Settings.Default.selectedUnityCNKey;
            if (index >= 0 && index < entries.Count)
            {
                list.SelectedIndex = index;
            }
        }

        private void AddOrUpdate()
        {
            var name = nameBox.Text?.Trim();
            var key = keyBox.Text?.Trim().Replace(" ", "");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key))
                return;
            var entry = new UnityCN.Entry(name, key);
            bool valid;
            try
            {
                valid = entry.Validate();
            }
            catch (FormatException)
            {
                valid = false;
            }
            if (!valid)
            {
                _ = Dialogs.MessageAsync(this, "Invalid key", "The key must be 16 bytes written as 32 hex characters.");
                return;
            }
            var existing = entries.FirstOrDefault(x => x.Name == name);
            if (existing != null)
            {
                var i = entries.IndexOf(existing);
                entries[i] = entry;
                list.SelectedIndex = i;
            }
            else
            {
                entries.Add(entry);
                list.SelectedIndex = entries.Count - 1;
            }
        }

        private void SaveAndSelect()
        {
            try
            {
                UnityCNManager.SaveEntries(new List<UnityCN.Entry>(entries));
            }
            catch (Exception e)
            {
                Logger.Error($"Unable to save {UnityCNManager.KeysFileName}: {e.Message}");
            }
            var index = Math.Max(0, list.SelectedIndex);
            if (Studio.Game.Type.IsUnityCN() && entries.Count > 0)
            {
                UnityCNManager.SetKey(index);
            }
            Settings.Default.selectedUnityCNKey = index;
            Settings.Default.Save();
            Close();
        }
    }
}
