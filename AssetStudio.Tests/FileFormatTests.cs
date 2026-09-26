using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AssetStudio.Tests
{
    public class AddressablesCatalogTests
    {
        private const string Prefix = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64";

        public static IEnumerable<object[]> Catalogs => new[]
        {
            new object[] { "synthetic_catalog.json", "JSON" },
            new object[] { "catalog_v1.bin", "binary v1" },
            new object[] { "catalog_v2.bin", "binary v2" },
            new object[] { "catalog_v3.bin", "binary v3" },
        };

        [Theory]
        [MemberData(nameof(Catalogs))]
        public void ReadsLocationsKeysAndBundles(string file, string format)
        {
            var catalog = AddressablesCatalog.TryLoad(TestUtil.Fixture(file));
            Assert.NotNull(catalog);
            Assert.Equal(format, catalog.Format);
            Assert.Equal("AddressablesMainContentCatalog", catalog.LocatorId);
            Assert.Equal(5, catalog.Locations.Count);

            var bundles = catalog.Locations.Where(x => x.IsBundle).ToList();
            Assert.Equal(2, bundles.Count);
            var characters = catalog.FindBundle("characters_assets_all_0123456789abcdef0123456789abcdef.bundle");
            Assert.NotNull(characters);
            Assert.Equal(Prefix + "/characters_assets_all_0123456789abcdef0123456789abcdef.bundle", characters.InternalId);
            Assert.Equal("characters_assets_all", characters.BundleName);
            Assert.Equal(1234567u, characters.BundleCrc);
            Assert.Equal(56789, characters.BundleSize);

            var hero = catalog.FindAsset("assets/characters/hero.prefab", "characters_assets_all_0123456789abcdef0123456789abcdef.bundle");
            Assert.NotNull(hero);
            Assert.Equal("Hero", hero.PrimaryKey);
            Assert.Equal(new[] { "Characters", "Playable" }, hero.Labels.OrderBy(x => x).ToArray());
            Assert.Equal("UnityEngine.GameObject", hero.ResourceType);
            Assert.Equal(new[] { "characters_assets_all_0123456789abcdef0123456789abcdef.bundle", "shared_assets_all_fedcba9876543210fedcba9876543210.bundle" },
                hero.Dependencies.Select(x => x.FileName).OrderBy(x => x).ToArray());

            var level = catalog.FindAsset("Assets/Scenes/Level1.unity", null);
            Assert.True(level.IsScene);
            Assert.Equal("shared_assets_all_fedcba9876543210fedcba9876543210.bundle", level.Dependencies.Single().FileName);
            Assert.Single(catalog.AssetsInBundle("shared_assets_all_fedcba9876543210fedcba9876543210.bundle"));
        }

        [Fact]
        public void ExportObject_ListsBundlesAndAssets()
        {
            var catalog = AddressablesCatalog.TryLoad(TestUtil.Fixture("catalog_v3.bin"));
            var json = JObject.FromObject(catalog.ToExportObject());
            Assert.Equal(2, ((JArray)json["Bundles"]).Count);
            Assert.Contains(((JArray)json["Assets"]).Select(x => (string)x["Address"]), x => x == "HeroAlbedo");
        }

        [Fact]
        public void OtherFiles_AreNotCatalogs()
        {
            Assert.Null(AddressablesCatalog.TryRead(Encoding.UTF8.GetBytes("{\"m_Name\": 1}"), "settings.json"));
            Assert.Null(AddressablesCatalog.TryRead(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "catalog.bin"));
            Assert.True(AddressablesCatalog.IsCatalogFileName("aa/Windows/catalog_2019.06.23.18.23.57.json"));
            Assert.False(AddressablesCatalog.IsCatalogFileName("aa/Windows/settings.json"));
        }

        [Fact]
        public void AssetsManager_FindsTheCatalogNextToTheFiles()
        {
            var folder = TestUtil.TempDirectory();
            var bundles = Path.Combine(folder, "aa", "Windows", "StandaloneWindows64");
            Directory.CreateDirectory(bundles);
            File.Copy(TestUtil.Fixture("synthetic_catalog.json"), Path.Combine(folder, "aa", "Windows", "catalog.json"));
            var bundle = Path.Combine(bundles, "characters_assets_all_0123456789abcdef0123456789abcdef.bundle");
            File.WriteAllBytes(bundle, new byte[64]);
            var manager = new AssetsManager { Game = GameManager.GetGame("Normal"), Silent = true };
            manager.LoadFiles(bundle);
            Assert.Single(manager.Catalogs);
            //kept when the files are cleared (the CLI loads one file at a time), dropped on request
            manager.Clear();
            Assert.Single(manager.Catalogs);
            manager.ClearCatalogs();
            Assert.Empty(manager.Catalogs);
        }
    }

    public class WebFileTests
    {
        [Fact]
        public void ReadsEveryEntry()
        {
            var files = new[] { ("data.unity3d", new byte[] { 1, 2, 3 }), ("Il2CppData/Metadata/global-metadata.dat", new byte[] { 4, 5 }), ("boot.config", Encoding.ASCII.GetBytes("gfx-enable-gfx-jobs=1")) };
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                var signature = Encoding.ASCII.GetBytes("UnityWebData1.0\0");
                var header = signature.Length + 4 + files.Sum(f => 12 + Encoding.UTF8.GetByteCount(f.Item1));
                w.Write(signature);
                w.Write(header);
                var offset = header;
                foreach (var (path, data) in files)
                {
                    w.Write(offset);
                    w.Write(data.Length);
                    w.Write(Encoding.UTF8.GetByteCount(path));
                    w.Write(Encoding.UTF8.GetBytes(path));
                    offset += data.Length;
                }
                foreach (var (_, data) in files)
                    w.Write(data);
            }
            ms.Position = 0;
            var web = new WebFile(new EndianBinaryReader(ms, EndianType.LittleEndian));
            Assert.Equal(files.Select(f => f.Item1), web.fileList.Select(f => f.path));
            Assert.Equal("global-metadata.dat", web.fileList[1].fileName);
            Assert.Equal(new byte[] { 4, 5 }, ((MemoryStream)web.fileList[1].stream).ToArray());
        }
    }

    public class TypeTreeDatabaseTests
    {
        private static JObject Node(List<TypeTreeNode> nodes, ref int index)
        {
            var node = nodes[index];
            var json = new JObject
            {
                ["TypeName"] = node.m_Type, ["Name"] = node.m_Name, ["Level"] = node.m_Level, ["ByteSize"] = node.m_ByteSize,
                ["Index"] = index, ["Version"] = node.m_Version, ["TypeFlags"] = node.m_TypeFlags, ["MetaFlag"] = node.m_MetaFlag,
            };
            var children = new JArray();
            index++;
            while (index < nodes.Count && nodes[index].m_Level > node.m_Level)
                children.Add(Node(nodes, ref index));
            json["SubNodes"] = children;
            return json;
        }

        [Fact]
        public void Dumps_ExtendTheEmbeddedDatabase()
        {
            var textAsset = (int)ClassIDType.TextAsset;
            var embedded = TypeTreeDatabase.GetTypeTree(textAsset, new[] { 2022, 3, 0, 1 }, new BuildType("f"));
            Assert.NotNull(embedded);
            //a dump of a future version where m_Script is renamed
            var nodes = embedded.m_Nodes.Select(x => new TypeTreeNode { m_Type = x.m_Type, m_Name = x.m_Name == "m_Script" ? "m_Script2" : x.m_Name, m_Level = x.m_Level, m_ByteSize = x.m_ByteSize, m_Version = x.m_Version, m_TypeFlags = x.m_TypeFlags, m_MetaFlag = x.m_MetaFlag }).ToList();
            var index = 0;
            var dump = new JObject
            {
                ["Version"] = "6100.1.0f1",
                ["Strings"] = new JArray(),
                ["Classes"] = new JArray(new JObject { ["Name"] = "TextAsset", ["TypeID"] = textAsset, ["ReleaseRootNode"] = Node(nodes, ref index), ["EditorRootNode"] = null }),
            };
            var folder = TestUtil.TempDirectory();
            File.WriteAllText(Path.Combine(folder, "6100.1.0f1.json"), dump.ToString());
            File.WriteAllText(Path.Combine(folder, "notes.json"), "{}");
            try
            {
                TypeTreeDatabase.DumpsDirectory = folder;
                Assert.Equal(new[] { "6100.1.0f1" }, TypeTreeDatabase.DumpVersions);
                var future = TypeTreeDatabase.GetTypeTree(textAsset, new[] { 6100, 2, 0, 1 }, new BuildType("f"));
                Assert.Contains(future.m_Nodes, x => x.m_Name == "m_Script2");
                Assert.Equal(embedded.m_Nodes.Count, future.m_Nodes.Count);
                //older versions keep the embedded trees; classes the dump doesn't have are absent from its version on
                var old = TypeTreeDatabase.GetTypeTree(textAsset, new[] { 2022, 3, 0, 1 }, new BuildType("f"));
                Assert.Contains(old.m_Nodes, x => x.m_Name == "m_Script");
                Assert.Null(TypeTreeDatabase.GetTypeTree((int)ClassIDType.Texture2D, new[] { 6100, 2, 0, 1 }, new BuildType("f")));
                Assert.NotNull(TypeTreeDatabase.GetTypeTree((int)ClassIDType.Texture2D, new[] { 6000, 0, 0, 1 }, new BuildType("f")));
            }
            finally
            {
                TypeTreeDatabase.DumpsDirectory = null;
            }
        }
    }

    public class SerializedFileTests
    {
        /// <summary>A SerializedFile of format 23 (Unity 6000.5+) built without type trees, with one TextAsset.</summary>
        private static byte[] PlayerFile(string name, byte[] script)
        {
            using var metadata = new MemoryStream();
            using (var w = new BinaryWriter(metadata, Encoding.UTF8, true))
            {
                w.Write(Encoding.ASCII.GetBytes("6000.6.0f1\0"));
                w.Write(20); //WebGL
                w.Write(false); //no type trees
                w.Write(1); //types
                w.Write((int)ClassIDType.TextAsset);
                w.Write((byte)0); //not stripped
                w.Write((short)-1); //script type index
                w.Write(new byte[16]); //old type hash; no type tree hash without type trees
                w.Write(1); //objects
                while ((48 + metadata.Position) % 4 != 0)
                    w.Write((byte)0);
                w.Write(1L); //path ID
                w.Write(0L); //byte start
                w.Write(0u); //byte size, patched below
                w.Write(0); //type index
                w.Write(0); //scripts
                w.Write(0); //externals
                w.Write(0); //reference types
                w.Write((byte)0); //user information
            }
            using var data = new MemoryStream();
            using (var w = new BinaryWriter(data, Encoding.UTF8, true))
            {
                TestUtil.WriteAligned(w, name);
                w.Write(script.Length);
                w.Write(script);
                while (data.Position % 4 != 0)
                    w.Write((byte)0);
            }
            var meta = metadata.ToArray();
            //the object's size: in front of its type index, the scripts, externals, reference types (4 bytes each) and the user info (1)
            BitConverter.TryWriteBytes(meta.AsSpan(meta.Length - 17 - 4), (uint)data.Length);
            var dataOffset = (48 + meta.Length + 15) / 16 * 16;
            var fileSize = dataOffset + data.Length;
            using var file = new MemoryStream();
            using (var w = new BinaryWriter(file, Encoding.UTF8, true))
            {
                w.Write(new byte[8]); //legacy metadata size and file size
                w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(23u));
                w.Write(0u); //legacy data offset
                w.Write((byte)0); //little endian
                w.Write(new byte[3]);
                w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)meta.Length));
                w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((long)fileSize));
                w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((long)dataOffset));
                w.Write(0L);
                w.Write(meta);
                w.Write(new byte[dataOffset - 48 - meta.Length]);
                w.Write(data.ToArray());
            }
            return file.ToArray();
        }

        [Fact]
        public void Format23WithoutTypeTrees_ReadsItsObjects()
        {
            var folder = TestUtil.TempDirectory();
            var path = Path.Combine(folder, "sharedassets0.assets");
            File.WriteAllBytes(path, PlayerFile("readme", Encoding.UTF8.GetBytes("hello from 6000.6")));
            var manager = new AssetsManager { Game = GameManager.GetGame("Normal"), Silent = true };
            manager.LoadFiles(path);
            var file = Assert.Single(manager.assetsFileList);
            var text = Assert.IsType<TextAsset>(Assert.Single(file.Objects));
            Assert.Equal("readme", text.m_Name);
            Assert.Equal("hello from 6000.6", Encoding.UTF8.GetString(text.m_Script));
        }
    }
}
