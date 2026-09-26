using System;
using System.Collections.Generic;

namespace AssetStudio.Avalonia
{
    using Matrix4x4 = System.Numerics.Matrix4x4;
    using Quaternion = System.Numerics.Quaternion;
    using Vector3 = System.Numerics.Vector3;

    /// <summary>
    /// Poses a model converted by <see cref="ModelConverter"/>: the frame hierarchy, at rest or sampled from one of its
    /// animations, places the vertices of each mesh (rigidly with the frame of the mesh, or skinned by its bones).
    /// Everything is in the (mirrored) space of the converter; matrices are System.Numerics (row vectors).
    /// </summary>
    public sealed class ModelAnimator
    {
        private sealed class Track
        {
            public int Frame;
            public ImportedKeyframe<AssetStudio.Vector3>[] Translations;
            public ImportedKeyframe<AssetStudio.Quaternion>[] Rotations;
            public ImportedKeyframe<AssetStudio.Vector3>[] Scalings;
        }

        private sealed class Clip
        {
            public string Name;
            public float Duration;
            public Track[] Tracks;
        }

        private readonly int[] parents;
        private readonly Vector3[] restPosition;
        private readonly Quaternion[] restRotation;
        private readonly Vector3[] restScale;
        private readonly Matrix4x4[] world;

        private readonly Vector3[] meshVertices;
        private readonly Vector3[] meshNormals;
        private readonly int[] vertexFrame; //rigid vertices: frame of their mesh; skinned: -1
        private readonly int[] influenceStart; //skinned vertices: influences [start, start + count)
        private readonly byte[] influenceCount;
        private readonly List<int> influenceBone = new List<int>();
        private readonly List<float> influenceWeight = new List<float>();
        private readonly List<int> boneFrame = new List<int>();
        private readonly List<Matrix4x4> boneBindPose = new List<Matrix4x4>();
        private readonly Matrix4x4[] skin;

        private readonly List<Clip> clips = new List<Clip>();
        private readonly List<(int child, int parent)> skeleton = new List<(int, int)>();

        public int ClipCount => clips.Count;
        public string ClipName(int clip) => clips[clip].Name;
        public float ClipDuration(int clip) => clips[clip].Duration;
        public bool HasSkeleton => skeleton.Count > 0;

        /// <param name="vertices">the vertices of the meshes of the model, one after the other (mesh space)</param>
        public ModelAnimator(ModelConverter model, Vector3[] vertices, Vector3[] normals)
        {
            var frames = new List<ImportedFrame>();
            var parentList = new List<int>();
            var index = new Dictionary<ImportedFrame, int>();
            void Add(ImportedFrame frame, int parent)
            {
                index[frame] = frames.Count;
                frames.Add(frame);
                parentList.Add(parent);
                var self = frames.Count - 1;
                for (int i = 0; i < frame.Count; i++)
                    Add(frame[i], self);
            }
            Add(model.RootFrame, -1);
            parents = parentList.ToArray();
            restPosition = new Vector3[frames.Count];
            restRotation = new Quaternion[frames.Count];
            restScale = new Vector3[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                var p = frames[i].LocalPosition;
                var r = frames[i].LocalRotation;
                var s = frames[i].LocalScale;
                restPosition[i] = new Vector3(p.X, p.Y, p.Z);
                restRotation[i] = r.X == 0 && r.Y == 0 && r.Z == 0 && r.W == 0 ? Quaternion.Identity : Quaternion.Normalize(new Quaternion(r.X, r.Y, r.Z, r.W));
                restScale[i] = new Vector3(s.X, s.Y, s.Z);
            }
            world = new Matrix4x4[frames.Count];

            int FrameOf(string path) => path != null && model.RootFrame.FindFrameByPath(path) is { } f && index.TryGetValue(f, out var i) ? i : -1;

            meshVertices = (Vector3[])vertices.Clone();
            meshNormals = (Vector3[])normals?.Clone();
            vertexFrame = new int[vertices.Length];
            influenceStart = new int[vertices.Length];
            influenceCount = new byte[vertices.Length];
            var boneFrames = new HashSet<int>();
            var offset = 0;
            foreach (var mesh in model.MeshList)
            {
                var meshFrame = Math.Max(0, FrameOf(mesh.Path));
                var bones = new int[mesh.BoneList?.Count ?? 0];
                for (int b = 0; b < bones.Length; b++)
                {
                    var bone = mesh.BoneList[b];
                    var frame = FrameOf(bone.Path);
                    bones[b] = -1;
                    if (frame < 0)
                        continue;
                    bones[b] = boneFrame.Count;
                    boneFrame.Add(frame);
                    boneBindPose.Add(ToRowVector(bone.Matrix));
                    boneFrames.Add(frame);
                }
                for (int v = 0; v < mesh.VertexList.Count; v++)
                {
                    var i = offset + v;
                    vertexFrame[i] = meshFrame;
                    var vertex = mesh.VertexList[v];
                    if (bones.Length == 0 || vertex.Weights == null || vertex.BoneIndices == null)
                        continue;
                    var start = influenceBone.Count;
                    var total = 0f;
                    for (int k = 0; k < vertex.Weights.Length && k < vertex.BoneIndices.Length; k++)
                    {
                        var weight = vertex.Weights[k];
                        var b = vertex.BoneIndices[k];
                        if (weight <= 0 || (uint)b >= (uint)bones.Length || bones[b] < 0)
                            continue;
                        influenceBone.Add(bones[b]);
                        influenceWeight.Add(weight);
                        total += weight;
                    }
                    var count = influenceBone.Count - start;
                    if (count == 0 || total <= 0)
                    {
                        influenceBone.RemoveRange(start, count);
                        influenceWeight.RemoveRange(start, count);
                        continue;
                    }
                    for (int k = start; k < influenceWeight.Count; k++)
                        influenceWeight[k] /= total;
                    vertexFrame[i] = -1;
                    influenceStart[i] = start;
                    influenceCount[i] = (byte)Math.Min(count, 255);
                }
                offset += mesh.VertexList.Count;
            }
            skin = new Matrix4x4[boneFrame.Count];

            //a line from each bone to the closest ancestor that is a bone too
            foreach (var frame in boneFrames)
            {
                var parent = parents[frame];
                while (parent >= 0 && !boneFrames.Contains(parent))
                    parent = parents[parent];
                if (parent >= 0)
                    skeleton.Add((frame, parent));
            }

            foreach (var animation in model.AnimationList)
            {
                var clip = new Clip { Name = animation.Name };
                var tracks = new List<Track>();
                foreach (var track in animation.TrackList)
                {
                    var frame = FrameOf(track.Path);
                    if (frame < 0 || (track.Translations.Count == 0 && track.Rotations.Count == 0 && track.Scalings.Count == 0))
                        continue;
                    tracks.Add(new Track { Frame = frame, Translations = track.Translations.ToArray(), Rotations = track.Rotations.ToArray(), Scalings = track.Scalings.ToArray() });
                    foreach (var key in track.Translations) clip.Duration = Math.Max(clip.Duration, key.time);
                    foreach (var key in track.Rotations) clip.Duration = Math.Max(clip.Duration, key.time);
                    foreach (var key in track.Scalings) clip.Duration = Math.Max(clip.Duration, key.time);
                }
                if (tracks.Count == 0)
                    continue;
                clip.Tracks = tracks.ToArray();
                clips.Add(clip);
            }
        }

