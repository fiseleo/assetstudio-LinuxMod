using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio.Avalonia
{
    using Vector3 = System.Numerics.Vector3;

    /// <summary>
    /// The blend shapes of the meshes of a preview: position (and normal) offsets of the vertices, weighted 0 to 100 like
    /// Unity. A channel with several frames (in-between shapes) blends between the two frames around its weight.
    /// Vertex indices are those of the preview (the meshes one after the other).
    /// </summary>
    public sealed class BlendShapeSet
    {
        public sealed class Frame
        {
            public float Weight;
            public int[] Vertices;
            public Vector3[] Positions;
            public Vector3[] Normals; //null: the shape doesn't change the normals
        }

        public sealed class Channel
        {
            public string MeshPath;
            public string Name;
            public Frame[] Frames; //by weight
        }

        public List<Channel> Channels { get; } = new List<Channel>();

        public int Count => Channels.Count;

        /// <summary>The channel of a mesh by name (the blend shape curves of the clips), -1 when unknown.</summary>
        public int Find(string meshPath, string name)
        {
            var index = Channels.FindIndex(x => x.MeshPath == meshPath && x.Name == name);
            return index >= 0 ? index : Channels.FindIndex(x => x.Name == name);
        }

        /// <summary>The blend shapes of the meshes of a converted model (<see cref="ImportedMorph"/>, in the mirrored space).</summary>
        public static BlendShapeSet FromModel(ModelConverter model)
        {
            var set = new BlendShapeSet();
            var offset = 0;
            foreach (var mesh in model.MeshList)
            {
                var morph = model.MorphList?.FirstOrDefault(x => x.Path == mesh.Path);
                foreach (var channel in morph?.Channels ?? new List<ImportedMorphChannel>())
                {
                    var frames = new List<Frame>();
                    foreach (var keyframe in channel.KeyframeList ?? new List<ImportedMorphKeyframe>())
                    {
                        var vertices = keyframe.VertexList.Where(x => x.Index < mesh.VertexList.Count).ToList();
                        frames.Add(new Frame
                        {
                            Weight = keyframe.Weight,
                            Vertices = vertices.Select(x => offset + (int)x.Index).ToArray(),
                            //the converter stores the shaped position: the offset is from the vertex
                            Positions = vertices.Select(x => ToNumerics(x.Vertex.Vertex - mesh.VertexList[(int)x.Index].Vertex)).ToArray(),
                            Normals = keyframe.hasNormals ? vertices.Select(x => ToNumerics(x.Vertex.Normal)).ToArray() : null,
                        });
                    }
                    if (frames.Count > 0)
                        set.Channels.Add(new Channel { MeshPath = mesh.Path, Name = channel.Name, Frames = frames.OrderBy(x => x.Weight).ToArray() });
                }
                offset += mesh.VertexList.Count;
            }
            return set.Count > 0 ? set : null;
        }

        /// <summary>The blend shapes of a mesh asset (Unity space: X mirrored like the mesh preview).</summary>
        public static BlendShapeSet FromMesh(Mesh mesh)
        {
            var shapes = mesh.m_Shapes;
            if (shapes?.channels == null || shapes.channels.Count == 0)
                return null;
            var set = new BlendShapeSet();
            foreach (var channel in shapes.channels)
            {
                var frames = new List<Frame>();
                for (int f = channel.frameIndex; f < channel.frameIndex + channel.frameCount && f < shapes.shapes.Count; f++)
                {
                    var shape = shapes.shapes[f];
                    var count = (int)Math.Min(shape.vertexCount, Math.Max(0, shapes.vertices.Count - (int)shape.firstVertex));
                    var vertices = shapes.vertices.GetRange((int)shape.firstVertex, count).Where(x => x.index < mesh.m_VertexCount).ToList();
                    frames.Add(new Frame
                    {
                        Weight = f < shapes.fullWeights.Length ? shapes.fullWeights[f] : 100f,
                        Vertices = vertices.Select(x => (int)x.index).ToArray(),
                        Positions = vertices.Select(x => new Vector3(-x.vertex.X, x.vertex.Y, x.vertex.Z)).ToArray(),
                        Normals = shape.hasNormals ? vertices.Select(x => new Vector3(-x.normal.X, x.normal.Y, x.normal.Z)).ToArray() : null,
                    });
                }
                if (frames.Count > 0)
                    set.Channels.Add(new Channel { MeshPath = mesh.m_Name, Name = channel.name.Split('.').Last(), Frames = frames.OrderBy(x => x.Weight).ToArray() });
            }
            return set.Count > 0 ? set : null;
        }

        /// <summary>
        /// positions = base + the weighted shapes (normals too, when given); false (nothing written) when every weight is 0.
        /// </summary>
        public bool Apply(Vector3[] basePositions, Vector3[] baseNormals, float[] weights, Vector3[] positions, Vector3[] normals)
        {
            if (weights == null || weights.All(x => x == 0))
                return false;
            Array.Copy(basePositions, positions, Math.Min(basePositions.Length, positions.Length));
            if (baseNormals != null && normals != null)
                Array.Copy(baseNormals, normals, Math.Min(baseNormals.Length, normals.Length));
            for (int c = 0; c < Channels.Count && c < weights.Length; c++)
            {
                var weight = weights[c];
                if (weight == 0)
                    continue;
                var frames = Channels[c].Frames;
                //Unity: below the first frame the first shape scales from 0; between two frames they blend; past the last it extrapolates
                if (weight <= frames[0].Weight || frames.Length == 1)
                {
                    Add(frames[0], frames[0].Weight != 0 ? weight / frames[0].Weight : 0, positions, normals);
                    continue;
                }
                var next = Array.FindIndex(frames, x => x.Weight >= weight);
                if (next < 0)
                {
                    var last = frames[^1];
                    Add(last, last.Weight != 0 ? weight / last.Weight : 0, positions, normals);
                    continue;
                }
                var previous = frames[next - 1];
                var t = (weight - previous.Weight) / Math.Max(frames[next].Weight - previous.Weight, 1e-6f);
                Add(previous, 1 - t, positions, normals);
                Add(frames[next], t, positions, normals);
            }
            return true;
        }

        private static void Add(Frame frame, float amount, Vector3[] positions, Vector3[] normals)
        {
            if (amount == 0)
                return;
            for (int i = 0; i < frame.Vertices.Length; i++)
            {
                var vertex = frame.Vertices[i];
                if ((uint)vertex >= (uint)positions.Length)
                    continue;
                positions[vertex] += frame.Positions[i] * amount;
                if (normals != null && frame.Normals != null && (uint)vertex < (uint)normals.Length)
                    normals[vertex] += frame.Normals[i] * amount;
            }
        }

        private static Vector3 ToNumerics(AssetStudio.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
