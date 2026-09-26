using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections;
using System.Collections.Specialized;

namespace AssetStudio
{
    /// <summary>
    /// The heightmap of a TerrainData, read through its type tree (the class has no reader of its own).
    /// </summary>
    public static class TerrainDataConverter
    {
        private const double MaxHeight = 32766.0; //heights are stored as 0 to 32766

        public static bool TryGetHeightmap(OrderedDictionary terrainData, out ushort[] heights, out int width, out int height)
        {
            heights = null;
            width = height = 0;
            if (terrainData?["m_Heightmap"] is not OrderedDictionary heightmap || heightmap["m_Heights"] is not IList values)
                return false;
            if (heightmap.Contains("m_Resolution")) //2019.3 and up
            {
                width = height = Convert.ToInt32(heightmap["m_Resolution"]);
            }
            else
            {
                width = Convert.ToInt32(heightmap["m_Width"]);
                height = Convert.ToInt32(heightmap["m_Height"]);
            }
            if (width <= 0 || height <= 0 || values.Count < width * height)
                return false;
            heights = new ushort[width * height];
            for (int i = 0; i < heights.Length; i++)
            {
                var value = Math.Clamp(Convert.ToInt32(values[i]), 0, (int)MaxHeight);
                heights[i] = (ushort)Math.Round(value / MaxHeight * ushort.MaxValue);
            }
            return true;
        }

        /// <summary>
        /// 16-bit grayscale, the first row of the heightmap (the terrain's -Z edge) at the bottom.
        /// </summary>
        public static Image<L16> ToImage(ushort[] heights, int width, int height)
        {
            var image = new Image<L16>(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    image[x, height - 1 - y] = new L16(heights[y * width + x]);
                }
            }
            return image;
        }

        public static void SaveAsPng(ushort[] heights, int width, int height, string path)
        {
            using var image = ToImage(heights, width, height);
            image.SaveAsPng(path, new PngEncoder { BitDepth = PngBitDepth.Bit16, ColorType = PngColorType.Grayscale });
        }

        /// <summary>
        /// For display: 8-bit gray.
        /// </summary>
        public static Image<Bgra32> ToPreviewImage(ushort[] heights, int width, int height)
        {
            using var image = ToImage(heights, width, height);
            return image.CloneAs<Bgra32>();
        }

        /// <summary>
        /// The format of Unity's terrain "Import Raw": 16 bits little endian, rows in heightmap order.
        /// </summary>
        public static byte[] ToRaw(ushort[] heights)
        {
            var raw = new byte[heights.Length * 2];
            for (int i = 0; i < heights.Length; i++)
            {
                raw[i * 2] = (byte)heights[i];
                raw[i * 2 + 1] = (byte)(heights[i] >> 8);
            }
            return raw;
        }
    }
}
