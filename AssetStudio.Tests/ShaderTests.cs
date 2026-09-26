using System.Text;
using Xunit;
using Matrix = System.Numerics.Matrix4x4;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace AssetStudio.Tests
{
    public class ShaderTranslationTests
    {
        private const string VertexHlsl = @"
cbuffer PerDraw : register(b2) { float4x4 model; float4 tint; };
cbuffer PerFrame : register(b0) { float4x4 viewProjection; };
struct Input { float4 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 color : COLOR; };
struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : TEXCOORD1; };
Output main(Input input)
{
    Output o;
    o.position = mul(viewProjection, mul(model, input.position));
    o.uv = input.uv;
    o.color = input.color * tint * input.normal.x;
    return o;
}";

        private const string FragmentHlsl = @"
Texture2D albedo : register(t3);
SamplerState albedoSampler : register(s1);
TextureCube sky : register(t0);
SamplerState skySampler : register(s0);
cbuffer Material : register(b1) { float4 color; };
float4 main(float4 position : SV_POSITION, float2 uv : TEXCOORD0, float4 vertexColor : TEXCOORD1) : SV_TARGET
{
    return albedo.Sample(albedoSampler, uv) * color * vertexColor + sky.Sample(skySampler, float3(uv, 1));
}";

        [SkippableFact]
        public void VertexProgram_ReflectsConstantBufferRegistersAndInputs()
        {
            Skip.IfNot(Hlsl.Available, "vkd3d-shader not available");
            var dxbc = Hlsl.ToDxbc(VertexHlsl, "vs_4_0");

            var inputs = DxbcSignature.ReadInputs(dxbc);
            Assert.Equal(new[] { "POSITION", "NORMAL", "TEXCOORD", "COLOR" }, inputs.Select(x => x.Semantic).ToArray());
            Assert.Equal(new[] { 0, 1, 2, 3 }, inputs.Select(x => x.Register).ToArray());
            Assert.All(inputs, x => Assert.Equal(0, x.SystemValue));

            var spirv = Vkd3dShader.ToSpirv(dxbc, Vkd3dShader.SourceType.DxbcTpf);
            var reflection = SpirvReflection.Read(spirv);
            var buffers = reflection.Resources.Where(x => x.Kind == SpirvReflection.ResourceKind.UniformBuffer).ToList();
            Assert.Equal(new[] { 0, 2 }, buffers.Select(x => x.Register).OrderBy(x => x).ToArray());
            //vkd3d sizes a constant buffer by the registers the program reads
            Assert.Equal(64, buffers.Single(x => x.Register == 0).Size);
            Assert.True(buffers.Single(x => x.Register == 2).Size >= 80);
            Assert.Equal(new[] { 0, 1, 2, 3 }, reflection.InputLocations.OrderBy(x => x).ToArray());
            Assert.All(reflection.Resources, x => Assert.Equal(0u, x.Set));
        }

        [SkippableFact]
        public void FragmentProgram_ReflectsTexturesSamplersAndDimensions()
        {
            Skip.IfNot(Hlsl.Available, "vkd3d-shader not available");
            var dxbc = Hlsl.ToDxbc(FragmentHlsl, "ps_4_0");
            var inputs = DxbcSignature.ReadInputs(dxbc);
            Assert.Contains(inputs, x => x.Semantic == "SV_POSITION" && x.SystemValue != 0);

            var spirv = Vkd3dShader.ToSpirv(dxbc, Vkd3dShader.SourceType.DxbcTpf);
            var reflection = SpirvReflection.Read(spirv);
            var images = reflection.Resources.Where(x => x.Kind == SpirvReflection.ResourceKind.Image).ToDictionary(x => x.Register);
            Assert.Equal(1, images[3].ImageDimension); //2D
            Assert.Equal(3, images[0].ImageDimension); //cube
            Assert.Equal(new[] { 0, 1 }, reflection.Resources.Where(x => x.Kind == SpirvReflection.ResourceKind.Sampler).Select(x => x.Register).OrderBy(x => x).ToArray());
            Assert.Equal(1, reflection.Resources.Single(x => x.Kind == SpirvReflection.ResourceKind.UniformBuffer).Register);

            //the fragment stage goes to descriptor set 1
            var moved = SpirvReflection.Read(SpirvReflection.WithDescriptorSet(spirv, 1));
            Assert.All(moved.Resources, x => Assert.Equal(1u, x.Set));
            Assert.Equal(reflection.Resources.Select(x => x.Binding), moved.Resources.Select(x => x.Binding));
        }
    }

    public class BlobParameterTests
    {
        private static void Field(BinaryWriter w, string name, int rows, int columns, bool matrix, int arraySize, int offset)
        {
            TestUtil.WriteAligned(w, name);
            w.Write(0); //float
            w.Write(rows);
            w.Write(columns);
            w.Write(matrix ? 1 : 0);
            w.Write(arraySize);
            w.Write(offset);
        }

        /// <summary>The parameters as the blob stores them after the byte code (2018.2 and up).</summary>
        private static byte[] Parameters(bool bindChannels)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            if (bindChannels)
            {
                w.Write(0x11u); //source map
                w.Write(2); //bind channels
                w.Write(0u); w.Write(0u);
                w.Write(4u); w.Write(5u);
            }
            w.Write(3); //groups
            //the values outside any constant buffer
            TestUtil.WriteAligned(w, "");
            w.Write(0);
            w.Write(1);
            Field(w, "_GlobalValue", 1, 4, false, 0, 0);
            w.Write(0); //structs
            TestUtil.WriteAligned(w, "UnityPerDraw");
            w.Write(176);
            w.Write(2);
            Field(w, "unity_ObjectToWorld", 4, 4, true, 0, 0);
            Field(w, "unity_WorldTransformParams", 1, 4, false, 0, 144);
            w.Write(1); //structs
            TestUtil.WriteAligned(w, "lights");
            w.Write(160); w.Write(2); w.Write(16);
            w.Write(1);
            Field(w, "color", 1, 3, false, 0, 4);
            TestUtil.WriteAligned(w, "$Globals");
            w.Write(48);
            w.Write(1);
            Field(w, "_Color", 1, 4, false, 0, 32);
            w.Write(0);
            w.Write(5); //resources
            TestUtil.WriteAligned(w, "_MainTex"); w.Write(0); w.Write(3); w.Write(2); w.Write(2u << 1); //texture t3 s2, 2D
            TestUtil.WriteAligned(w, "unity_SpecCube0"); w.Write(0); w.Write(1); w.Write(-1); w.Write(4u << 1); //cube, no sampler
            TestUtil.WriteAligned(w, "UnityPerDraw"); w.Write(1); w.Write(2); w.Write(0); //constant buffer b2
            TestUtil.WriteAligned(w, "$Globals"); w.Write(1); w.Write(0); w.Write(0);
            TestUtil.WriteAligned(w, "sampler_Linear"); w.Write(4); w.Write(5); w.Write(0x42);
            return ms.ToArray();
        }

        [Fact]
        public void AfterByteCode_ReadsGroupsFieldsAndBindings()
        {
            var data = Parameters(true);
            using var reader = new EndianBinaryReader(new MemoryStream(data), EndianType.LittleEndian);
            var parameters = BlobProgramParameters.Read(reader, 202012090, data.Length);
            Assert.Equal(data.Length, reader.BaseStream.Position);
            Assert.Equal(new[] { (0u, 0u), (4u, 5u) }, parameters.BindChannels);
            Assert.Equal("_GlobalValue", parameters.Globals.Single().Name);

            var perDraw = parameters.ConstantBuffers.Single(x => x.Name == "UnityPerDraw");
            Assert.Equal(2, perDraw.Register);
            Assert.Equal(176, perDraw.Size);
            var matrix = perDraw.Fields.Single(x => x.Name == "unity_ObjectToWorld");
            Assert.True(matrix.IsMatrix);
            Assert.Equal(4, matrix.Components);
            Assert.Equal(144, perDraw.Fields.Single(x => x.Name == "unity_WorldTransformParams").Offset);
            //struct members: offset from the struct
            Assert.Equal(164, perDraw.Fields.Single(x => x.Name == "lights.color").Offset);
            Assert.Equal(0, parameters.ConstantBuffers.Single(x => x.Name == "$Globals").Register);

            var mainTex = parameters.Textures.Single(x => x.Name == "_MainTex");
            Assert.Equal((3, 2, 2), (mainTex.Register, mainTex.SamplerRegister, mainTex.Dimension));
            Assert.Equal(4, parameters.Textures.Single(x => x.Name == "unity_SpecCube0").Dimension);
            Assert.Equal((0x42u, 5), parameters.Samplers.Single());
        }

        [Fact]
        public void ParameterEntry_HasAVersionAndNoBindChannels()
        {
            var body = Parameters(false);
            var data = BitConverter.GetBytes(202012090).Concat(body).ToArray();
            using var reader = new EndianBinaryReader(new MemoryStream(data), EndianType.LittleEndian);
            var parameters = BlobProgramParameters.ReadParameterEntry(reader, data.Length);
            Assert.Empty(parameters.BindChannels);
            Assert.Equal(new[] { ("UnityPerDraw", 2), ("$Globals", 0) }, parameters.ConstantBufferBindings);
        }

        [Fact]
        public void TruncatedOrForeignData_IsRefused()
        {
            var data = Parameters(true);
            using var reader = new EndianBinaryReader(new MemoryStream(data), EndianType.LittleEndian);
            Assert.ThrowsAny<Exception>(() => BlobProgramParameters.Read(reader, 202012090, data.Length / 2));
            //a huge count must not allocate
            var bad = new byte[64];
            BitConverter.TryWriteBytes(bad.AsSpan(4), int.MaxValue);
            using var badReader = new EndianBinaryReader(new MemoryStream(bad), EndianType.LittleEndian);
            Assert.Throws<InvalidDataException>(() => BlobProgramParameters.Read(badReader, 202012090, bad.Length));
        }
    }

    public class ShaderSubProgramTests
    {
        /// <summary>A program entry of a blob (2021.2 layout): version, type, stats, keywords, code, parameters.</summary>
        private static byte[] Program(ShaderGpuProgramType type, byte[] code, int version = 202012090)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(version);
            w.Write((int)type);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); //stats
            w.Write(1);
            TestUtil.WriteAligned(w, "DIRECTIONAL");
            w.Write(code.Length);
            w.Write(code);
            while (ms.Position % 4 != 0)
                w.Write((byte)0);
            w.Write(0u); w.Write(0); //no bind channels
            w.Write(0); //no groups
            w.Write(0); //no resources
            return ms.ToArray();
        }

        [Fact]
        public void WgslProgram_ExportsBothStages()
        {
            var vertex = Encoding.UTF8.GetBytes("@vertex fn main() -> @builtin(position) vec4f { return vec4f(); }\n");
            var fragment = Encoding.UTF8.GetBytes("@fragment fn main() -> @location(0) vec4f { return vec4f(1.0); }\n");
            using var codeStream = new MemoryStream();
            using (var w = new BinaryWriter(codeStream, Encoding.UTF8, true))
            {
                w.Write(20); w.Write(vertex.Length);
                w.Write(20 + vertex.Length); w.Write(fragment.Length);
                w.Write(1); //flags
                w.Write(vertex);
                w.Write(fragment);
            }
            var data = Program(ShaderGpuProgramType.WGSL, codeStream.ToArray());
            using var reader = new EndianBinaryReader(new MemoryStream(data), EndianType.LittleEndian);
            var program = new ShaderSubProgram(reader, false, data.Length);
            Assert.NotNull(program.Parameters);
            var text = program.Export();
            Assert.Contains("\"DIRECTIONAL\"", text);
            Assert.Contains("// flags: 1", text);
            var vertexAt = text.IndexOf("---- vertex (WGSL) ----", StringComparison.Ordinal);
            var fragmentAt = text.IndexOf("---- fragment (WGSL) ----", StringComparison.Ordinal);
            Assert.True(vertexAt >= 0 && fragmentAt > vertexAt);
            Assert.Contains("@vertex fn main()", text[vertexAt..fragmentAt]);
            Assert.Contains("@fragment fn main()", text[fragmentAt..]);
        }

        [Fact]
        public void EntryThatIsNotAProgram_IsRefused()
        {
            //an entry that doesn't start with a program version (a date, YYYYMMDD and a digit)
            var data = new byte[256];
            BitConverter.TryWriteBytes(data.AsSpan(0), 201946554);
            using var reader = new EndianBinaryReader(new MemoryStream(data), EndianType.LittleEndian);
            Assert.Throws<InvalidDataException>(() => new ShaderSubProgram(reader, false, data.Length));
            //a code length past the entry must not be read
            var program = Program(ShaderGpuProgramType.DX11VertexSM40, new byte[16]);
            using var truncated = new EndianBinaryReader(new MemoryStream(program), EndianType.LittleEndian);
            Assert.Throws<InvalidDataException>(() => new ShaderSubProgram(truncated, false, 40));
        }
    }

    public class UnityShaderValuesTests
    {
        [Fact]
        public void Matrices_AreWrittenColumnMajor()
        {
            var values = new UnityShaderValues();
            //Unity's (column vector) matrix with a translation (1, 2, 3); stored transposed
            var unity = Matrix.CreateTranslation(1, 2, 3);
            values.Set("unity_ObjectToWorld", unity);
            values.Set("_Color", 0.25f, 0.5f, 0.75f, 1f);
            var buffer = new UnityConstantBuffer
            {
                Size = 96,
                Fields =
                {
                    new UnityShaderField { Name = "unity_ObjectToWorld", Offset = 0, IsMatrix = true, Components = 4 },
                    new UnityShaderField { Name = "_Color", Offset = 64, Components = 3 },
                    new UnityShaderField { Name = "_Missing", Offset = 80, Components = 4 },
                },
            };
            var data = values.WriteConstantBuffer(buffer, 0);
            var floats = new float[data.Length / 4];
            Buffer.BlockCopy(data, 0, floats, 0, data.Length);
            //column 3 holds the translation
            Assert.Equal(new[] { 1f, 2f, 3f, 1f }, floats[12..16]);
            Assert.Equal(new[] { 1f, 0f, 0f, 0f }, floats[0..4]);
            //a float3 writes 3 components
            Assert.Equal(new[] { 0.25f, 0.5f, 0.75f, 0f }, floats[16..20]);
            Assert.All(floats[20..24], x => Assert.Equal(0f, x));
        }

        [Fact]
        public void Camera_ProjectsLikeUnityOnDirect3D11()
        {
            var values = new UnityShaderValues();
            var target = new Vec3(0, 1, 0);
            values.SetCamera(Matrix.Identity, new Vec3(0, 1, -5), target, Vec3.UnitY, 400, 300);
            values.TryGet("unity_MatrixVP", out var vp);
            var m = new Matrix(vp[0], vp[1], vp[2], vp[3], vp[4], vp[5], vp[6], vp[7], vp[8], vp[9], vp[10], vp[11], vp[12], vp[13], vp[14], vp[15]);
            Vec3 Ndc(Vec3 p)
            {
                var clip = Vec4.Transform(new Vec4(p, 1), m);
                return new Vec3(clip.X, clip.Y, clip.Z) / clip.W;
            }
            var center = Ndc(target);
            Assert.Equal(0, center.X, 4);
            Assert.Equal(0, center.Y, 4);
            //reversed Z: nearer is larger, inside [0, 1]
            var near = Ndc(target + new Vec3(0, 0, -1));
            var far = Ndc(target + new Vec3(0, 0, 1));
            Assert.InRange(far.Z, 0, 1);
            Assert.InRange(near.Z, 0, 1);
            Assert.True(near.Z > far.Z);
            //the camera looks down +Z from -Z (Unity's left-handed world): +X is on the right
            Assert.True(Ndc(target + Vec3.UnitX).X > 0);
            //rendered like into a render texture: Y flipped
            Assert.True(Ndc(target + Vec3.UnitY).Y < 0);
            values.TryGet("_ProjectionParams", out var projectionParams);
            Assert.Equal(-1, projectionParams[0]);

            //MVP is M, then V, then P
            values.TryGet("glstate_matrix_mvp", out var mvp);
            Assert.Equal(vp, mvp);
        }
    }

    public class EulerTests
    {
        [Theory]
        [InlineData(30, 0, 0)]
        [InlineData(0, 45, 0)]
        [InlineData(0, 0, -60)]
        [InlineData(10, 200, -35)]
        [InlineData(-170, 80, 95)]
        public void EulerToQuaternion_IsXThenYThenZ(float x, float y, float z)
        {
            var q = ModelConverter.EulerToQuaternion(new Vector3(x, y, z));
            float Rad(float d) => d * MathF.PI / 180;
            var expected = System.Numerics.Quaternion.CreateFromAxisAngle(Vec3.UnitZ, Rad(z)) * System.Numerics.Quaternion.CreateFromAxisAngle(Vec3.UnitY, Rad(y))
                * System.Numerics.Quaternion.CreateFromAxisAngle(Vec3.UnitX, Rad(x));
            var dot = q.X * expected.X + q.Y * expected.Y + q.Z * expected.Z + q.W * expected.W;
            Assert.True(MathF.Abs(MathF.Abs(dot) - 1) < 1e-5, $"{q.X} {q.Y} {q.Z} {q.W} vs {expected}");
        }
    }
}
