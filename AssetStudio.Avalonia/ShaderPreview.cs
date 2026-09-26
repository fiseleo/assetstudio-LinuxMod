using System;
using System.Collections.Generic;
using System.Linq;
using SixLabors.ImageSharp;

namespace AssetStudio.Avalonia
{
    using Matrix4x4 = System.Numerics.Matrix4x4;
    using Vector2 = System.Numerics.Vector2;
    using Vector3 = System.Numerics.Vector3;
    using Vector4 = System.Numerics.Vector4;

    /// <summary>
    /// Draws a model (or a material on a sphere) with the game's own shaders: for each sub mesh the variant of its
    /// material's shader (see <see cref="UnityShaderVariant"/>), compiled from its Direct3D 11 programs for Vulkan.
    /// The <see cref="MeshRenderer"/> it belongs to keeps the camera and the (posed) vertices, in the mirrored space of the
    /// preview: they are mirrored back to Unity's space here.
    /// </summary>
    public sealed class ShaderPreview : IDisposable
    {
        private sealed class Part
        {
            public int First, Count;
            public Material Material;
            public UnityShaderVariant Variant;
            public ShadedDraw Draw;
            public Dictionary<string, (int, int)> TextureSizes = new Dictionary<string, (int, int)>();
        }

        private readonly List<Part> parts = new List<Part>();
        private readonly ShadedVertices vertices;
        private readonly int[] indices;
        private ShadedMesh gpuMesh;
        private bool failed;

        /// <summary>What the preview draws with (the variants), or why it can't.</summary>
        public string Summary { get; private set; }
        public bool IsUsable => parts.Any(x => x.Variant != null) && !failed;

        private ShaderPreview(ShadedVertices vertices, int[] indices)
        {
            this.vertices = vertices;
            this.indices = indices;
        }

        public static bool IsAvailable => VulkanMeshRenderer.Instance != null && Vkd3dShader.IsAvailable;

        /// <summary>For tools and tests: replaces the materials of the sub meshes of the model previews.</summary>
        public static Func<Material, Material> MaterialOverride { get; set; }

        /// <summary>The preview of a converted model: its sub meshes with their materials (null when none can be drawn).</summary>
        public static ShaderPreview FromModel(ModelConverter model)
        {
            var count = model.MeshList.Sum(x => x.VertexList.Count);
            var v = new ShadedVertices
            {
                Positions = new Vector3[count],
                Normals = new Vector3[count],
                Tangents = new Vector4[count],
                Colors = new Vector4[count],
                UV0 = new Vector4[count],
                UV1 = new Vector4[count],
                UV2 = new Vector2[count],
                UV3 = new Vector2[count],
            };
            var indexList = new List<int>();
            var parts = new List<Part>();
            var offset = 0;
            foreach (var mesh in model.MeshList)
            {
                for (int i = 0; i < mesh.VertexList.Count; i++)
                {
                    var vertex = mesh.VertexList[i];
                    var t = vertex.Tangent;
                    //the converter mirrors X: back to Unity's space
                    v.Tangents[offset + i] = mesh.hasTangent ? new Vector4(-t.X, t.Y, t.Z, t.W) : new Vector4(1, 0, 0, 1);
                    v.Colors[offset + i] = mesh.hasColor ? new Vector4(vertex.Color.R, vertex.Color.G, vertex.Color.B, vertex.Color.A) : Vector4.One;
                    float[] Uv(int set) => set < (mesh.hasUV?.Length ?? 0) && mesh.hasUV[set] ? vertex.UV?[set] : null;
                    if (Uv(0) is { } uv0) v.UV0[offset + i] = new Vector4(uv0[0], uv0[1], 0, 0);
                    v.UV1[offset + i] = Uv(1) is { } uv1 ? new Vector4(uv1[0], uv1[1], 0, 0) : v.UV0[offset + i];
                    if (Uv(2) is { } uv2) v.UV2[offset + i] = new Vector2(uv2[0], uv2[1]);
                    if (Uv(3) is { } uv3) v.UV3[offset + i] = new Vector2(uv3[0], uv3[1]);
                }
                foreach (var submesh in mesh.SubmeshList)
                {
                    var first = indexList.Count;
                    foreach (var face in submesh.FaceList)
                    {
                        //mirrored back, the triangles turn back too
                        indexList.Add(face.VertexIndices[2] + submesh.BaseVertex + offset);
                        indexList.Add(face.VertexIndices[1] + submesh.BaseVertex + offset);
                        indexList.Add(face.VertexIndices[0] + submesh.BaseVertex + offset);
                    }
                    model.SourceMaterials.TryGetValue(submesh.Material ?? "", out var material);
                    if (MaterialOverride != null)
                        material = MaterialOverride(material);
                    parts.Add(new Part { First = first, Count = indexList.Count - first, Material = material });
                }
                offset += mesh.VertexList.Count;
            }
            var result = new ShaderPreview(v, indexList.ToArray());
            result.parts.AddRange(parts);
            result.SelectVariants();
            return result;
        }

