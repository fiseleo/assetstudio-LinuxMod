using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AssetStudio
{
    public enum ModelFormat
    {
        Fbx,
        /// <summary>glTF 2.0: .gltf with a .bin and the images next to it</summary>
        Gltf,
        /// <summary>glTF 2.0 binary: everything in one .glb</summary>
        Glb,
    }

    /// <summary>
    /// Writes the models of <see cref="ModelConverter"/> as glTF 2.0, without the FBX SDK: node hierarchy, meshes with their
    /// sub meshes, materials and textures, skins, blend shapes and animations (transforms and blend shape weights).
    /// The imported data is right-handed Y-up like glTF (ModelConverter mirrors X for FBX); only V and the tangent
    /// handedness change.
    /// </summary>
    public sealed class GltfExporter
    {
        private const int FLOAT = 5126, UNSIGNED_SHORT = 5123, UNSIGNED_INT = 5125;
        private const int ARRAY_BUFFER = 34962, ELEMENT_ARRAY_BUFFER = 34963;

        private readonly IImported imported;
        private readonly Fbx.ExportOptions options;
        private readonly bool binary;
        private readonly string path;

        private readonly MemoryStream buffer = new MemoryStream();
        private readonly JArray bufferViews = new JArray();
        private readonly JArray accessors = new JArray();
        private readonly JArray nodes = new JArray();
        private readonly JArray meshes = new JArray();
        private readonly JArray materials = new JArray();
        private readonly JArray textures = new JArray();
        private readonly JArray images = new JArray();
        private readonly JArray samplers = new JArray();
        private readonly JArray skins = new JArray();
        private readonly JArray animations = new JArray();
        private readonly JArray sceneRoots = new JArray();
        private readonly Dictionary<ImportedFrame, int> nodeIndices = new Dictionary<ImportedFrame, int>();
        private readonly Dictionary<string, int> materialIndices = new Dictionary<string, int>();
        private readonly Dictionary<string, int> textureIndices = new Dictionary<string, int>();
        private readonly Dictionary<string, (int node, List<string> targetNames)> morphMeshes = new Dictionary<string, (int, List<string>)>();
        private readonly HashSet<string> writtenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> extensionsUsed = new HashSet<string>();

        private GltfExporter(string path, IImported imported, Fbx.ExportOptions options, bool binary)
        {
            this.path = path;
            this.imported = imported;
            this.options = options;
            this.binary = binary;
        }

        public static void Export(string path, IImported imported, Fbx.ExportOptions options, bool binary)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            new GltfExporter(path, imported, options, binary).Write();
        }

        private float Scale => options.scaleFactor > 0 ? options.scaleFactor : 1f;

        private void Write()
        {
            if (imported.RootFrame != null)
            {
                sceneRoots.Add(AddNode(imported.RootFrame));
            }
            foreach (var mesh in imported.MeshList ?? new List<ImportedMesh>())
            {
                AddMesh(mesh);
            }
            if (options.exportAnimations)
            {
                foreach (var animation in imported.AnimationList ?? new List<ImportedKeyframedAnimation>())
                {
                    AddAnimation(animation);
                }
            }

            var gltf = new JObject
            {
                ["asset"] = new JObject { ["version"] = "2.0", ["generator"] = "AssetStudio" },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = sceneRoots }),
                ["nodes"] = nodes,
            };
            void AddArray(string name, JArray array)
            {
                if (array.Count > 0)
                    gltf[name] = array;
            }
            AddArray("meshes", meshes);
            AddArray("materials", materials);
            AddArray("textures", textures);
            AddArray("images", images);
            AddArray("samplers", samplers);
            AddArray("skins", skins);
            AddArray("animations", animations);
            AddArray("accessors", accessors);
            if (extensionsUsed.Count > 0)
                gltf["extensionsUsed"] = new JArray(extensionsUsed.OrderBy(x => x));
            AddArray("bufferViews", bufferViews);

            Align(buffer, 4);
            var bufferData = buffer.ToArray();
            var gltfBuffer = new JObject { ["byteLength"] = bufferData.Length };
            if (bufferData.Length > 0)
            {
                if (!binary)
                {
                    var binName = Path.GetFileNameWithoutExtension(path) + ".bin";
                    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), binName), bufferData);
                    gltfBuffer["uri"] = Uri.EscapeDataString(binName);
                }
                gltf["buffers"] = new JArray(gltfBuffer);
            }

            var json = gltf.ToString(binary ? Formatting.None : Formatting.Indented);
            if (!binary)
            {
                File.WriteAllText(path, json);
                return;
            }
            //GLB: header, JSON chunk (padded with spaces), BIN chunk (padded with zeros)
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var jsonLength = (jsonBytes.Length + 3) & ~3;
            using var file = File.Create(path);
            using var writer = new BinaryWriter(file);
            var total = 12 + 8 + jsonLength + (bufferData.Length > 0 ? 8 + bufferData.Length : 0);
            writer.Write(0x46546C67u); //glTF
            writer.Write(2u);
            writer.Write((uint)total);
            writer.Write((uint)jsonLength);
            writer.Write(0x4E4F534Au); //JSON
            writer.Write(jsonBytes);
            for (int i = jsonBytes.Length; i < jsonLength; i++)
                writer.Write((byte)' ');
            if (bufferData.Length > 0)
            {
                writer.Write((uint)bufferData.Length);
                writer.Write(0x004E4942u); //BIN
                writer.Write(bufferData);
            }
        }

        #region Buffers

        private static void Align(Stream stream, int alignment)
        {
            while (stream.Length % alignment != 0)
                stream.WriteByte(0);
        }

        private int AddBufferView(byte[] data, int? target, int? byteStride = null)
        {
            Align(buffer, 4);
            var view = new JObject { ["buffer"] = 0, ["byteOffset"] = buffer.Length, ["byteLength"] = data.Length };
            if (target.HasValue)
                view["target"] = target.Value;
            if (byteStride.HasValue)
                view["byteStride"] = byteStride.Value;
            buffer.Write(data, 0, data.Length);
            bufferViews.Add(view);
            return bufferViews.Count - 1;
        }

        private int AddAccessor(float[] values, int components, string type, int? target, bool minMax = false)
        {
            var data = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, data, 0, data.Length);
            var accessor = new JObject
            {
                ["bufferView"] = AddBufferView(data, target),
                ["componentType"] = FLOAT,
                ["count"] = values.Length / components,
                ["type"] = type,
            };
            if (minMax && values.Length >= components)
            {
                var min = new float[components];
                var max = new float[components];
                for (int c = 0; c < components; c++)
                {
                    min[c] = float.MaxValue;
                    max[c] = float.MinValue;
                }
                for (int i = 0; i < values.Length; i++)
                {
                    min[i % components] = Math.Min(min[i % components], values[i]);
                    max[i % components] = Math.Max(max[i % components], values[i]);
                }
                accessor["min"] = new JArray(min);
                accessor["max"] = new JArray(max);
            }
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        private int AddIndexAccessor(uint[] indices, uint maxIndex)
        {
            byte[] data;
            int componentType;
            if (maxIndex < ushort.MaxValue)
            {
                data = new byte[indices.Length * 2];
                for (int i = 0; i < indices.Length; i++)
                {
                    BitConverter.TryWriteBytes(data.AsSpan(i * 2), (ushort)indices[i]);
                }
                componentType = UNSIGNED_SHORT;
            }
            else
            {
                data = new byte[indices.Length * 4];
                Buffer.BlockCopy(indices, 0, data, 0, data.Length);
                componentType = UNSIGNED_INT;
            }
            accessors.Add(new JObject
            {
                ["bufferView"] = AddBufferView(data, ELEMENT_ARRAY_BUFFER),
                ["componentType"] = componentType,
                ["count"] = indices.Length,
                ["type"] = "SCALAR",
            });
            return accessors.Count - 1;
        }

        #endregion

        #region Nodes

        private static JArray Quat(Quaternion q)
        {
            var length = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (length < 1e-6f || float.IsNaN(length))
                return new JArray(0f, 0f, 0f, 1f);
            return new JArray(q.X / length, q.Y / length, q.Z / length, q.W / length);
        }

        private int AddNode(ImportedFrame frame)
        {
            var node = new JObject { ["name"] = frame.Name ?? "" };
            var index = nodes.Count;
            nodes.Add(node);
            nodeIndices[frame] = index;

            var t = frame.LocalPosition;
            if (t.X != 0 || t.Y != 0 || t.Z != 0)
                node["translation"] = new JArray(t.X * Scale, t.Y * Scale, t.Z * Scale);
            var r = Quat(frame.LocalRotation);
            if ((float)r[3] != 1f)
                node["rotation"] = r;
            var s = frame.LocalScale;
            if (s.X != 1 || s.Y != 1 || s.Z != 1)
                node["scale"] = new JArray(s.X, s.Y, s.Z);

            if (frame.Count > 0)
            {
                var children = new JArray();
                for (int i = 0; i < frame.Count; i++)
                {
                    children.Add(AddNode(frame[i]));
                }
                node["children"] = children;
            }
            return index;
        }

        private int FindNode(string framePath)
        {
            if (string.IsNullOrEmpty(framePath) || imported.RootFrame == null)
                return -1;
            var frame = imported.RootFrame.FindFrameByPath(framePath);
            return frame != null && nodeIndices.TryGetValue(frame, out var index) ? index : -1;
        }

        #endregion

        #region Meshes

        private void AddMesh(ImportedMesh mesh)
        {
            var nodeIndex = FindNode(mesh.Path);
            if (nodeIndex < 0 || mesh.VertexList == null || mesh.VertexList.Count == 0)
                return;
            var vertices = mesh.VertexList;
            var count = vertices.Count;

            var attributes = new JObject();
            var positions = new float[count * 3];
            for (int i = 0; i < count; i++)
            {
                var v = vertices[i].Vertex;
                positions[i * 3] = v.X * Scale;
                positions[i * 3 + 1] = v.Y * Scale;
                positions[i * 3 + 2] = v.Z * Scale;
            }
            attributes["POSITION"] = AddAccessor(positions, 3, "VEC3", ARRAY_BUFFER, true);
            if (mesh.hasNormal)
            {
                var normals = new float[count * 3];
                for (int i = 0; i < count; i++)
                {
                    var n = vertices[i].Normal;
                    var length = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
                    if (length < 1e-6f || float.IsNaN(length))
                    {
                        n = new Vector3(0, 1, 0);
                        length = 1;
                    }
                    normals[i * 3] = n.X / length;
                    normals[i * 3 + 1] = n.Y / length;
                    normals[i * 3 + 2] = n.Z / length;
                }
                attributes["NORMAL"] = AddAccessor(normals, 3, "VEC3", ARRAY_BUFFER);
                if (mesh.hasTangent)
                {
                    var tangents = new float[count * 4];
                    for (int i = 0; i < count; i++)
                    {
                        var tangent = vertices[i].Tangent;
                        var length = MathF.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y + tangent.Z * tangent.Z);
                        if (length < 1e-6f || float.IsNaN(length))
                        {
                            tangent = new Vector4(1, 0, 0, 1);
                            length = 1;
                        }
                        tangents[i * 4] = tangent.X / length;
                        tangents[i * 4 + 1] = tangent.Y / length;
                        tangents[i * 4 + 2] = tangent.Z / length;
                        //mirroring X flips the handedness of the tangent frame
                        tangents[i * 4 + 3] = tangent.W < 0 ? 1f : -1f;
                    }
                    attributes["TANGENT"] = AddAccessor(tangents, 4, "VEC4", ARRAY_BUFFER);
                }
            }
            var texCoord = 0;
            for (int uv = 0; uv < (mesh.hasUV?.Length ?? 0); uv++)
            {
                if (!mesh.hasUV[uv])
                    continue;
                var uvs = new float[count * 2];
                for (int i = 0; i < count; i++)
                {
                    var value = vertices[i].UV?[uv];
                    if (value == null)
                        continue;
                    uvs[i * 2] = value[0];
                    uvs[i * 2 + 1] = 1f - value[1]; //glTF's V goes down
                }
                attributes[$"TEXCOORD_{texCoord++}"] = AddAccessor(uvs, 2, "VEC2", ARRAY_BUFFER);
            }
            if (mesh.hasColor)
            {
                var colors = new float[count * 4];
                for (int i = 0; i < count; i++)
                {
                    var c = vertices[i].Color;
                    colors[i * 4] = c.R;
                    colors[i * 4 + 1] = c.G;
                    colors[i * 4 + 2] = c.B;
                    colors[i * 4 + 3] = c.A;
                }
                attributes["COLOR_0"] = AddAccessor(colors, 4, "VEC4", ARRAY_BUFFER);
            }

            var skinned = options.exportSkins && mesh.BoneList?.Count > 0 && vertices[0].BoneIndices != null;
            if (skinned)
            {
                //the transform of a skinned mesh node doesn't apply (its bones place it): a child node of its own keeps the
                //animations of the frame off the skinned node
                var frameNode = (JObject)nodes[nodeIndex];
                nodes.Add(new JObject { ["name"] = frameNode["name"] + "_skinned" });
                var children = frameNode["children"] as JArray ?? new JArray();
                children.Add(nodes.Count - 1);
                frameNode["children"] = children;
                nodeIndex = nodes.Count - 1;
                AddSkin(mesh, nodeIndex, attributes);
            }

            var gltfMesh = new JObject { ["name"] = nodes[nodeIndex]["name"] };
            var primitives = new JArray();
            foreach (var submesh in mesh.SubmeshList ?? new List<ImportedSubmesh>())
            {
                if (submesh.FaceList == null || submesh.FaceList.Count == 0)
                    continue;
                var indices = new uint[submesh.FaceList.Count * 3];
                uint maxIndex = 0;
                for (int f = 0; f < submesh.FaceList.Count; f++)
                {
                    var face = submesh.FaceList[f].VertexIndices;
                    for (int k = 0; k < 3; k++)
                    {
                        var index = (uint)(face[k] + submesh.BaseVertex);
                        indices[f * 3 + k] = index;
                        maxIndex = Math.Max(maxIndex, index);
                    }
                }
                var primitive = new JObject
                {
                    ["attributes"] = attributes,
                    ["indices"] = AddIndexAccessor(indices, maxIndex),
                    ["mode"] = 4,
                };
                var material = AddMaterial(submesh.Material);
                if (material >= 0)
                    primitive["material"] = material;
                primitives.Add(primitive);
            }
            if (primitives.Count == 0)
                return;
            gltfMesh["primitives"] = primitives;
            if (options.exportBlendShape)
            {
                AddMorphTargets(mesh, gltfMesh, primitives, nodeIndex);
            }
            meshes.Add(gltfMesh);
            nodes[nodeIndex]["mesh"] = meshes.Count - 1;
        }

        private void AddSkin(ImportedMesh mesh, int meshNode, JObject attributes)
        {
            var vertices = mesh.VertexList;
            var joints = new JArray();
            var inverseBindMatrices = new float[mesh.BoneList.Count * 16];
            for (int b = 0; b < mesh.BoneList.Count; b++)
            {
                var bone = mesh.BoneList[b];
                //the bind pose is in row-vector layout (translation in M30..M32): row-major is glTF's column-major
                var m = bone.Matrix;
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        inverseBindMatrices[b * 16 + row * 4 + column] = m[row, column];
                    }
                }
                inverseBindMatrices[b * 16 + 12] *= Scale;
                inverseBindMatrices[b * 16 + 13] *= Scale;
                inverseBindMatrices[b * 16 + 14] *= Scale;
                var node = FindNode(bone.Path);
                if (node < 0)
                {
                    //a bone that is not in the hierarchy (a mesh exported without its skeleton): a node of its own at its bind pose
                    node = AddBindPoseNode(bone.Path ?? $"bone{b}", inverseBindMatrices.AsSpan(b * 16, 16), imported.RootFrame.FindFrameByPath(mesh.Path));
                }
                joints.Add(node);
            }

            var jointData = new byte[vertices.Count * 8];
            var weights = new float[vertices.Count * 4];
            for (int i = 0; i < vertices.Count; i++)
            {
                var boneIndices = vertices[i].BoneIndices;
                var boneWeights = vertices[i].Weights;
                var sum = 0f;
                for (int k = 0; k < 4; k++)
                {
                    var bone = boneIndices?[k] ?? 0;
                    var weight = boneWeights?[k] ?? 0;
                    if (bone < 0 || bone >= mesh.BoneList.Count || weight <= 0)
                    {
                        bone = 0;
                        weight = 0;
                    }
                    BitConverter.TryWriteBytes(jointData.AsSpan(i * 8 + k * 2), (ushort)bone);
                    weights[i * 4 + k] = weight;
                    sum += weight;
                }
                if (sum > 0)
                {
                    for (int k = 0; k < 4; k++)
                        weights[i * 4 + k] /= sum;
                }
                else
                {
                    weights[i * 4] = 1f;
                }
            }
            accessors.Add(new JObject
            {
                ["bufferView"] = AddBufferView(jointData, ARRAY_BUFFER),
                ["componentType"] = UNSIGNED_SHORT,
                ["count"] = vertices.Count,
                ["type"] = "VEC4",
            });
            attributes["JOINTS_0"] = accessors.Count - 1;
            attributes["WEIGHTS_0"] = AddAccessor(weights, 4, "VEC4", ARRAY_BUFFER);

            skins.Add(new JObject
            {
                ["joints"] = joints,
                ["inverseBindMatrices"] = AddAccessor(inverseBindMatrices, 16, "MAT4", null),
            });
            nodes[meshNode]["skin"] = skins.Count - 1;
        }

        private static System.Numerics.Quaternion ToNumerics(JArray q) => new System.Numerics.Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);

        /// <summary>The world transform of a frame (row vectors, like the node transforms).</summary>
        private System.Numerics.Matrix4x4 FrameWorld(ImportedFrame frame)
        {
            var world = System.Numerics.Matrix4x4.Identity;
            for (; frame != null; frame = frame.Parent)
            {
                world *= System.Numerics.Matrix4x4.CreateScale(frame.LocalScale.X, frame.LocalScale.Y, frame.LocalScale.Z)
                    * System.Numerics.Matrix4x4.CreateFromQuaternion(ToNumerics(Quat(frame.LocalRotation)))
                    * System.Numerics.Matrix4x4.CreateTranslation(frame.LocalPosition.X * Scale, frame.LocalPosition.Y * Scale, frame.LocalPosition.Z * Scale);
            }
            return world;
        }

        private int AddBindPoseNode(string bonePath, ReadOnlySpan<float> inverseBindMatrix, ImportedFrame meshFrame)
        {
            var ibm = new System.Numerics.Matrix4x4(
                inverseBindMatrix[0], inverseBindMatrix[1], inverseBindMatrix[2], inverseBindMatrix[3],
                inverseBindMatrix[4], inverseBindMatrix[5], inverseBindMatrix[6], inverseBindMatrix[7],
                inverseBindMatrix[8], inverseBindMatrix[9], inverseBindMatrix[10], inverseBindMatrix[11],
                inverseBindMatrix[12], inverseBindMatrix[13], inverseBindMatrix[14], inverseBindMatrix[15]);
            if (!System.Numerics.Matrix4x4.Invert(ibm, out var bind))
                bind = System.Numerics.Matrix4x4.Identity;
            //at the bind pose the mesh sits where its frame is, like a rigid mesh: bind * mesh frame world, as a child of
            //the root node (the joints of a skin need a common root), so relative to the root's transform
            var root = imported.RootFrame;
            bind *= FrameWorld(meshFrame);
            if (System.Numerics.Matrix4x4.Invert(FrameWorld(root), out var rootInverse))
                bind *= rootInverse;
            nodes.Add(new JObject
            {
                ["name"] = bonePath[(bonePath.LastIndexOf('/') + 1)..],
                ["matrix"] = new JArray(bind.M11, bind.M12, bind.M13, bind.M14, bind.M21, bind.M22, bind.M23, bind.M24,
                    bind.M31, bind.M32, bind.M33, bind.M34, bind.M41, bind.M42, bind.M43, bind.M44),
            });
            var rootNode = (JObject)nodes[nodeIndices[root]];
            var children = rootNode["children"] as JArray ?? new JArray();
            children.Add(nodes.Count - 1);
            rootNode["children"] = children;
            return nodes.Count - 1;
        }

        private void AddMorphTargets(ImportedMesh mesh, JObject gltfMesh, JArray primitives, int nodeIndex)
        {
            var morph = imported.MorphList?.FirstOrDefault(x => x.Path == mesh.Path);
            if (morph?.Channels == null || morph.Channels.Count == 0)
                return;
            var vertices = mesh.VertexList;
            var targets = new JArray();
            var names = new List<string>();
            foreach (var channel in morph.Channels)
            {
                //glTF has one shape per target: the full weight one (the last) of the in-between shapes
                var keyframe = channel.KeyframeList?.LastOrDefault();
                if (keyframe?.VertexList == null)
                    continue;
                var positions = new float[vertices.Count * 3];
                var normals = keyframe.hasNormals && mesh.hasNormal ? new float[vertices.Count * 3] : null;
                foreach (var morphVertex in keyframe.VertexList)
                {
                    var index = (int)morphVertex.Index;
                    if (index >= vertices.Count)
                        continue;
                    var delta = morphVertex.Vertex.Vertex - vertices[index].Vertex;
                    positions[index * 3] = delta.X * Scale;
                    positions[index * 3 + 1] = delta.Y * Scale;
                    positions[index * 3 + 2] = delta.Z * Scale;
                    if (normals != null)
                    {
                        //the normals of a shape are deltas too
                        var n = morphVertex.Vertex.Normal;
                        normals[index * 3] = n.X;
                        normals[index * 3 + 1] = n.Y;
                        normals[index * 3 + 2] = n.Z;
                    }
                }
                var target = new JObject { ["POSITION"] = AddAccessor(positions, 3, "VEC3", ARRAY_BUFFER, true) };
                if (normals != null)
                    target["NORMAL"] = AddAccessor(normals, 3, "VEC3", ARRAY_BUFFER);
                targets.Add(target);
                names.Add(channel.Name);
            }
            if (targets.Count == 0)
                return;
            foreach (JObject primitive in primitives)
            {
                primitive["targets"] = targets;
            }
            gltfMesh["weights"] = new JArray(names.Select(_ => 0f));
            gltfMesh["extras"] = new JObject { ["targetNames"] = new JArray(names) };
            morphMeshes[mesh.Path] = (nodeIndex, names);
        }

        #endregion

        #region Materials

        private int AddMaterial(string name)
        {
            if (string.IsNullOrEmpty(name))
                return -1;
            if (materialIndices.TryGetValue(name, out var index))
                return index;
            var material = ImportedHelpers.FindMaterial(name, imported.MaterialList);
            if (material == null)
                return -1;

            var diffuse = material.Diffuse;
            var pbr = new JObject
            {
                ["baseColorFactor"] = new JArray(Clamp01(diffuse.R), Clamp01(diffuse.G), Clamp01(diffuse.B), Clamp01(diffuse.A)),
                ["metallicFactor"] = 0f,
                ["roughnessFactor"] = 1f,
            };
            var gltfMaterial = new JObject { ["name"] = name, ["pbrMetallicRoughness"] = pbr };
            var emissive = material.Emissive;
            if (emissive.R > 0 || emissive.G > 0 || emissive.B > 0)
                gltfMaterial["emissiveFactor"] = new JArray(Clamp01(emissive.R), Clamp01(emissive.G), Clamp01(emissive.B));
            if (diffuse.A < 1f)
                gltfMaterial["alphaMode"] = "BLEND";

            foreach (var materialTexture in material.Textures ?? new List<ImportedMaterialTexture>())
            {
                //Dest: 0 diffuse, 1 normal (name), 2 specular, 3 bump map (see ModelConverter.ConvertMaterial)
                if (materialTexture.Dest == 0 && pbr["baseColorTexture"] == null)
                {
                    var texture = AddTexture(materialTexture);
                    if (texture != null)
                        pbr["baseColorTexture"] = texture;
                }
                else if ((materialTexture.Dest == 3 || materialTexture.Dest == 1) && gltfMaterial["normalTexture"] == null)
                {
                    var texture = AddTexture(materialTexture);
                    if (texture != null)
                        gltfMaterial["normalTexture"] = texture;
                }
            }

            materials.Add(gltfMaterial);
            index = materials.Count - 1;
            materialIndices[name] = index;
            return index;
        }

        private static float Clamp01(float value) => float.IsNaN(value) ? 0 : Math.Clamp(value, 0f, 1f);

        private JObject AddTexture(ImportedMaterialTexture materialTexture)
        {
            var texture = ImportedHelpers.FindTexture(materialTexture.Name, imported.TextureList);
            if (texture?.Data == null || texture.Data.Length == 0)
                return null;
            if (!textureIndices.TryGetValue(texture.Name, out var index))
            {
                var image = new JObject { ["name"] = Path.GetFileNameWithoutExtension(texture.Name) };
                var mimeType = MimeType(texture.Data);
                if (binary)
                {
                    image["bufferView"] = AddBufferView(texture.Data, null);
                    image["mimeType"] = mimeType ?? "image/png";
                }
                else
                {
                    var fileName = texture.Name;
                    if (writtenImages.Add(fileName))
                    {
                        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), fileName), texture.Data);
                    }
                    image["uri"] = Uri.EscapeDataString(fileName);
                }
                images.Add(image);
                if (samplers.Count == 0)
                {
                    samplers.Add(new JObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
                }
                textures.Add(new JObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
                index = textures.Count - 1;
                textureIndices[texture.Name] = index;
            }
            var info = new JObject { ["index"] = index };
            var scale = materialTexture.Scale;
            var offset = materialTexture.Offset;
            if (scale.X != 1 || scale.Y != 1 || offset.X != 0 || offset.Y != 0)
            {
                //Unity: uv * scale + offset with V up; glTF's V goes down
                extensionsUsed.Add("KHR_texture_transform");
                info["extensions"] = new JObject
                {
                    ["KHR_texture_transform"] = new JObject
                    {
                        ["offset"] = new JArray(offset.X, 1f - scale.Y - offset.Y),
                        ["scale"] = new JArray(scale.X, scale.Y),
                    }
                };
            }
            return info;
        }

        private static string MimeType(byte[] data)
        {
            if (data.Length > 4 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
                return "image/png";
            if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return "image/jpeg";
            if (data.Length > 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E')
                return "image/webp";
            return null;
        }

        #endregion

        #region Animations

        private void AddAnimation(ImportedKeyframedAnimation animation)
        {
            var channels = new JArray();
            var animationSamplers = new JArray();
            var inputs = new Dictionary<string, int>(); //shared time accessors

            int Input(float[] times)
            {
                var key = string.Join(",", times.Select(x => BitConverter.SingleToInt32Bits(x)));
                if (!inputs.TryGetValue(key, out var accessor))
                {
                    accessor = AddAccessor(times, 1, "SCALAR", null, true);
                    inputs[key] = accessor;
                }
                return accessor;
            }

            void AddChannel(int node, string pathName, float[] times, float[] values, int components, string type)
            {
                animationSamplers.Add(new JObject
                {
                    ["input"] = Input(times),
                    ["output"] = AddAccessor(values, components, type, null),
                    ["interpolation"] = "LINEAR",
                });
                channels.Add(new JObject
                {
                    ["sampler"] = animationSamplers.Count - 1,
                    ["target"] = new JObject { ["node"] = node, ["path"] = pathName },
                });
            }

            var blendShapeTracks = new Dictionary<string, List<ImportedBlendShape>>();
            foreach (var track in animation.TrackList ?? new List<ImportedAnimationKeyframedTrack>())
            {
                if (track.BlendShape != null)
                {
                    if (track.Path != null && options.exportBlendShape)
                    {
                        if (!blendShapeTracks.TryGetValue(track.Path, out var list))
                            blendShapeTracks[track.Path] = list = new List<ImportedBlendShape>();
                        list.Add(track.BlendShape);
                    }
                    continue;
                }
                var node = FindNode(track.Path);
                if (node < 0)
                    continue;
                var translations = Sorted(track.Translations);
                if (translations.Count > 0)
                {
                    AddChannel(node, "translation", translations.Select(x => x.time).ToArray(),
                        translations.SelectMany(x => new[] { x.value.X * Scale, x.value.Y * Scale, x.value.Z * Scale }).ToArray(), 3, "VEC3");
                }
                var rotations = Sorted(track.Rotations);
                if (rotations.Count > 0)
                {
                    //keep consecutive quaternions in the same hemisphere so the interpolation takes the short way
                    var values = new float[rotations.Count * 4];
                    var previous = new Quaternion(0, 0, 0, 1);
                    for (int i = 0; i < rotations.Count; i++)
                    {
                        var q = Quat(rotations[i].value).Select(x => (float)x).ToArray();
                        if (i > 0 && q[0] * previous.X + q[1] * previous.Y + q[2] * previous.Z + q[3] * previous.W < 0)
                        {
                            for (int k = 0; k < 4; k++)
                                q[k] = -q[k];
                        }
                        previous = new Quaternion(q[0], q[1], q[2], q[3]);
                        Array.Copy(q, 0, values, i * 4, 4);
                    }
                    AddChannel(node, "rotation", rotations.Select(x => x.time).ToArray(), values, 4, "VEC4");
                }
                var scalings = Sorted(track.Scalings);
                if (scalings.Count > 0)
                {
                    AddChannel(node, "scale", scalings.Select(x => x.time).ToArray(),
                        scalings.SelectMany(x => new[] { x.value.X, x.value.Y, x.value.Z }).ToArray(), 3, "VEC3");
                }
            }

            //weights: one value per target at every key time of the mesh, from the blend shape curves (0 to 100)
            foreach (var (meshPath, curves) in blendShapeTracks)
            {
                if (!morphMeshes.TryGetValue(meshPath, out var morph))
                    continue;
                var times = curves.SelectMany(x => x.Keyframes.Select(k => k.time)).Distinct().OrderBy(x => x).ToArray();
                if (times.Length == 0)
                    continue;
                var byName = curves.GroupBy(x => x.ChannelName).ToDictionary(x => x.Key, x => Sorted(x.SelectMany(c => c.Keyframes).ToList()));
                var values = new float[times.Length * morph.targetNames.Count];
                for (int t = 0; t < times.Length; t++)
                {
                    for (int target = 0; target < morph.targetNames.Count; target++)
                    {
                        if (byName.TryGetValue(morph.targetNames[target], out var keyframes))
                        {
                            values[t * morph.targetNames.Count + target] = Math.Clamp(Evaluate(keyframes, times[t]) / 100f, 0f, 1f);
                        }
                    }
                }
                AddChannel(morph.node, "weights", times, values, 1, "SCALAR");
            }

            if (channels.Count == 0)
                return;
            animations.Add(new JObject
            {
                ["name"] = animation.Name ?? $"Take{animations.Count}",
                ["channels"] = channels,
                ["samplers"] = animationSamplers,
            });
        }

        /// <summary>The keyframes by time, without duplicate times (glTF needs strictly increasing times).</summary>
        private static List<ImportedKeyframe<T>> Sorted<T>(List<ImportedKeyframe<T>> keyframes)
        {
            var result = new List<ImportedKeyframe<T>>(keyframes.Count);
            foreach (var keyframe in keyframes.OrderBy(x => x.time))
            {
                if (float.IsNaN(keyframe.time) || float.IsInfinity(keyframe.time))
                    continue;
                if (result.Count > 0 && keyframe.time <= result[^1].time)
                {
                    result[^1] = keyframe;
                    continue;
                }
                result.Add(keyframe);
            }
            return result;
        }

        private static float Evaluate(List<ImportedKeyframe<float>> keyframes, float time)
        {
            if (keyframes.Count == 0)
                return 0;
            if (time <= keyframes[0].time)
                return keyframes[0].value;
            for (int i = 1; i < keyframes.Count; i++)
            {
                if (time <= keyframes[i].time)
                {
                    var a = keyframes[i - 1];
                    var b = keyframes[i];
                    var f = (time - a.time) / Math.Max(b.time - a.time, 1e-6f);
                    return a.value + (b.value - a.value) * f;
                }
            }
            return keyframes[^1].value;
        }

        #endregion
    }
}
