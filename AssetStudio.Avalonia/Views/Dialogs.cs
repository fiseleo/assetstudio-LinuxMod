using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia.Views
{
    /// <summary>
    /// Small code-only dialogs replacing WinForms MessageBox / OpenFolderDialog / ad-hoc input forms.
    /// </summary>
    public static class Dialogs
    {
        public static async Task MessageAsync(Window owner, string title, string message)
        {
            await ShowButtonsAsync(owner, title, message, "OK");
        }

        public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
        {
            return await ShowButtonsAsync(owner, title, message, "Yes", "No") == "Yes";
        }

        private static async Task<string> ShowButtonsAsync(Window owner, string title, string message, params string[] buttons)
        {
            string result = null;
            var window = CreateWindow(title);
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var text in buttons)
            {
                var button = new Button { Content = text, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
                button.Click += (_, _) => { result = text; window.Close(); };
                buttonPanel.Children.Add(button);
            }
            var messageBox = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            var scroll = new ScrollViewer { Content = messageBox, MaxHeight = 400 };
            window.Content = new DockPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    Dock(buttonPanel, global::Avalonia.Controls.Dock.Bottom),
                    scroll,
                }
            };
            buttonPanel.Margin = new Thickness(0, 12, 0, 0);
            await window.ShowDialog(owner);
            return result;
        }

        /// <summary>
        /// Text input dialog. When suggestions are given, an auto-complete box is used.
        /// Returns null when cancelled.
        /// </summary>
        public static async Task<string> InputAsync(Window owner, string title, string message, string defaultValue = "", string hint = null, IEnumerable<string> suggestions = null)
        {
            string result = null;
            var window = CreateWindow(title);
            Control input;
            System.Func<string> getText;
            if (suggestions != null)
            {
                var box = new AutoCompleteBox { Text = defaultValue, ItemsSource = suggestions.ToList(), FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 0 };
                input = box;
                getText = () => box.Text;
            }
            else
            {
                var box = new TextBox { Text = defaultValue };
                input = box;
                getText = () => box.Text;
            }

            var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Click += (_, _) => { result = getText() ?? ""; window.Close(); };
            cancel.Click += (_, _) => window.Close();

            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            if (!string.IsNullOrEmpty(hint))
            {
                panel.Children.Add(new TextBlock { Text = hint, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
            }
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
                Children = { ok, cancel }
            });
            window.Content = panel;
            window.Opened += (_, _) => input.Focus();
            await window.ShowDialog(owner);
            return result;
        }

        private static Window CreateWindow(string title) => new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        private static Control Dock(Control control, global::Avalonia.Controls.Dock dock)
        {
            DockPanel.SetDock(control, dock);
            return control;
        }

        public static async Task<string> PickFolderAsync(Window owner, string title, string initialFolder = null)
        {
            if (!owner.StorageProvider.CanPickFolder)
            {
                var typed = await InputAsync(owner, title, "No folder dialog is available on this system. Enter a folder path:", initialFolder ?? "");
                return string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
            }
            var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
            if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
            {
                options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialFolder);
            }
            var result = await owner.StorageProvider.OpenFolderPickerAsync(options);
            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        }

        public static async Task<string[]> PickFilesAsync(Window owner, string title, bool allowMultiple, string initialFolder = null, params FilePickerFileType[] filters)
        {
            if (!owner.StorageProvider.CanOpen)
            {
                var typed = await InputAsync(owner, title, "No file dialog is available on this system. Enter file path(s), separated by ';':", initialFolder ?? "");
                return string.IsNullOrWhiteSpace(typed) ? System.Array.Empty<string>() : typed.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
            }
            var options = new FilePickerOpenOptions { Title = title, AllowMultiple = allowMultiple };
            if (filters.Length > 0)
            {
                options.FileTypeFilter = filters;
            }
            if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
            {
                options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialFolder);
            }
            var result = await owner.StorageProvider.OpenFilePickerAsync(options);
            return result.Select(x => x.TryGetLocalPath()).Where(x => x != null).ToArray();
        }

        public static async Task<string> SaveFileAsync(Window owner, string title, string suggestedName, string initialFolder, FilePickerFileType filter)
        {
            if (!owner.StorageProvider.CanSave)
            {
                var typed = await InputAsync(owner, title, "No save dialog is available on this system. Enter the file path:", Path.Combine(initialFolder ?? "", suggestedName));
                return string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
            }
            var options = new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, FileTypeChoices = new[] { filter }, ShowOverwritePrompt = true };
            if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
            {
                options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialFolder);
            }
            var result = await owner.StorageProvider.SaveFilePickerAsync(options);
            return result?.TryGetLocalPath();
        }

        public static bool IsFileDrop(DragEventArgs e) => e.Data.Contains(DataFormats.Files);
    }
}
