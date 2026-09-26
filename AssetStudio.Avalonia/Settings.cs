using Newtonsoft.Json;
using System;
using System.IO;

namespace AssetStudio.Avalonia
{
    /// <summary>
    /// Cross-platform replacement for the WinForms Properties.Settings.
    /// Stored as JSON under $XDG_CONFIG_HOME/AssetStudio (or ~/.config/AssetStudio).
    /// </summary>
    public class Settings
    {
        private static readonly object saveLock = new object();

        public static string ConfigDirectory
        {
            get
            {
                var baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(baseDir))
                {
                    baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }
                return Path.Combine(baseDir, "AssetStudio");
            }
        }

        public static string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

        public static Settings Default { get; private set; } = Load();

        public bool displayAll = false;
        public bool enablePreview = true;
        public bool displayInfo = true;
        public bool openAfterExport = true;
        public int assetGroupOption = 0;
        public bool convertTexture = true;
        public bool convertAudio = true;
        public ImageFormat convertType = ImageFormat.Png;
        public bool eulerFilter = true;
        public decimal filterPrecision = 0.25m;
        public bool exportAllNodes = true;
        public bool exportSkins = true;
        public bool exportAnimations = true;
        public decimal boneSize = 10;
        public int fbxVersion = 3;
        public int fbxFormat = 0;
        public int modelFormat = 0; //ModelFormat: 0 FBX, 1 glTF, 2 GLB
        public decimal scaleFactor = 1;
        public bool exportBlendShape = true;
        public bool castToBone = false;
        public bool restoreExtensionName = true;
        public byte key = 147;
        public bool enableConsole = true;
        public bool encrypted = true;
        public int selectedGame = 0;
        public bool enableResolveDependencies = true;
        public int assetMapType = 1;
        public bool collectAnimations = true;
        public bool skipContainer = false;
        public bool minimalAssetMap = true;
        public bool modelsOnly = false;
        public bool enableModelPreview = false;
        public int selectedUnityCNKey = 0;
        public string selectedCABMapName = "";
        public bool enableFileLogging = false;
        public int theme = 0; //0 = follow the system, 1 = light, 2 = dark
        public bool showSkeleton = true;
        public bool gameShaders = true; //preview with the games' shaders where possible
        public string uvs = "{\"UV0\":{\"Item1\":true,\"Item2\":0},\"UV1\":{\"Item1\":true,\"Item2\":1},\"UV2\":{\"Item1\":false,\"Item2\":0},\"UV3\":{\"Item1\":false,\"Item2\":0},\"UV4\":{\"Item1\":false,\"Item2\":0},\"UV5\":{\"Item1\":false,\"Item2\":0},\"UV6\":{\"Item1\":false,\"Item2\":0},\"UV7\":{\"Item1\":false,\"Item2\":0}}";
        public bool allowDuplicates = false;
        public int loggerEventType = 30;
        public string types = "{\"Animation\":{\"Item1\":true,\"Item2\":false},\"AnimationClip\":{\"Item1\":true,\"Item2\":true},\"Animator\":{\"Item1\":true,\"Item2\":true},\"AnimatorController\":{\"Item1\":true,\"Item2\":false},\"AnimatorOverrideController\":{\"Item1\":true,\"Item2\":false},\"AssetBundle\":{\"Item1\":true,\"Item2\":false},\"AudioClip\":{\"Item1\":true,\"Item2\":true},\"Avatar\":{\"Item1\":true,\"Item2\":false},\"Font\":{\"Item1\":true,\"Item2\":true},\"GameObject\":{\"Item1\":true,\"Item2\":false},\"IndexObject\":{\"Item1\":true,\"Item2\":false},\"Material\":{\"Item1\":true,\"Item2\":true},\"Mesh\":{\"Item1\":true,\"Item2\":true},\"MeshFilter\":{\"Item1\":true,\"Item2\":false},\"MeshRenderer\":{\"Item1\":true,\"Item2\":false},\"MiHoYoBinData\":{\"Item1\":true,\"Item2\":true},\"MonoBehaviour\":{\"Item1\":true,\"Item2\":true},\"MonoScript\":{\"Item1\":true,\"Item2\":false},\"MovieTexture\":{\"Item1\":true,\"Item2\":true},\"PlayerSettings\":{\"Item1\":true,\"Item2\":false},\"RectTransform\":{\"Item1\":true,\"Item2\":false},\"Shader\":{\"Item1\":true,\"Item2\":true},\"SkinnedMeshRenderer\":{\"Item1\":true,\"Item2\":false},\"Sprite\":{\"Item1\":true,\"Item2\":true},\"SpriteAtlas\":{\"Item1\":true,\"Item2\":false},\"TextAsset\":{\"Item1\":true,\"Item2\":true},\"Texture2D\":{\"Item1\":true,\"Item2\":true},\"Transform\":{\"Item1\":true,\"Item2\":false},\"VideoClip\":{\"Item1\":true,\"Item2\":true},\"ResourceManager\":{\"Item1\":true,\"Item2\":false},\"Cubemap\":{\"Item1\":true,\"Item2\":true},\"Texture2DArray\":{\"Item1\":true,\"Item2\":true},\"Texture3D\":{\"Item1\":true,\"Item2\":true},\"CubemapArray\":{\"Item1\":true,\"Item2\":true},\"TerrainData\":{\"Item1\":true,\"Item2\":true}}";
        public string texs = "{}";
        public bool exportMaterials = false;
        public string lastOpenDirectory = "";
        public string lastSaveDirectory = "";
        public string typeTreeDumpsDirectory = ""; //empty: TypeTreeDumps in the app data folder

        private static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(SettingsPath));
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Failed to load settings, using defaults: {e.Message}");
            }
            return new Settings();
        }

        public void Save()
        {
            lock (saveLock)
            {
                try
                {
                    Directory.CreateDirectory(ConfigDirectory);
                    var tmp = SettingsPath + ".tmp";
                    File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
                    File.Move(tmp, SettingsPath, true);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Failed to save settings: {e.Message}");
                }
            }
        }

        public static void Reset()
        {
            Default = new Settings();
            Default.Save();
        }
    }
}
