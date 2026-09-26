using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia
{
    using Matrix4x4 = System.Numerics.Matrix4x4;
    using Vector2 = System.Numerics.Vector2;
    using Vector3 = System.Numerics.Vector3;

    /// <summary>A decoded texture for the preview: tightly packed BGRA, top row first.</summary>
    public sealed record PreviewTexture(byte[] Bgra, int Width, int Height);

    /// <summary>A range of the index list (whole triangles) drawn with one texture (-1 = untextured).</summary>
    public readonly record struct DrawRange(int Start, int Count, int Texture);

    /// <summary>
    /// Mesh / model preview (replaces the OpenTK GLControl of the Windows GUI). Renders on the GPU with Vulkan
    /// (<see cref="VulkanMeshRenderer"/>) when available, otherwise with a small software rasterizer
    /// (flat shaded, z-buffered, optional wireframe overlay).
    /// </summary>
    public sealed class MeshRenderer : IDisposable
    {
        private readonly Vector3[] vertices;
        private readonly Vector3[] normals;
        private readonly int[] indices;
        private readonly Vector2[] uvs;
        private readonly DrawRange[] ranges;
        private readonly PreviewTexture[] textures;
        private VulkanMesh gpuMesh;
        private bool gpuVerticesChanged;
        private readonly Vector3 center;
        private readonly float radius;

        public float Yaw = -MathF.PI / 4;
        public float Pitch = MathF.PI / 6;
        public float Zoom = 1f;
        public Vector2 Pan;
        /// <summary>0 = shaded, 1 = shaded + wireframe, 2 = wireframe only</summary>
        public int WireframeMode;

        public int VertexCount => vertices.Length;
        public int TriangleCount => indices.Length / 3;
        public int TextureCount => textures.Length;

        /// <summary>Draws with the game's shaders instead (see <see cref="UseShaders"/>); null when not built.</summary>
        public ShaderPreview ShaderPreview { get; set; }
        /// <summary>Render with <see cref="ShaderPreview"/> when it can.</summary>
        public bool UseShaders { get; set; }
        internal Vector3[] PosedVertices => vertices;
        internal Vector3[] PosedNormals => normals;
        internal (Vector3 center, float radius) Bounds => (center, radius);
        internal bool VerticesChangedForShaders;
        /// <summary>The converted model of a model preview (its materials), null for a mesh.</summary>
        public ModelConverter Model { get; private set; }

        /// <summary>Poses the model (skinning, animations); null for a single mesh.</summary>
        public ModelAnimator Animator { get; private set; }
        public int Clip { get; private set; } = -1;
        public float Time { get; private set; }

        /// <summary>The blend shapes of the model or mesh, null when there are none.</summary>
        public BlendShapeSet BlendShapes => Animator?.BlendShapes ?? meshBlendShapes;
        private BlendShapeSet meshBlendShapes;
        private float[] meshBlendShapeWeights;
        private Vector3[] baseVertices;
        private Vector3[] baseNormals;

        /// <summary>The blend shape weights of the current pose (a clip's curves override the user weights).</summary>
        public float[] BlendShapeWeights => Animator?.PosedBlendShapeWeights ?? meshBlendShapeWeights;

        /// <summary>Sets the weight (0 to 100) of a blend shape and updates the vertices.</summary>
        public void SetBlendShapeWeight(int channel, float weight)
        {
            if (Animator?.BlendShapes != null)
            {
                Animator.BlendShapeWeights[channel] = weight;
                SetPose(Clip, Time);
                return;
            }
            if (meshBlendShapes == null)
                return;
            meshBlendShapeWeights[channel] = weight;
            if (!meshBlendShapes.Apply(baseVertices, baseNormals, meshBlendShapeWeights, vertices, normals))
            {
                Array.Copy(baseVertices, vertices, vertices.Length);
                if (normals != null)
                    Array.Copy(baseNormals, normals, normals.Length);
            }
            if (normals != null)
            {
                for (int i = 0; i < normals.Length; i++)
                {
                    var length = normals[i].Length();
                    if (length > 0)
                        normals[i] /= length;
                }
            }
            gpuVerticesChanged = true;
            VerticesChangedForShaders = true;
        }

        /// <summary>Places the vertices for a clip of <see cref="Animator"/> at a time (clip -1: rest pose).</summary>
        public void SetPose(int clip, float time)
        {
            if (Animator == null)
                return;
            Clip = clip;
            Time = time;
            Animator.Pose(clip, time, vertices, normals);
            gpuVerticesChanged = true;
            VerticesChangedForShaders = true;
        }

        /// <summary>The bones of the current pose as lines in pixels of a render of this size.</summary>
        public IEnumerable<(Vector2 child, Vector2 parent)> SkeletonLines(int width, int height)
        {
            if (Animator == null)
                yield break;
            var rotation = Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateRotationX(Pitch);
            var scale = Math.Min(width, height) * 0.45f * Zoom / radius;
            Vector2 Project(Vector3 v)
            {
                var p = Vector3.Transform(v - center, rotation);
                return new Vector2(width / 2f + (p.X + Pan.X * radius) * scale, height / 2f - (p.Y + Pan.Y * radius) * scale);
            }
            foreach (var (child, parent) in Animator.SkeletonLines())
                yield return (Project(child), Project(parent));
        }

        /// <summary>"Vulkan (device)" or "Software", for the status bar.</summary>
        public string BackendName => VulkanMeshRenderer.Instance is { } gpu ? $"Vulkan ({gpu.DeviceName})" : "Software";

        public MeshRenderer(Vector3[] vertices, Vector3[] normals, int[] indices,
            Vector2[] uvs = null, DrawRange[] ranges = null, PreviewTexture[] textures = null)
        {
            this.vertices = vertices;
            this.normals = normals;
            this.indices = indices;
            this.textures = uvs != null && textures != null ? textures : Array.Empty<PreviewTexture>();
            this.uvs = this.textures.Length > 0 ? uvs : null;
            this.ranges = this.textures.Length > 0 && ranges != null ? ranges : new[] { new DrawRange(0, indices.Length / 3 * 3, -1) };
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in vertices)
            {
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
                    continue;
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            center = (min + max) / 2;
            radius = Math.Max(1e-5f, (max - min).Length() / 2);
        }

        public static MeshRenderer FromMesh(Mesh m_Mesh)
        {
            if (m_Mesh.m_VertexCount <= 0 || m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0 || m_Mesh.m_Indices == null)
                return null;
            var c = m_Mesh.m_Vertices.Length == m_Mesh.m_VertexCount * 4 ? 4 : 3;
            var verts = new Vector3[m_Mesh.m_VertexCount];
            for (int v = 0; v < verts.Length && v * c + 2 < m_Mesh.m_Vertices.Length; v++)
            {
                // Unity is left handed, mirror X like the OBJ exporter does
                verts[v] = new Vector3(-m_Mesh.m_Vertices[v * c], m_Mesh.m_Vertices[v * c + 1], m_Mesh.m_Vertices[v * c + 2]);
            }
            Vector3[] norms = null;
            var n = m_Mesh.m_Normals;
            if (n != null && (n.Length == verts.Length * 3 || n.Length == verts.Length * 4))
            {
                var nc = n.Length / verts.Length;
                norms = new Vector3[verts.Length];
                for (int v = 0; v < norms.Length; v++)
                {
                    norms[v] = new Vector3(-n[v * nc], n[v * nc + 1], n[v * nc + 2]);
                }
            }
            var idx = new int[m_Mesh.m_Indices.Count / 3 * 3];
            for (int i = 0; i < idx.Length; i++)
            {
                idx[i] = (int)m_Mesh.m_Indices[i];
            }
            var renderer = new MeshRenderer(verts, norms, idx);
            renderer.meshBlendShapes = BlendShapeSet.FromMesh(m_Mesh);
            if (renderer.meshBlendShapes != null)
            {
                renderer.meshBlendShapeWeights = new float[renderer.meshBlendShapes.Count];
                renderer.baseVertices = (Vector3[])verts.Clone();
                renderer.baseNormals = (Vector3[])norms?.Clone();
            }
            return renderer;
        }

        /// <param name="maxTextureSize">textures are scaled down to this size (preview memory)</param>
        public static MeshRenderer FromModel(ModelConverter model, int maxTextureSize = 2048)
        {
            if (model.MeshList.Count == 0)
                return null;
            var vertexCount = 0;
            foreach (var mesh in model.MeshList)
                vertexCount += mesh.VertexList.Count;
            var verts = new Vector3[vertexCount];
            // meshes without normals keep zero normals, the GPU shader falls back to flat shading for those
            var norms = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var uvDone = new bool[vertexCount];
            var hasNormals = false;
            var indices = new System.Collections.Generic.List<int>();
            var ranges = new System.Collections.Generic.List<DrawRange>();
            var textures = new System.Collections.Generic.List<PreviewTexture>();
            var textureIndex = new System.Collections.Generic.Dictionary<string, int>();
            int offset = 0;
            foreach (var mesh in model.MeshList)
            {
                // the UV set mapped to the diffuse map (Export options > UVs), else the first one
                var uvSet = -1;
                for (int i = 0; i < mesh.hasUV.Length; i++)
                {
                    if (mesh.hasUV[i] && (uvSet < 0 || (mesh.uvType[i] == 0 && mesh.uvType[uvSet] != 0)))
                        uvSet = i;
                }
                for (int i = 0; i < mesh.VertexList.Count; i++)
                {
                    var vertex = mesh.VertexList[i];
                    var p = vertex.Vertex;
                    verts[offset + i] = new Vector3(p.X, p.Y, p.Z);
                    if (mesh.hasNormal)
                    {
                        var n = vertex.Normal;
                        norms[offset + i] = new Vector3(n.X, n.Y, n.Z);
                    }
                    if (uvSet >= 0 && vertex.UV?[uvSet] is { Length: >= 2 } uv)
                    {
                        uvs[offset + i] = new Vector2(uv[0], uv[1]);
                    }
                }
                foreach (var submesh in mesh.SubmeshList)
                {
                    var start = indices.Count;
                    var (texture, uvScale, uvOffset) = uvSet >= 0 ? FindMainTexture(model, submesh.Material, textures, textureIndex, maxTextureSize) : (-1, Vector2.One, Vector2.Zero);
                    foreach (var face in submesh.FaceList)
                    {
                        foreach (var index in face.VertexIndices)
                        {
                            var vertex = submesh.BaseVertex + index + offset;
                            indices.Add(vertex);
                            // material tiling / offset (vertices are rarely shared between materials)
                            if (texture >= 0 && (uint)vertex < (uint)vertexCount && !uvDone[vertex])
                            {
                                uvDone[vertex] = true;
                                uvs[vertex] = uvs[vertex] * uvScale + uvOffset;
                            }
                        }
                    }
                    var count = (indices.Count - start) / 3 * 3;
                    if (count > 0)
                        ranges.Add(new DrawRange(start, count, texture));
                }
                hasNormals |= mesh.hasNormal;
                offset += mesh.VertexList.Count;
            }
            // the vertices are in the space of their mesh: place them with the frame hierarchy (and bones) at rest
            ModelAnimator animator = null;
            if (model.RootFrame != null)
            {
                animator = new ModelAnimator(model, verts, hasNormals ? norms : null);
                animator.Pose(-1, 0, verts, hasNormals ? norms : null);
            }
            return new MeshRenderer(verts, hasNormals ? norms : null, indices.ToArray(), uvs, ranges.ToArray(), textures.ToArray()) { Animator = animator, Model = model };
        }

        private static (int, Vector2, Vector2) FindMainTexture(ModelConverter model, string materialName,
            System.Collections.Generic.List<PreviewTexture> textures, System.Collections.Generic.Dictionary<string, int> textureIndex, int maxTextureSize)
        {
            var material = materialName == null ? null : ImportedHelpers.FindMaterial(materialName, model.MaterialList);
            var main = material?.Textures?.Find(x => x.Dest == 0);
            if (main?.Name == null)
                return (-1, Vector2.One, Vector2.Zero);
            if (!textureIndex.TryGetValue(main.Name, out var index))
            {
                index = -1;
                var data = ImportedHelpers.FindTexture(main.Name, model.TextureList)?.Data;
                if (data != null)
                {
                    try
                    {
                        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(data);
                        if (image.Width > maxTextureSize || image.Height > maxTextureSize)
                        {
                            var scale = (float)maxTextureSize / Math.Max(image.Width, image.Height);
                            SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(image, x => SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(x,
                                Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))));
                        }
                        var pixels = new byte[image.Width * image.Height * 4];
                        image.CopyPixelDataTo(pixels);
                        index = textures.Count;
                        textures.Add(new PreviewTexture(pixels, image.Width, image.Height));
                    }
                    catch (Exception e)
                    {
                        Logger.Warning($"Model preview: unable to decode texture {main.Name}: {e.Message}");
                    }
                }
                textureIndex[main.Name] = index;
            }
            var uvScale = main.Scale.X == 0 && main.Scale.Y == 0 ? Vector2.One : new Vector2(main.Scale.X, main.Scale.Y);
            return (index, uvScale, new Vector2(main.Offset.X, main.Offset.Y));
        }

        /// <summary>Renders into a BGRA (premultiplied, opaque) buffer.</summary>
        public byte[] Render(int width, int height)
        {
            var gpu = VulkanMeshRenderer.Instance;
            if (gpu != null && UseShaders && ShaderPreview?.IsUsable == true && ShaderPreview.IsPrepared && WireframeMode == 0)
            {
                var shaded = ShaderPreview.Render(gpu, this, width, height);
                if (shaded != null)
                    return shaded;
            }
            if (gpu != null)
            {
                try
                {
                    if (gpuMesh != null && gpuVerticesChanged)
                    {
                        gpu.UpdateVertices(gpuMesh, vertices, normals);
                    }
                    gpuVerticesChanged = false;
                    gpuMesh ??= gpu.Upload(vertices, normals, uvs, indices, ranges, textures);
                    var (mvp, view) = Camera(width, height);
                    return gpu.Render(gpuMesh, width, height, mvp, view, WireframeMode);
                }
                catch (Exception e)
                {
                    Logger.Warning($"Vulkan rendering failed, switching to the software renderer: {e.Message}");
                    gpuMesh = null;
                    VulkanMeshRenderer.Disable();
                }
            }
            return RenderSoftware(width, height);
        }

        /// <summary>
        /// Same orthographic camera as the software renderer: returns model -> clip (Vulkan: y down, depth 0..1,
        /// larger view z is closer) and model -> view.
        /// </summary>
        private (Matrix4x4 mvp, Matrix4x4 view) Camera(int width, int height)
        {
            var view = Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateRotationX(Pitch);
            var scale = Math.Min(width, height) * 0.45f * Zoom / radius;
            var projection = Matrix4x4.CreateTranslation(Pan.X * radius, Pan.Y * radius, 0)
                * Matrix4x4.CreateScale(2 * scale / width, -2 * scale / height, -1 / (2.02f * radius))
                * Matrix4x4.CreateTranslation(0, 0, 0.5f);
            return (view * projection, view);
        }

        public void Dispose()
        {
            ShaderPreview?.Dispose();
            ShaderPreview = null;
            gpuMesh?.Dispose();
            gpuMesh = null;
        }

        private byte[] RenderSoftware(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            var depth = new float[width * height];
            Array.Fill(depth, float.MaxValue);

            // background gradient
            for (int y = 0; y < height; y++)
            {
                var t = (float)y / Math.Max(1, height - 1);
                var g = (byte)(70 - 30 * t);
                var b = (byte)(80 - 30 * t);
                for (int x = 0; x < width; x++)
                {
                    var o = (y * width + x) * 4;
                    pixels[o] = b; pixels[o + 1] = g; pixels[o + 2] = g; pixels[o + 3] = 255;
                }
            }

            var rotation = Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateRotationX(Pitch);
            var scale = Math.Min(width, height) * 0.45f * Zoom / radius;
            var projected = new Vector3[vertices.Length];
            Parallel.For(0, vertices.Length, i =>
            {
                var p = Vector3.Transform(vertices[i] - center, rotation);
                projected[i] = new Vector3(width / 2f + (p.X + Pan.X * radius) * scale, height / 2f - (p.Y + Pan.Y * radius) * scale, -p.Z);
            });

            var light = Vector3.Normalize(new Vector3(0.3f, 0.5f, 1f));
            if (WireframeMode != 2)
            {
                foreach (var range in ranges)
                {
                    var texture = range.Texture >= 0 ? textures[range.Texture] : null;
                    for (int t = range.Start; t + 2 < range.Start + range.Count; t += 3)
                    {
                        int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                        if ((uint)i0 >= projected.Length || (uint)i1 >= projected.Length || (uint)i2 >= projected.Length)
                            continue;
                        var w0 = Vector3.Transform(vertices[i0] - center, rotation);
                        var w1 = Vector3.Transform(vertices[i1] - center, rotation);
                        var w2 = Vector3.Transform(vertices[i2] - center, rotation);
                        var n = Vector3.Cross(w1 - w0, w2 - w0);
                        var len = n.Length();
                        if (len <= 0 || !float.IsFinite(len))
                            continue;
                        n /= len;
                        var intensity = 0.4f + 0.6f * MathF.Abs(Vector3.Dot(n, light));
                        if (texture != null)
                        {
                            FillTriangle(pixels, depth, width, height, projected[i0], projected[i1], projected[i2], 0, 0, 0,
                                texture, uvs[i0], uvs[i1], uvs[i2], intensity);
                            continue;
                        }
                        var shade = (byte)Math.Clamp(intensity * 235, 0, 255);
                        FillTriangle(pixels, depth, width, height, projected[i0], projected[i1], projected[i2],
                            (byte)(shade * 0.95f), (byte)(shade * 0.92f), shade);
                    }
                }
            }
            if (WireframeMode != 0)
            {
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                    if ((uint)i0 >= projected.Length || (uint)i1 >= projected.Length || (uint)i2 >= projected.Length)
                        continue;
                    DrawLine(pixels, width, height, projected[i0], projected[i1]);
                    DrawLine(pixels, width, height, projected[i1], projected[i2]);
                    DrawLine(pixels, width, height, projected[i2], projected[i0]);
                }
            }
            return pixels;
        }

        /// <summary>Flat colored, or textured (nearest texel, shaded by <paramref name="intensity"/>) when <paramref name="texture"/> is set.</summary>
        private static void FillTriangle(byte[] pixels, float[] depth, int width, int height, Vector3 a, Vector3 b, Vector3 c, byte blue, byte green, byte red,
            PreviewTexture texture = null, Vector2 uvA = default, Vector2 uvB = default, Vector2 uvC = default, float intensity = 1)
        {
            var minX = (int)MathF.Max(0, MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            var maxX = (int)MathF.Min(width - 1, MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            var minY = (int)MathF.Max(0, MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            var maxY = (int)MathF.Min(height - 1, MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            if (minX > maxX || minY > maxY)
                return;
            var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-8f || !float.IsFinite(area))
                return;
            var inv = 1f / area;
            for (int y = minY; y <= maxY; y++)
            {
                var py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    var px = x + 0.5f;
                    var w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) * inv;
                    var w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) * inv;
                    var w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0)
                        continue;
                    var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                    var i = y * width + x;
                    if (z >= depth[i])
                        continue;
                    depth[i] = z;
                    var o = i * 4;
                    if (texture != null)
                    {
                        // orthographic projection: affine interpolation is exact; repeat wrapping, V flipped (image rows go down)
                        var uv = uvA * w0 + uvB * w1 + uvC * w2;
                        var tu = uv.X - MathF.Floor(uv.X);
                        var tv = 1 - (uv.Y - MathF.Floor(uv.Y));
                        var tx = Math.Clamp((int)(tu * texture.Width), 0, texture.Width - 1);
                        var ty = Math.Clamp((int)(tv * texture.Height), 0, texture.Height - 1);
                        var s = (ty * texture.Width + tx) * 4;
                        pixels[o] = (byte)Math.Min(255, texture.Bgra[s] * intensity);
                        pixels[o + 1] = (byte)Math.Min(255, texture.Bgra[s + 1] * intensity);
                        pixels[o + 2] = (byte)Math.Min(255, texture.Bgra[s + 2] * intensity);
                        pixels[o + 3] = 255;
                        continue;
                    }
                    pixels[o] = blue; pixels[o + 1] = green; pixels[o + 2] = red; pixels[o + 3] = 255;
                }
            }
        }

        private static void DrawLine(byte[] pixels, int width, int height, Vector3 p0, Vector3 p1)
        {
            if (!float.IsFinite(p0.X + p0.Y + p1.X + p1.Y))
                return;
            var dx = p1.X - p0.X;
            var dy = p1.Y - p0.Y;
            var steps = (int)MathF.Min(4096, MathF.Max(MathF.Abs(dx), MathF.Abs(dy)));
            if (steps <= 0)
                steps = 1;
            for (int s = 0; s <= steps; s++)
            {
                var x = (int)(p0.X + dx * s / steps);
                var y = (int)(p0.Y + dy * s / steps);
                if ((uint)x >= width || (uint)y >= height)
                    continue;
                var o = (y * width + x) * 4;
                pixels[o] = 40; pixels[o + 1] = 40; pixels[o + 2] = 40; pixels[o + 3] = 255;
            }
        }
    }
}
