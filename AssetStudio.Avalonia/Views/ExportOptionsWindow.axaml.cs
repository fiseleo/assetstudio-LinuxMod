using Avalonia.Controls;
using Avalonia.Interactivity;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AssetStudio.Avalonia.Views
{
    public partial class ExportOptionsWindow : Window
    {
        private static readonly string[] TextureTypes = { "Diffuse", "NormalMap", "Specular", "Bump", "Ambient", "Emissive", "Reflection", "Displacement" };

        public bool Resetted { get; private set; }

        private readonly Dictionary<ClassIDType, (bool, bool)> types;
        private readonly Dictionary<string, (bool, int)> uvs;
        private readonly Dictionary<string, int> texs;
        private bool updating;

        public ExportOptionsWindow()
        {
            InitializeComponent();
            var s = Settings.Default;
            assetGroupOptions.SelectedIndex = s.assetGroupOption;
            restoreExtensionName.IsChecked = s.restoreExtensionName;
            convertTexture.IsChecked = s.convertTexture;
            convertAudio.IsChecked = s.convertAudio;
            (s.convertType switch
            {
                ImageFormat.Bmp => formatBmp,
                ImageFormat.Jpeg => formatJpeg,
                ImageFormat.Tga => formatTga,
                _ => formatPng,
            }).IsChecked = true;
            openAfterExport.IsChecked = s.openAfterExport;
            eulerFilter.IsChecked = s.eulerFilter;
            filterPrecision.Value = s.filterPrecision;
            exportAllNodes.IsChecked = s.exportAllNodes;
            exportSkins.IsChecked = s.exportSkins;
            exportMaterials.IsChecked = s.exportMaterials;
            exportAnimations.IsChecked = s.exportAnimations;
            exportBlendShape.IsChecked = s.exportBlendShape;
            castToBone.IsChecked = s.castToBone;
            boneSize.Value = s.boneSize;
            scaleFactor.Value = s.scaleFactor;
            fbxVersion.SelectedIndex = s.fbxVersion;
            fbxFormat.SelectedIndex = s.fbxFormat;
            collectAnimations.IsChecked = s.collectAnimations;
            encrypted.IsChecked = s.encrypted;
            keyTextBox.Text = s.key.ToString("X2");
            minimalAssetMap.IsChecked = s.minimalAssetMap;

            types = TypeFlags.WithAddedTypes(JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(s.types));
            uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(s.uvs) ?? new Dictionary<string, (bool, int)>();
            texs = (string.IsNullOrEmpty(s.texs) ? null : JsonConvert.DeserializeObject<Dictionary<string, int>>(s.texs)) ?? new Dictionary<string, int>();

            typesComboBox.ItemsSource = types.Keys.OrderBy(x => x.ToString()).ToList();
            typesComboBox.SelectedIndex = 0;
            uvsComboBox.ItemsSource = uvs.Keys.ToList();
            uvTypesComboBox.ItemsSource = TextureTypes;
            uvsComboBox.SelectedIndex = 0;
            texTypeComboBox.ItemsSource = TextureTypes;
            texTypeComboBox.SelectedIndex = 0;
            texNameBox.ItemsSource = texs.Keys.ToList();
            if (texs.Count > 0)
            {
                var first = texs.First();
                texNameBox.Text = first.Key;
                texTypeComboBox.SelectedIndex = first.Value;
            }

            ToolTip.SetTip(typesComboBox, string.Join("\n", types.Select(t => $"{t.Key}: {(t.Value.Item1 ? '✓' : '✗')}, {(t.Value.Item2 ? '✓' : '✗')}")));
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (!byte.TryParse(keyTextBox.Text?.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var key))
            {
                _ = Dialogs.MessageAsync(this, "Invalid key", "The MiHoYoBinData key must be a hex byte (00 - FF).");
                return;
            }
            var s = Settings.Default;
            s.assetGroupOption = assetGroupOptions.SelectedIndex;
            s.restoreExtensionName = restoreExtensionName.IsChecked == true;
            s.convertTexture = convertTexture.IsChecked == true;
            s.convertAudio = convertAudio.IsChecked == true;
            s.convertType = formatBmp.IsChecked == true ? ImageFormat.Bmp
                : formatJpeg.IsChecked == true ? ImageFormat.Jpeg
                : formatTga.IsChecked == true ? ImageFormat.Tga
                : ImageFormat.Png;
            s.openAfterExport = openAfterExport.IsChecked == true;
            s.eulerFilter = eulerFilter.IsChecked == true;
            s.filterPrecision = filterPrecision.Value ?? 0.25m;
            s.exportAllNodes = exportAllNodes.IsChecked == true;
            s.exportSkins = exportSkins.IsChecked == true;
            s.exportMaterials = exportMaterials.IsChecked == true;
            s.exportAnimations = exportAnimations.IsChecked == true;
            s.exportBlendShape = exportBlendShape.IsChecked == true;
            s.castToBone = castToBone.IsChecked == true;
            s.boneSize = boneSize.Value ?? 10;
            s.scaleFactor = scaleFactor.Value ?? 1;
            s.fbxVersion = fbxVersion.SelectedIndex;
            s.fbxFormat = fbxFormat.SelectedIndex;
            s.collectAnimations = collectAnimations.IsChecked == true;
            s.encrypted = encrypted.IsChecked == true;
            s.key = key;
            s.minimalAssetMap = minimalAssetMap.IsChecked == true;
            s.types = JsonConvert.SerializeObject(types);
            s.uvs = JsonConvert.SerializeObject(uvs);
            s.texs = JsonConvert.SerializeObject(texs);
            s.Save();

            MiHoYoBinData.Key = key;
            MiHoYoBinData.Encrypted = s.encrypted;
            AssetsHelper.Minimal = s.minimalAssetMap;
            TypeFlags.SetTypes(types);
            Close();
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (!await Dialogs.ConfirmAsync(this, "Reset", "Reset all settings to their defaults?"))
                return;
            Settings.Reset();
            Resetted = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Types_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (typesComboBox.SelectedItem is ClassIDType type && types.TryGetValue(type, out var param))
            {
                updating = true;
                canParseCheckBox.IsChecked = param.Item1;
                canExportCheckBox.IsChecked = param.Item2;
                updating = false;
            }
        }

        private void CanParse_Changed(object sender, RoutedEventArgs e)
        {
            if (!updating && typesComboBox.SelectedItem is ClassIDType type && types.TryGetValue(type, out var param))
            {
                types[type] = (canParseCheckBox.IsChecked == true, param.Item2);
            }
        }

        private void CanExport_Changed(object sender, RoutedEventArgs e)
        {
            if (!updating && typesComboBox.SelectedItem is ClassIDType type && types.TryGetValue(type, out var param))
            {
                types[type] = (param.Item1, canExportCheckBox.IsChecked == true);
            }
        }

        private void Uvs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (uvsComboBox.SelectedItem is string uv && uvs.TryGetValue(uv, out var param))
            {
                updating = true;
                uvEnabledCheckBox.IsChecked = param.Item1;
                uvTypesComboBox.SelectedIndex = param.Item2;
                updating = false;
            }
        }

        private void UvEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (!updating && uvsComboBox.SelectedItem is string uv && uvs.TryGetValue(uv, out var param))
            {
                uvs[uv] = (uvEnabledCheckBox.IsChecked == true, param.Item2);
            }
        }

        private void UvTypes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!updating && uvsComboBox.SelectedItem is string uv && uvs.TryGetValue(uv, out var param) && uvTypesComboBox.SelectedIndex >= 0)
            {
                uvs[uv] = (param.Item1, uvTypesComboBox.SelectedIndex);
            }
        }

        private void TexName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (texNameBox.SelectedItem is string name && texs.TryGetValue(name, out var type))
            {
                updating = true;
                texTypeComboBox.SelectedIndex = type;
                updating = false;
            }
        }

        private void TexType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var name = texNameBox?.Text;
            if (!updating && !string.IsNullOrEmpty(name) && texs.ContainsKey(name) && texTypeComboBox.SelectedIndex >= 0)
            {
                texs[name] = texTypeComboBox.SelectedIndex;
            }
        }

        private void AddTex_Click(object sender, RoutedEventArgs e)
        {
            var name = texNameBox.Text?.Trim();
            if (!string.IsNullOrEmpty(name) && !texs.ContainsKey(name))
            {
                texs[name] = Math.Max(0, texTypeComboBox.SelectedIndex);
                texNameBox.ItemsSource = texs.Keys.ToList();
            }
        }

        private void RemoveTex_Click(object sender, RoutedEventArgs e)
        {
            var name = texNameBox.Text?.Trim();
            if (!string.IsNullOrEmpty(name) && texs.Remove(name))
            {
                texNameBox.ItemsSource = texs.Keys.ToList();
                texNameBox.Text = texs.Keys.FirstOrDefault() ?? "";
                texTypeComboBox.SelectedIndex = texs.Values.FirstOrDefault();
            }
        }
    }
}
