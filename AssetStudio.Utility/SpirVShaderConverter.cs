using Smolv;
using SpirV;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetStudio
{
    public static class SpirVShaderConverter
    {
        public static string Convert(byte[] m_ProgramCode)
        {
            var sb = new StringBuilder();
            using (var ms = new MemoryStream(m_ProgramCode))
            {
                using (var reader = new BinaryReader(ms))
                {
                    int requirements = reader.ReadInt32();
                    int minOffset = m_ProgramCode.Length;
                    int snippetCount = 5;
                    /*if (version[0] > 2019 || (version[0] == 2019 && version[1] >= 3)) //2019.3 and up
                    {
                        snippetCount = 6;
                    }*/
                    for (int i = 0; i < snippetCount; i++)
                    {
                        if (reader.BaseStream.Position >= minOffset)
                        {
                            break;
                        }

                        int offset = reader.ReadInt32();
                        int size = reader.ReadInt32();
                        if (size > 0)
                        {
                            if (offset < minOffset)
                            {
                                minOffset = offset;
                            }
                            var pos = ms.Position;
                            sb.Append(ExportSnippet(ms, offset, size));
                            ms.Position = pos;
                        }
                    }
                }
            }
            return sb.ToString();
        }

        private static string ExportSnippet(Stream stream, int offset, int size)
        {
            var spirv = DecodeSnippet(((MemoryStream)stream).ToArray(), offset, size);
            using var decodedStream = new MemoryStream(spirv);
            var module = Module.ReadFrom(decodedStream);
            var disassembler = new Disassembler();
            return disassembler.Disassemble(module, DisassemblyOptions.Default).Replace("\r\n", "\n");
        }

        /// <summary>
        /// The SPIR-V of the stages of a Vulkan program (0 vertex, 1 fragment, 2 hull, 3 domain, 4 geometry; null when
        /// absent): after a requirements word, the offset and size of each SMOL-V snippet.
        /// </summary>
        public static byte[][] DecodeStages(byte[] programCode)
        {
            var stages = new byte[5][];
            if (programCode == null || programCode.Length < 4 + 8 * 2)
                return stages;
            for (int i = 0; i < stages.Length && 4 + i * 8 + 8 <= programCode.Length; i++)
            {
                var offset = BitConverter.ToInt32(programCode, 4 + i * 8);
                var size = BitConverter.ToInt32(programCode, 8 + i * 8);
                if (size > 0 && offset >= 0 && offset + size <= programCode.Length)
                    stages[i] = DecodeSnippet(programCode, offset, size);
            }
            return stages;
        }

        /// <summary>
        /// A SMOL-V snippet as SPIR-V. Version 0 files may also be in the 2016 encoding (same header): the one that decodes
        /// to the size the header gives and parses is it.
        /// </summary>
        public static byte[] DecodeSnippet(byte[] data, int offset, int size)
        {
            using var stream = new MemoryStream(data, offset, size, false);
            int decodedSize = SmolvDecoder.GetDecodedBufferSize(stream);
            if (decodedSize <= 0 || decodedSize > 64 * 1024 * 1024)
            {
                throw new Exception("Invalid SMOL-V shader header");
            }
            foreach (var beforeZero in new[] { false, true })
            {
                stream.Position = 0;
                var decoded = new byte[decodedSize];
                using var decodedStream = new MemoryStream(decoded);
                try
                {
                    if (SmolvDecoder.Decode(stream, size, decodedStream, beforeZero))
                    {
                        decodedStream.Position = 0;
                        Module.ReadFrom(decodedStream);
                        return decoded;
                    }
                }
                catch (Exception e) when (e is EndOfStreamException || e is IOException || e is NotSupportedException || e is ArgumentException
                    || e is IndexOutOfRangeException || e is InvalidOperationException || e is KeyNotFoundException || e is FormatException)
                {
                    //the other encoding
                }
            }
            throw new Exception("Unable to decode SMOL-V shader");
        }
    }
}
