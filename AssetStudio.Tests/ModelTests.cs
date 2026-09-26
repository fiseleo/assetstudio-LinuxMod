using AssetStudio.Avalonia;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using NMatrix = System.Numerics.Matrix4x4;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace AssetStudio.Tests
{
    /// <summary>A small model: two bones, a skinned quad with a blend shape, bone and blend shape animations, a texture.</summary>
    internal sealed class SyntheticModel : IImported
    {
        public ImportedFrame RootFrame { get; } = new ImportedFrame();
        public List<ImportedMesh> MeshList { get; } = new List<ImportedMesh>();
        public List<ImportedMaterial> MaterialList { get; } = new List<ImportedMaterial>();
        public List<ImportedTexture> TextureList { get; } = new List<ImportedTexture>();
        public List<ImportedKeyframedAnimation> AnimationList { get; } = new List<ImportedKeyframedAnimation>();
        public List<ImportedMorph> MorphList { get; } = new List<ImportedMorph>();

        private static ImportedFrame Frame(string name, Vector3 position, Quaternion rotation, Vector3 scale) =>
            new ImportedFrame { Name = name, LocalPosition = position, LocalRotation = rotation, LocalScale = scale };

        private static NMatrix World(ImportedFrame frame)
        {
            var world = NMatrix.Identity;
            for (; frame != null; frame = frame.Parent)
            {
                var r = frame.LocalRotation;
                world *= NMatrix.CreateScale(frame.LocalScale.X, frame.LocalScale.Y, frame.LocalScale.Z)
                    * NMatrix.CreateFromQuaternion(r.W == 0 && r.X == 0 && r.Y == 0 && r.Z == 0 ? NQuaternion.Identity : new NQuaternion(r.X, r.Y, r.Z, r.W))
                    * NMatrix.CreateTranslation(frame.LocalPosition.X, frame.LocalPosition.Y, frame.LocalPosition.Z);
            }
            return world;
        }

        //bind poses: row-vector layout, translation in M30..M32
        private static Matrix4x4 Bind(ImportedFrame bone, ImportedFrame mesh)
        {
            NMatrix.Invert(World(bone), out var inverse);
            var m = World(mesh) * inverse;
            return new Matrix4x4 { M00 = m.M11, M01 = m.M12, M02 = m.M13, M03 = m.M14, M10 = m.M21, M11 = m.M22, M12 = m.M23, M13 = m.M24, M20 = m.M31, M21 = m.M32, M22 = m.M33, M23 = m.M34, M30 = m.M41, M31 = m.M42, M32 = m.M43, M33 = m.M44 };
        }

        public SyntheticModel()
        {
            RootFrame.Name = "Root";
            RootFrame.LocalRotation = new Quaternion(0, 0, 0, 0); //as ModelConverter does
            RootFrame.LocalScale = new Vector3(1, 1, 1);
            var hips = Frame("Hips", new Vector3(0, 1, 0), new Quaternion(0, 0, 0.3826834f, 0.9238795f), new Vector3(1, 1, 1));
            var spine = Frame("Spine", new Vector3(0, 1, 0), new Quaternion(0, 0, 0, 1), new Vector3(1, 1, 1));
            var body = Frame("Body", new Vector3(0.5f, 0, 0), new Quaternion(0, 0, 0, 1), new Vector3(2, 2, 2));
            RootFrame.AddChild(hips);
            hips.AddChild(spine);
            RootFrame.AddChild(body);

            var vertices = new List<ImportedVertex>();
            var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
            for (int i = 0; i < 4; i++)
            {
                vertices.Add(new ImportedVertex
                {
                    Vertex = positions[i],
                    Normal = new Vector3(0, 0, 1),
                    Tangent = new Vector4(1, 0, 0, 1),
                    UV = new[] { new[] { positions[i].X, positions[i].Y }, null, null, null, null, null, null, null },
                    BoneIndices = new[] { 0, 1, 0, 0 },
                    Weights = i < 2 ? new[] { 1f, 0, 0, 0 } : new[] { 0.25f, 0.75f, 0, 0 },
                });
            }
            var mesh = new ImportedMesh
            {
                Path = "Root/Body",
                VertexList = vertices,
                SubmeshList = new List<ImportedSubmesh>
                {
                    new ImportedSubmesh
                    {
                        Material = "Mat",
                        BaseVertex = 0,
                        FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 2, 1 } }, new ImportedFace { VertexIndices = new[] { 0, 3, 2 } } },
                    },
                },
                BoneList = new List<ImportedBone>
                {
                    new ImportedBone { Path = "Root/Hips", Matrix = Bind(hips, body) },
                    new ImportedBone { Path = "Root/Hips/Spine", Matrix = Bind(spine, body) },
                },
                hasNormal = true,
                hasTangent = true,
                hasUV = new[] { true, false, false, false, false, false, false, false },
                uvType = new int[8],
            };
            MeshList.Add(mesh);

            using var image = new Image<Rgba32>(2, 2);
            image[0, 0] = new Rgba32(255, 0, 0);
            using var png = new MemoryStream();
            image.SaveAsPng(png);
            TextureList.Add(new ImportedTexture(png, "Albedo.png"));
            MaterialList.Add(new ImportedMaterial
            {
                Name = "Mat",
                Diffuse = new Color(0.5f, 0.6f, 0.7f, 1),
                Emissive = new Color(0, 0, 0, 1),
                Textures = new List<ImportedMaterialTexture> { new ImportedMaterialTexture { Name = "Albedo.png", Dest = 0, Scale = new Vector2(2, 2), Offset = new Vector2(0.25f, 0) } },
            });

            MorphList.Add(new ImportedMorph
            {
                Path = "Root/Body",
                Channels = new List<ImportedMorphChannel>
                {
                    new ImportedMorphChannel
                    {
                        Name = "Smile",
                        KeyframeList = new List<ImportedMorphKeyframe>
                        {
                            new ImportedMorphKeyframe
                            {
                                Weight = 100,
                                VertexList = new List<ImportedMorphVertex>
                                {
                                    new ImportedMorphVertex { Index = 2, Vertex = new ImportedVertex { Vertex = new Vector3(1.5f, 1.25f, 0), Normal = new Vector3(0, 0, 0) } },
                                },
                            },
                        },
                    },
                },
            });

            var animation = new ImportedKeyframedAnimation { Name = "Wave", SampleRate = 30, TrackList = new List<ImportedAnimationKeyframedTrack>() };
            var spineTrack = animation.FindTrack("Root/Hips/Spine");
            spineTrack.Rotations.Add(new ImportedKeyframe<Quaternion>(0, new Quaternion(0, 0, 0, 1)));
            spineTrack.Rotations.Add(new ImportedKeyframe<Quaternion>(1, new Quaternion(0, 0, 0.7071068f, 0.7071068f)));
            var hipsTrack = animation.FindTrack("Root/Hips");
            hipsTrack.Translations.Add(new ImportedKeyframe<Vector3>(0, new Vector3(0, 1, 0)));
            hipsTrack.Translations.Add(new ImportedKeyframe<Vector3>(1, new Vector3(0, 2, 0.5f)));
            hipsTrack.Scalings.Add(new ImportedKeyframe<Vector3>(0, new Vector3(1, 1, 1)));
            hipsTrack.Scalings.Add(new ImportedKeyframe<Vector3>(1, new Vector3(1.5f, 1, 1)));
            var smile = animation.FindTrack("Root/Body", "Smile");
            smile.BlendShape = new ImportedBlendShape { ChannelName = "Smile" };
            smile.BlendShape.Keyframes.Add(new ImportedKeyframe<float>(0, 0));
            smile.BlendShape.Keyframes.Add(new ImportedKeyframe<float>(1, 100));
            AnimationList.Add(animation);
        }
    }

    /// <summary>Evaluates a GLB: node transforms and animations, morph targets, skinning (the glTF 2.0 rules).</summary>
    internal sealed class GlbModel
    {
        public JObject Json;
        private byte[] bin;

        public static GlbModel Load(string path)
        {
            var data = File.ReadAllBytes(path);
            Assert.Equal(0x46546C67u, BitConverter.ToUInt32(data, 0));
            Assert.Equal((uint)data.Length, BitConverter.ToUInt32(data, 8));
            var jsonLength = BitConverter.ToInt32(data, 12);
            var model = new GlbModel { Json = JObject.Parse(System.Text.Encoding.UTF8.GetString(data, 20, jsonLength)) };
            var binStart = 20 + jsonLength;
            if (binStart < data.Length)
                model.bin = data.AsSpan(binStart + 8, BitConverter.ToInt32(data, binStart)).ToArray();
            return model;
        }

        public float[] Floats(int accessor)
        {
            var a = Json["accessors"][accessor];
            var view = Json["bufferViews"][(int)a["bufferView"]];
            var components = (string)a["type"] switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 0 };
            var count = (int)a["count"] * components;
            var offset = (int)(view["byteOffset"] ?? 0);
            var result = new float[count];
            switch ((int)a["componentType"])
            {
                case 5126: Buffer.BlockCopy(bin, offset, result, 0, count * 4); break;
                case 5123: for (int i = 0; i < count; i++) result[i] = BitConverter.ToUInt16(bin, offset + i * 2); break;
                case 5125: for (int i = 0; i < count; i++) result[i] = BitConverter.ToUInt32(bin, offset + i * 4); break;
            }
            return result;
        }

        public NVector3[] Evaluate(string animationName, float time)
        {
            var nodes = (JArray)Json["nodes"];
            var n = nodes.Count;
            var t = new NVector3[n]; var r = new NQuaternion[n]; var s = new NVector3[n];
            var weights = new Dictionary<int, float[]>();
            for (int i = 0; i < n; i++)
            {
                var node = nodes[i];
                t[i] = node["translation"] is JArray tr ? new NVector3((float)tr[0], (float)tr[1], (float)tr[2]) : NVector3.Zero;
                r[i] = node["rotation"] is JArray ro ? new NQuaternion((float)ro[0], (float)ro[1], (float)ro[2], (float)ro[3]) : NQuaternion.Identity;
                s[i] = node["scale"] is JArray sc ? new NVector3((float)sc[0], (float)sc[1], (float)sc[2]) : NVector3.One;
                if (node["mesh"] != null && Json["meshes"][(int)node["mesh"]]["weights"] is JArray w)
                    weights[i] = w.Select(x => (float)x).ToArray();
            }
            var animation = (Json["animations"] as JArray)?.FirstOrDefault(a => (string)a["name"] == animationName);
            foreach (var channel in animation?["channels"] ?? new JArray())
            {
                var sampler = animation["samplers"][(int)channel["sampler"]];
                var times = Floats((int)sampler["input"]);
                var values = Floats((int)sampler["output"]);
                var node = (int)channel["target"]["node"];
                var path = (string)channel["target"]["path"];
                int k = 0;
                while (k < times.Length - 1 && times[k + 1] < time)
                    k++;
                int k2 = k; float f = 0;
                if (time <= times[0]) k = k2 = 0;
                else if (time >= times[^1]) k = k2 = times.Length - 1;
                else { k2 = k + 1; f = (time - times[k]) / (times[k2] - times[k]); }
                NVector3 V3(int i) => new NVector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
                switch (path)
                {
                    case "translation": t[node] = NVector3.Lerp(V3(k), V3(k2), f); break;
                    case "scale": s[node] = NVector3.Lerp(V3(k), V3(k2), f); break;
                    case "rotation": r[node] = NQuaternion.Slerp(new NQuaternion(values[k * 4], values[k * 4 + 1], values[k * 4 + 2], values[k * 4 + 3]), new NQuaternion(values[k2 * 4], values[k2 * 4 + 1], values[k2 * 4 + 2], values[k2 * 4 + 3]), f); break;
                    case "weights":
                        var targets = weights[node].Length;
                        for (int j = 0; j < targets; j++)
                            weights[node][j] = values[k * targets + j] + (values[k2 * targets + j] - values[k * targets + j]) * f;
                        break;
                }
            }
            var parent = new int[n];
            Array.Fill(parent, -1);
            for (int i = 0; i < n; i++)
                foreach (var child in nodes[i]["children"] as JArray ?? new JArray())
                    parent[(int)child] = i;
            var world = new NMatrix?[n];
            NMatrix World(int i)
            {
                if (world[i] is { } cached)
                    return cached;
                var node = nodes[i];
                NMatrix local;
                if (node["matrix"] is JArray m)
                {
                    var v = m.Select(x => (float)x).ToArray();
                    local = new NMatrix(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
                }
                else
                {
                    local = NMatrix.CreateScale(s[i]) * NMatrix.CreateFromQuaternion(NQuaternion.Normalize(r[i])) * NMatrix.CreateTranslation(t[i]);
                }
                var result = parent[i] < 0 ? local : local * World(parent[i]);
                world[i] = result;
                return result;
            }
            var output = new List<NVector3>();
            foreach (var i in Enumerable.Range(0, n).Where(i => nodes[i]["mesh"] != null).OrderBy(i => (int)nodes[i]["mesh"]))
            {
                var primitive = Json["meshes"][(int)nodes[i]["mesh"]]["primitives"][0];
                var attributes = primitive["attributes"];
                var positions = Floats((int)attributes["POSITION"]);
                if (primitive["targets"] is JArray targets)
                {
                    for (int target = 0; target < targets.Count; target++)
                    {
                        var delta = Floats((int)targets[target]["POSITION"]);
                        for (int p = 0; p < positions.Length; p++)
                            positions[p] += weights[i][target] * delta[p];
                    }
                }
                NMatrix[] joints = null;
                float[] jointIndices = null, jointWeights = null;
                if (nodes[i]["skin"] != null)
                {
                    var skin = Json["skins"][(int)nodes[i]["skin"]];
                    var ibm = Floats((int)skin["inverseBindMatrices"]);
                    joints = ((JArray)skin["joints"]).Select((j, b) => new NMatrix(ibm[b * 16], ibm[b * 16 + 1], ibm[b * 16 + 2], ibm[b * 16 + 3], ibm[b * 16 + 4], ibm[b * 16 + 5], ibm[b * 16 + 6], ibm[b * 16 + 7],
                        ibm[b * 16 + 8], ibm[b * 16 + 9], ibm[b * 16 + 10], ibm[b * 16 + 11], ibm[b * 16 + 12], ibm[b * 16 + 13], ibm[b * 16 + 14], ibm[b * 16 + 15]) * World((int)j)).ToArray();
                    jointIndices = Floats((int)attributes["JOINTS_0"]);
                    jointWeights = Floats((int)attributes["WEIGHTS_0"]);
                }
                for (int v = 0; v < positions.Length / 3; v++)
                {
                    var p = new NVector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
                    if (joints == null)
                    {
                        output.Add(NVector3.Transform(p, World(i)));
                        continue;
                    }
                    var m = default(NMatrix);
                    for (int k = 0; k < 4; k++)
                        m += joints[(int)jointIndices[v * 4 + k]] * jointWeights[v * 4 + k];
                    output.Add(NVector3.Transform(p, m));
                }
            }
            return output.ToArray();
        }
    }

    public class GltfExportTests
    {
        private static Fbx.ExportOptions Options => new Fbx.ExportOptions { exportSkins = true, exportAnimations = true, exportBlendShape = true, exportAllNodes = true, scaleFactor = 1 };

        [Fact]
        public void Glb_MatchesThePreviewPosesAtEveryTime()
        {
            var model = new SyntheticModel();
            var path = Path.Combine(TestUtil.TempDirectory(), "model.fbx");
            path = ModelExporter.ExportModel(path, model, Options, ModelFormat.Glb);
            Assert.EndsWith(".glb", path);
            var glb = GlbModel.Load(path);

            var vertices = model.MeshList.SelectMany(m => m.VertexList.Select(v => new NVector3(v.Vertex.X, v.Vertex.Y, v.Vertex.Z))).ToArray();
            var animator = new ModelAnimator(model, vertices, null);
            Assert.Equal(1, animator.ClipCount);
            foreach (var time in new[] { 0f, 0.3f, 0.5f, 0.85f, 1f })
            {
                var expected = new NVector3[vertices.Length];
                animator.Pose(0, time, expected, null);
                var actual = glb.Evaluate("Wave", time);
                Assert.Equal(expected.Length, actual.Length);
                for (int i = 0; i < expected.Length; i++)
                    Assert.True((expected[i] - actual[i]).Length() < 1e-4f, $"t={time} vertex {i}: {expected[i]} vs {actual[i]}");
            }
            //the rest pose too
            var rest = new NVector3[vertices.Length];
            animator.Pose(-1, 0, rest, null);
            var restGlb = glb.Evaluate(null, 0);
            for (int i = 0; i < rest.Length; i++)
                Assert.True((rest[i] - restGlb[i]).Length() < 1e-4f);
        }

        [Fact]
        public void Glb_HasAValidStructure()
        {
            var model = new SyntheticModel();
            var path = ModelExporter.ExportModel(Path.Combine(TestUtil.TempDirectory(), "model"), model, Options, ModelFormat.Glb);
            var json = GlbModel.Load(path).Json;
            Assert.Equal("2.0", (string)json["asset"]["version"]);
            var skin = json["skins"].Single();
            var joints = ((JArray)skin["joints"]).Select(x => (int)x).ToArray();
            Assert.Equal(joints.Length, joints.Distinct().Count());
            Assert.Equal(new[] { "Hips", "Spine" }, joints.Select(j => (string)json["nodes"][j]["name"]).ToArray());
            //the skinned mesh is on a child node of its own (no transform, no animation on it)
            var skinned = json["nodes"].Single(x => x["skin"] != null);
            Assert.Null(skinned["translation"]);
            Assert.Equal("Body_skinned", (string)skinned["name"]);
            var mesh = json["meshes"].Single();
            Assert.Equal(new[] { "Smile" }, mesh["extras"]["targetNames"].Select(x => (string)x).ToArray());
            var primitive = mesh["primitives"].Single();
            Assert.NotNull(primitive["attributes"]["TANGENT"]);
            //the bounds of the positions
            var position = json["accessors"][(int)primitive["attributes"]["POSITION"]];
            Assert.Equal(new[] { 0f, 0f, 0f }, position["min"].Select(x => (float)x).ToArray());
            Assert.Equal(new[] { 1f, 1f, 0f }, position["max"].Select(x => (float)x).ToArray());
            //V goes down in glTF; the texture transform too
            var material = json["materials"].Single();
            Assert.Equal(new[] { 0.5f, 0.6f, 0.7f, 1f }, material["pbrMetallicRoughness"]["baseColorFactor"].Select(x => (float)x).ToArray());
            var transform = material["pbrMetallicRoughness"]["baseColorTexture"]["extensions"]["KHR_texture_transform"];
            Assert.Equal(new[] { 0.25f, -1f }, transform["offset"].Select(x => (float)x).ToArray());
            Assert.Contains("KHR_texture_transform", json["extensionsUsed"].Select(x => (string)x));
            Assert.Equal("image/png", (string)json["images"].Single()["mimeType"]);
            //mirroring flips the tangent frame
            var tangents = GlbModel.Load(path).Floats((int)primitive["attributes"]["TANGENT"]);
            Assert.Equal(-1f, tangents[3]);
        }

        [Fact]
        public void Gltf_WritesTheBufferAndTheImagesNextToIt()
        {
            var folder = TestUtil.TempDirectory();
            var path = ModelExporter.ExportModel(Path.Combine(folder, "model.fbx"), new SyntheticModel(), Options, ModelFormat.Gltf);
            Assert.Equal(Path.Combine(folder, "model.gltf"), path);
            var json = JObject.Parse(File.ReadAllText(path));
            Assert.Equal("model.bin", (string)json["buffers"][0]["uri"]);
            Assert.Equal(new FileInfo(Path.Combine(folder, "model.bin")).Length, (long)json["buffers"][0]["byteLength"]);
            Assert.True(File.Exists(Path.Combine(folder, "Albedo.png")));
        }

        [Fact]
        public void MissingBones_GetNodesAtTheirBindPose()
        {
            var model = new SyntheticModel();
            //a mesh exported without its skeleton
            model.MeshList[0].BoneList[1].Path = "Root/Elsewhere/Spine";
            var path = ModelExporter.ExportModel(Path.Combine(TestUtil.TempDirectory(), "model"), model, Options, ModelFormat.Glb);
            var glb = GlbModel.Load(path);
            var joints = ((JArray)glb.Json["skins"][0]["joints"]).Select(x => (int)x).ToArray();
            Assert.Equal(joints.Length, joints.Distinct().Count());
            //at rest the mesh is where a rigid mesh of the frame would be
            var rest = glb.Evaluate(null, 0);
            var body = model.RootFrame.FindFrameByPath("Root/Body");
            var world = NMatrix.CreateScale(2) * NMatrix.CreateTranslation(0.5f, 0, 0);
            for (int i = 0; i < rest.Length; i++)
            {
                var v = model.MeshList[0].VertexList[i].Vertex;
                var expected = NVector3.Transform(new NVector3(v.X, v.Y, v.Z), world);
                Assert.True((expected - rest[i]).Length() < 1e-4f, $"{expected} vs {rest[i]}");
            }
            Assert.NotNull(body);
        }
    }

    public class BlendShapeSetTests
    {
        private static SyntheticModel ModelWithInBetween()
        {
            var model = new SyntheticModel();
            var channel = model.MorphList[0].Channels[0];
            //an in-between shape at 50
            channel.KeyframeList.Insert(0, new ImportedMorphKeyframe
            {
                Weight = 50,
                VertexList = new List<ImportedMorphVertex> { new ImportedMorphVertex { Index = 2, Vertex = new ImportedVertex { Vertex = new Vector3(1, 1, 1) } } },
            });
            return model;
        }

        [Theory]
        [InlineData(0, 1, 1, 0)]
        [InlineData(25, 1, 1, 0.5f)] //half of the first frame
        [InlineData(50, 1, 1, 1)] //the first frame
        [InlineData(75, 1.25f, 1.125f, 0.5f)] //between the frames
        [InlineData(100, 1.5f, 1.25f, 0)] //the last frame
        [InlineData(150, 1.75f, 1.375f, 0)] //extrapolated from the last frame
        public void Weights_BlendLikeUnity(float weight, float x, float y, float z)
        {
            var model = ModelWithInBetween();
            var set = BlendShapeSet.FromModel(model);
            var base_ = model.MeshList[0].VertexList.Select(v => new NVector3(v.Vertex.X, v.Vertex.Y, v.Vertex.Z)).ToArray();
            var positions = new NVector3[base_.Length];
            var applied = set.Apply(base_, null, new[] { weight }, positions, null);
            if (weight == 0)
            {
                Assert.False(applied);
                return;
            }
            Assert.True(applied);
            Assert.Equal(new NVector3(x, y, z), positions[2]);
            Assert.Equal(base_[0], positions[0]);
        }

        [Fact]
        public void Clips_DriveTheWeights()
        {
            var model = new SyntheticModel();
            var vertices = model.MeshList[0].VertexList.Select(v => new NVector3(v.Vertex.X, v.Vertex.Y, v.Vertex.Z)).ToArray();
            var animator = new ModelAnimator(model, vertices, null);
            animator.Pose(0, 0.5f, new NVector3[vertices.Length], null);
            Assert.Equal(50, animator.PosedBlendShapeWeights[0], 3);
            animator.BlendShapeWeights[0] = 30;
            animator.Pose(-1, 0, new NVector3[vertices.Length], null);
            Assert.Equal(30, animator.PosedBlendShapeWeights[0]);
        }
    }
}