        // the bind poses read from the files have the translation in M30..M32: they are already laid out for row vectors
        // (the FBX exporter copies them as they are)
        private static Matrix4x4 ToRowVector(AssetStudio.Matrix4x4 m) => new Matrix4x4(
            m.M00, m.M01, m.M02, m.M03,
            m.M10, m.M11, m.M12, m.M13,
            m.M20, m.M21, m.M22, m.M23,
            m.M30, m.M31, m.M32, m.M33);

        /// <summary>
        /// Places the vertices for a clip at a time (clip -1: the rest pose).
        /// </summary>
        public void Pose(int clip, float time, Vector3[] vertices, Vector3[] normals)
        {
            var position = (Vector3[])restPosition.Clone();
            var rotation = (Quaternion[])restRotation.Clone();
            var scale = (Vector3[])restScale.Clone();
            if (clip >= 0 && clip < clips.Count)
            {
                foreach (var track in clips[clip].Tracks)
                {
                    if (track.Translations.Length > 0)
                        position[track.Frame] = Sample(track.Translations, time, (a, b, t) => Vector3.Lerp(ToNumerics(a), ToNumerics(b), t));
                    if (track.Rotations.Length > 0)
                        rotation[track.Frame] = Sample(track.Rotations, time, (a, b, t) => Quaternion.Slerp(ToNumerics(a), ToNumerics(b), t));
                    if (track.Scalings.Length > 0)
                        scale[track.Frame] = Sample(track.Scalings, time, (a, b, t) => Vector3.Lerp(ToNumerics(a), ToNumerics(b), t));
                }
            }
            for (int i = 0; i < world.Length; i++) //parents come first
            {
                var local = Matrix4x4.CreateScale(scale[i]) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation[i])) * Matrix4x4.CreateTranslation(position[i]);
                world[i] = parents[i] < 0 ? local : local * world[parents[i]];
            }
            for (int b = 0; b < skin.Length; b++)
            {
                skin[b] = boneBindPose[b] * world[boneFrame[b]];
            }
            System.Threading.Tasks.Parallel.For(0, Math.Min(vertices.Length, meshVertices.Length), i =>
            {
                Matrix4x4 m;
                if (vertexFrame[i] >= 0)
                {
                    m = world[vertexFrame[i]];
                }
                else
                {
                    m = default;
                    var start = influenceStart[i];
                    for (int k = start; k < start + influenceCount[i]; k++)
                        m += skin[influenceBone[k]] * influenceWeight[k];
                }
                vertices[i] = Vector3.Transform(meshVertices[i], m);
                if (normals != null && meshNormals != null && i < normals.Length)
                {
                    var n = Vector3.TransformNormal(meshNormals[i], m);
                    var length = n.Length();
                    normals[i] = length > 0 ? n / length : n;
                }
            });
        }

        /// <summary>The bones of the last pose as lines (child, parent).</summary>
        public IEnumerable<(Vector3 child, Vector3 parent)> SkeletonLines()
        {
            foreach (var (child, parent) in skeleton)
                yield return (world[child].Translation, world[parent].Translation);
        }

        private static T Sample<TKey, T>(ImportedKeyframe<TKey>[] keys, float time, Func<TKey, TKey, float, T> lerp)
        {
            if (time <= keys[0].time || keys.Length == 1)
                return lerp(keys[0].value, keys[0].value, 0);
            if (time >= keys[^1].time)
                return lerp(keys[^1].value, keys[^1].value, 0);
            int lo = 0, hi = keys.Length - 1;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (keys[mid].time <= time)
                    lo = mid;
                else
                    hi = mid;
            }
            var span = keys[hi].time - keys[lo].time;
            return lerp(keys[lo].value, keys[hi].value, span > 0 ? (time - keys[lo].time) / span : 0);
        }

        private static Vector3 ToNumerics(AssetStudio.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
        private static Quaternion ToNumerics(AssetStudio.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