        /// <summary>A material on a sphere (the mesh the preview shows is <paramref name="sphere"/>).</summary>
        public static ShaderPreview FromMaterial(Material material, out MeshRenderer sphere)
        {
            const int rings = 32, segments = 48;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var uvs = new List<Vector4>();
            for (int r = 0; r <= rings; r++)
            {
                var theta = MathF.PI * r / rings; //0 at the top
                for (int s = 0; s <= segments; s++)
                {
                    var phi = 2 * MathF.PI * s / segments;
                    var n = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                    positions.Add(n * 0.5f);
                    normals.Add(n);
                    //direction of increasing u (phi), Unity: binormal = cross(normal, tangent) * w points to increasing v (up)
                    var tangent = Vector3.Normalize(new Vector3(-MathF.Sin(phi), 0, MathF.Cos(phi)));
                    var bitangent = Vector3.Cross(n, tangent);
                    tangents.Add(new Vector4(tangent, Vector3.Dot(bitangent, Vector3.UnitY) >= 0 || r == 0 || r == rings ? 1 : -1));
                    uvs.Add(new Vector4(1 - s / (float)segments, 1 - r / (float)rings, 0, 0));
                }
            }
            var indexList = new List<int>();
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * (segments + 1) + s, b = a + segments + 1;
                    //clockwise seen from outside in Unity's left-handed space
                    indexList.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
                }
            }
            var v = new ShadedVertices
            {
                Positions = positions.ToArray(),
                Normals = normals.ToArray(),
                Tangents = tangents.ToArray(),
                UV0 = uvs.ToArray(),
                UV1 = uvs.ToArray(),
            };
            //the preview mesh: mirrored like the converted models, triangles turned
            var mirrored = positions.Select(p => new Vector3(-p.X, p.Y, p.Z)).ToArray();
            var mirroredNormals = normals.Select(n => new Vector3(-n.X, n.Y, n.Z)).ToArray();
            var turned = new int[indexList.Count];
            for (int i = 0; i < turned.Length; i += 3)
            {
                turned[i] = indexList[i + 2];
                turned[i + 1] = indexList[i + 1];
                turned[i + 2] = indexList[i];
            }
            sphere = new MeshRenderer(mirrored, mirroredNormals, turned);
            var preview = new ShaderPreview(v, indexList.ToArray());
            preview.parts.Add(new Part { First = 0, Count = indexList.Count, Material = material });
            preview.SelectVariants();
            sphere.ShaderPreview = preview;
            return preview;
        }

        private void SelectVariants()
        {
            var lines = new List<string>();
            foreach (var group in parts.GroupBy(x => x.Material))
            {
                var material = group.Key;
                if (material == null || !material.m_Shader.TryGet(out var shader))
                {
                    lines.Add($"{material?.m_Name ?? "(no material)"}: no shader");
                    continue;
                }
                UnityShaderVariant variant = null;
                string reason;
                try
                {
                    variant = UnityShaderVariant.Select(shader, material.m_ShaderKeywords, material, out reason);
                }
                catch (Exception e)
                {
                    reason = e.Message;
                }
                foreach (var part in group)
                    part.Variant = variant;
                var name = shader.m_ParsedForm?.m_Name ?? shader.m_Name;
                lines.Add(variant == null
                    ? $"{material.m_Name} ({name}): {reason}"
                    : $"{material.m_Name}: {name}, pass {variant.PassName ?? ""} {variant.LightMode}".TrimEnd() + (variant.Keywords.Length > 0 ? $" [{string.Join(" ", variant.Keywords)}]" : ""));
            }
            Summary = string.Join("\n", lines);
        }

        private PreviewTexture LoadTexture(Part part, UnityShaderTexture parameter)
        {
            if (part.Material == null || parameter.Name == null)
                return null;
            foreach (var (name, texEnv) in part.Material.m_SavedProperties.m_TexEnvs)
            {
                if (name != parameter.Name || !texEnv.m_Texture.TryGet<Texture2D>(out var texture) || texture is Cubemap)
                    continue;
                try
                {
                    //Unity's rows go up from v = 0: the image as stored, not flipped
                    using var image = texture.ConvertToImage(false);
                    if (image == null)
                        return null;
                    part.TextureSizes[name] = (image.Width, image.Height);
                    const int maxSize = 2048;
                    if (image.Width > maxSize || image.Height > maxSize)
                    {
                        var scale = (float)maxSize / Math.Max(image.Width, image.Height);
                        SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(image, x => SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(x,
                            Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))));
                    }
                    var pixels = new byte[image.Width * image.Height * 4];
                    image.CopyPixelDataTo(pixels);
                    return new PreviewTexture(pixels, image.Width, image.Height);
                }
                catch (Exception e)
                {
                    Logger.Warning($"Shader preview: unable to decode {texture.m_Name}: {e.Message}");
                    return null;
                }
            }
            return null;
        }

        private static string DefaultTexture(Part part, UnityShaderTexture parameter)
        {
            if (part.Material != null && part.Material.m_Shader.TryGet(out var shader))
            {
                var property = shader.m_ParsedForm?.m_PropInfo?.m_Props?.FirstOrDefault(x => x.m_Name == parameter.Name);
                if (property?.m_DefTexture != null)
                    return property.m_DefTexture.m_DefaultName;
            }
            return parameter.Name?.StartsWith("unity_", StringComparison.Ordinal) == true ? "" : "white";
        }

        /// <summary>Renders with the camera and the posed vertices of the preview; null when it fails (the caller falls back).</summary>
        public byte[] Render(VulkanMeshRenderer gpu, MeshRenderer view, int width, int height)
        {
            if (failed)
                return null;
            try
            {
                //posed positions and normals, mirrored back
                var positions = view.PosedVertices;
                var normals = view.PosedNormals;
                for (int i = 0; i < vertices.Positions.Length && i < positions.Length; i++)
                {
                    vertices.Positions[i] = new Vector3(-positions[i].X, positions[i].Y, positions[i].Z);
                    if (normals != null && i < normals.Length)
                        vertices.Normals[i] = new Vector3(-normals[i].X, normals[i].Y, normals[i].Z);
                }
                if (gpuMesh == null)
                {
                    gpuMesh = gpu.UploadShaded(vertices, indices);
                    foreach (var part in parts.Where(x => x.Variant != null && x.Count > 0))
                    {
                        try
                        {
                            part.Draw = gpu.CreateShadedDraw(part.Variant, part.First, part.Count, p => LoadTexture(part, p), p => DefaultTexture(part, p));
                        }
                        catch (Exception e)
                        {
                            Logger.Warning($"Shader preview: {part.Material?.m_Name}: {e.Message}");
                            Summary += $"\n{part.Material?.m_Name}: {e.Message}";
                        }
                    }
                    if (parts.All(x => x.Draw == null))
                    {
                        failed = true;
                        return null;
                    }
                }
                else if (view.VerticesChangedForShaders)
                {
                    gpu.UpdateShadedPositions(gpuMesh, vertices.Positions, vertices.Normals);
                }
                view.VerticesChangedForShaders = false;

                //the preview's orbit camera, in Unity's space
                var (center, radius) = view.Bounds;
                var rotation = Matrix4x4.CreateRotationY(view.Yaw) * Matrix4x4.CreateRotationX(view.Pitch);
                var inverse = Matrix4x4.Transpose(rotation);
                var toCamera = Vector3.TransformNormal(Vector3.UnitZ, inverse);
                var up = Vector3.TransformNormal(Vector3.UnitY, inverse);
                var right = Vector3.TransformNormal(Vector3.UnitX, inverse);
                var pixelsPerUnit = Math.Min(width, height) * 0.45f * view.Zoom / radius;
                var halfHeight = height / 2f / pixelsPerUnit;
                const float fieldOfView = 30f;
                var distance = halfHeight / MathF.Tan(fieldOfView * MathF.PI / 360f);
                var target = center - (right * view.Pan.X + up * view.Pan.Y) * radius;
                var position = target + toCamera * distance;
                Vector3 Unmirror(Vector3 x) => new Vector3(-x.X, x.Y, x.Z);

                foreach (var part in parts.Where(x => x.Draw != null))
                {
                    var values = UnityShaderValues.CreateDefaults(false);
                    values.SetCamera(Matrix4x4.Identity, Unmirror(position), Unmirror(target), Unmirror(up), width, height, fieldOfView);
                    values.SetLightFromCamera(Unmirror(position), Unmirror(target), Unmirror(up));
                    values.SetMaterial(part.Material, name => part.TextureSizes.TryGetValue(name, out var size) ? size : null, false);
                    gpu.UpdateShadedDraw(part.Draw, values);
                }
                return gpu.RenderShaded(gpuMesh, parts.Where(x => x.Draw != null).Select(x => x.Draw).ToList(), width, height);
            }
            catch (Exception e)
            {
                Logger.Warning($"Shader preview failed: {e.Message}");
                Summary += $"\nfailed: {e.Message}";
                failed = true;
                return null;
            }
        }

        public void Dispose()
        {
            foreach (var part in parts)
            {
                part.Draw?.Dispose();
                part.Draw = null;
            }
            gpuMesh?.Dispose();
            gpuMesh = null;
        }
    }
}
