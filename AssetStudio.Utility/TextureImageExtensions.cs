using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;

namespace AssetStudio
{
    /// <summary>
    /// The images of a texture with several: the faces of a Cubemap and CubemapArray, the slices of a Texture2DArray and Texture3D.
    /// Only the top mip level of each image is decoded.
    /// </summary>
    public static class TextureImageExtensions
    {
        private sealed class Layout
        {
            public ResourceReader Data;
            public TextureFormat Format;
            public int Width;
            public int Height;
            public int Count;
            public int Stride; //bytes from one image to the next
        }

        public static bool HasImages(this Texture texture) => texture is Cubemap || texture is Texture2DArray || texture is Texture3D || texture is CubemapArray;

        public static bool IsCubemap(this Texture texture) => texture is Cubemap || texture is CubemapArray;

        public static int GetImageCount(this Texture texture) => GetLayout(texture)?.Count ?? 0;

        public static (int width, int height, TextureFormat format) GetImageInfo(this Texture texture)
        {
            var layout = GetLayout(texture);
            return layout == null ? default : (layout.Width, layout.Height, layout.Format);
        }

        /// <summary>
        /// Decodes the images one at a time, null for an image that can't be decoded.
        /// flip as for Texture2D: cubemap faces are stored top down, so they are never flipped.
        /// </summary>
        public static IEnumerable<Image<Bgra32>> ConvertToImages(this Texture texture, bool flip)
        {
            flip &= !texture.IsCubemap();
            var layout = GetLayout(texture);
            if (layout == null || layout.Count == 0)
                yield break;
            var data = layout.Data.GetData();
            for (int i = 0; i < layout.Count; i++)
            {
                var offset = i * layout.Stride;
                if (offset >= data.Length)
                {
                    yield return null;
                    continue;
                }
                var size = Math.Min(layout.Stride, data.Length - offset);
                var converter = new Texture2DConverter(data, offset, size, layout.Width, layout.Height, layout.Format, texture.version, texture.platform);
                var buff = new byte[layout.Width * layout.Height * 4];
                if (!converter.DecodeTexture2D(buff))
                {
                    yield return null;
                    continue;
                }
                var image = Image.LoadPixelData<Bgra32>(buff, layout.Width, layout.Height);
                if (flip)
                {
                    image.Mutate(x => x.Flip(FlipMode.Vertical));
                }
                yield return image;
            }
        }

        /// <summary>
        /// The images to export with their index: a cross for each cube, else each slice. Images that can't be decoded are skipped.
        /// </summary>
        public static IEnumerable<(int index, Image<Bgra32> image)> ConvertToExportImages(this Texture texture, bool flip)
        {
            if (!texture.IsCubemap())
            {
                var i = 0;
                foreach (var image in texture.ConvertToImages(flip))
                {
                    if (image != null)
                        yield return (i, image);
                    i++;
                }
                yield break;
            }
            var faces = new List<Image<Bgra32>>();
            var cube = 0;
            foreach (var image in texture.ConvertToImages(flip))
            {
                faces.Add(image);
                if (faces.Count < 6)
                    continue;
                var cross = ToCubemapCross(faces);
                DisposeAll(faces);
                if (cross != null)
                    yield return (cube, cross);
                cube++;
            }
            if (faces.Count > 0) //crunched: only the first face
            {
                var cross = ToCubemapCross(faces);
                DisposeAll(faces);
                if (cross != null)
                    yield return (cube, cross);
            }
        }

        public static ResourceReader GetImageData(this Texture texture) => GetLayout(texture)?.Data;

        public static StreamingInfo GetStreamData(this Texture texture) => texture switch
        {
            Texture2D m_Texture2D => m_Texture2D.m_StreamData,
            Texture2DArray m_Texture2DArray => m_Texture2DArray.m_StreamData,
            Texture3D m_Texture3D => m_Texture3D.m_StreamData,
            CubemapArray m_CubemapArray => m_CubemapArray.m_StreamData,
            _ => null,
        };

        /// <summary>
        /// Six faces (+X, -X, +Y, -Y, +Z, -Z) as a horizontal cross, the layout Unity imports cubemaps from:
        /// +Y above, then -X, +Z, +X, -Z, then -Y below.
        /// </summary>
        public static Image<Bgra32> ToCubemapCross(IReadOnlyList<Image<Bgra32>> faces)
        {
            var size = 0;
            foreach (var face in faces)
            {
                if (face != null)
                    size = Math.Max(size, face.Width);
            }
            if (size == 0)
                return null;
            var cells = new (int x, int y)[] { (2, 1), (0, 1), (1, 0), (1, 2), (1, 1), (3, 1) };
            var cross = new Image<Bgra32>(size * 4, size * 3);
            for (int i = 0; i < 6 && i < faces.Count; i++)
            {
                var face = faces[i];
                if (face == null)
                    continue;
                var (x, y) = cells[i];
                cross.Mutate(c => c.DrawImage(face, new Point(x * size, y * size), 1f));
            }
            return cross;
        }

        /// <summary>
        /// All images in a grid, each scaled to fit a cell of at most cellSize pixels, for a preview.
        /// A cubemap is shown as a cross per cube.
        /// </summary>
        public static Image<Bgra32> ConvertToPreview(this Texture texture, bool flip, int cellSize = 512, int maxImages = 64)
        {
            var isCubemap = texture.IsCubemap();
            var cells = new List<Image<Bgra32>>();
            try
            {
                foreach (var (_, image) in texture.ConvertToExportImages(flip))
                {
                    cells.Add(Fit(image, isCubemap ? cellSize * 2 : cellSize));
                    if (cells.Count >= maxImages)
                        break;
                }
                if (cells.Count == 0)
                    return null;
                if (cells.Count == 1)
                {
                    var single = cells[0];
                    cells.Clear();
                    return single;
                }
                var columns = (int)Math.Ceiling(Math.Sqrt(cells.Count));
                var rows = (cells.Count + columns - 1) / columns;
                int cellWidth = 0, cellHeight = 0;
                foreach (var cell in cells)
                {
                    cellWidth = Math.Max(cellWidth, cell.Width);
                    cellHeight = Math.Max(cellHeight, cell.Height);
                }
                const int gap = 4;
                var sheet = new Image<Bgra32>(columns * cellWidth + (columns - 1) * gap, rows * cellHeight + (rows - 1) * gap);
                for (int i = 0; i < cells.Count; i++)
                {
                    var cell = cells[i];
                    var point = new Point(i % columns * (cellWidth + gap), i / columns * (cellHeight + gap));
                    sheet.Mutate(c => c.DrawImage(cell, point, 1f));
                }
                return sheet;
            }
            finally
            {
                DisposeAll(cells);
            }
        }

        private static Image<Bgra32> Fit(Image<Bgra32> image, int maxSize)
        {
            if (image.Width > maxSize || image.Height > maxSize)
            {
                var scale = Math.Min((double)maxSize / image.Width, (double)maxSize / image.Height);
                image.Mutate(x => x.Resize(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))));
            }
            return image;
        }

        private static void DisposeAll(List<Image<Bgra32>> images)
        {
            foreach (var image in images)
                image?.Dispose();
            images.Clear();
        }

        private static Layout GetLayout(Texture texture)
        {
            Layout layout;
            switch (texture)
            {
                case Cubemap m_Cubemap:
                    layout = new Layout { Data = m_Cubemap.image_data, Format = m_Cubemap.m_TextureFormat, Width = m_Cubemap.m_Width, Height = m_Cubemap.m_Height, Count = 6 };
                    break;
                case Texture2DArray m_Texture2DArray:
                    layout = new Layout { Data = m_Texture2DArray.image_data, Format = m_Texture2DArray.m_TextureFormat, Width = m_Texture2DArray.m_Width, Height = m_Texture2DArray.m_Height, Count = m_Texture2DArray.m_Depth };
                    break;
                case CubemapArray m_CubemapArray:
                    layout = new Layout { Data = m_CubemapArray.image_data, Format = m_CubemapArray.m_TextureFormat, Width = m_CubemapArray.m_Width, Height = m_CubemapArray.m_Width, Count = m_CubemapArray.m_CubemapCount * 6 };
                    break;
                case Texture3D m_Texture3D:
                    //the slices of the top mip level come first
                    layout = new Layout { Data = m_Texture3D.image_data, Format = m_Texture3D.m_TextureFormat, Width = m_Texture3D.m_Width, Height = m_Texture3D.m_Height, Count = m_Texture3D.m_Depth };
                    layout.Stride = GetImageSize(layout.Format, layout.Width, layout.Height);
                    if (layout.Stride <= 0)
                        layout.Count = Math.Min(layout.Count, 1);
                    return layout.Data == null || layout.Width <= 0 || layout.Height <= 0 ? null : layout;
                default:
                    return null;
            }
            if (layout.Data == null || layout.Count <= 0 || layout.Width <= 0 || layout.Height <= 0)
                return null;
            if (IsCrunched(layout.Format))
            {
                //one crunch file for all images, only the first one can be decoded
                layout.Count = 1;
                layout.Stride = layout.Data.Size;
            }
            else
            {
                //each image with its mip levels
                layout.Stride = layout.Data.Size / layout.Count;
            }
            return layout;
        }

        private static bool IsCrunched(TextureFormat format)
        {
            return format == TextureFormat.DXT1Crunched || format == TextureFormat.DXT5Crunched || format == TextureFormat.ETC_RGB4Crunched || format == TextureFormat.ETC2_RGBA8Crunched;
        }

        /// <summary>
        /// Size in bytes of one image of the top mip level, -1 when unknown.
        /// </summary>
        public static int GetImageSize(TextureFormat format, int width, int height)
        {
            int Blocks(int blockWidth, int blockHeight, int blockBytes) => (width + blockWidth - 1) / blockWidth * ((height + blockHeight - 1) / blockHeight) * blockBytes;
            switch (format)
            {
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                    return width * height;
                case TextureFormat.ARGB4444:
                case TextureFormat.RGBA4444:
                case TextureFormat.RGB565:
                case TextureFormat.R16:
                case TextureFormat.R16_Alt:
                case TextureFormat.RHalf:
                case TextureFormat.RG16:
                case TextureFormat.YUY2:
                    return width * height * 2;
                case TextureFormat.RGB24:
                case TextureFormat.BGR24:
                    return width * height * 3;
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                case TextureFormat.RGHalf:
                case TextureFormat.RFloat:
                case TextureFormat.RGB9e5Float:
                case TextureFormat.RG32:
                    return width * height * 4;
                case TextureFormat.RGB48:
                    return width * height * 6;
                case TextureFormat.RGBAHalf:
                case TextureFormat.RGFloat:
                case TextureFormat.RGBA64:
                    return width * height * 8;
                case TextureFormat.RGBFloat:
                    return width * height * 12;
                case TextureFormat.RGBAFloat:
                    return width * height * 16;
                case TextureFormat.DXT1:
                case TextureFormat.BC4:
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC_RGB4_3DS:
                case TextureFormat.ETC2_RGB:
                case TextureFormat.ETC2_RGBA1:
                case TextureFormat.EAC_R:
                case TextureFormat.EAC_R_SIGNED:
                case TextureFormat.ATC_RGB4:
                    return Blocks(4, 4, 8);
                case TextureFormat.DXT3:
                case TextureFormat.DXT5:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ETC_RGBA8_3DS:
                case TextureFormat.EAC_RG:
                case TextureFormat.EAC_RG_SIGNED:
                case TextureFormat.ATC_RGBA8:
                    return Blocks(4, 4, 16);
                case TextureFormat.ASTC_RGB_4x4:
                case TextureFormat.ASTC_RGBA_4x4:
                case TextureFormat.ASTC_HDR_4x4:
                    return Blocks(4, 4, 16);
                case TextureFormat.ASTC_RGB_5x5:
                case TextureFormat.ASTC_RGBA_5x5:
                case TextureFormat.ASTC_HDR_5x5:
                    return Blocks(5, 5, 16);
                case TextureFormat.ASTC_RGB_6x6:
                case TextureFormat.ASTC_RGBA_6x6:
                case TextureFormat.ASTC_HDR_6x6:
                    return Blocks(6, 6, 16);
                case TextureFormat.ASTC_RGB_8x8:
                case TextureFormat.ASTC_RGBA_8x8:
                case TextureFormat.ASTC_HDR_8x8:
                    return Blocks(8, 8, 16);
                case TextureFormat.ASTC_RGB_10x10:
                case TextureFormat.ASTC_RGBA_10x10:
                case TextureFormat.ASTC_HDR_10x10:
                    return Blocks(10, 10, 16);
                case TextureFormat.ASTC_RGB_12x12:
                case TextureFormat.ASTC_RGBA_12x12:
                case TextureFormat.ASTC_HDR_12x12:
                    return Blocks(12, 12, 16);
                case TextureFormat.PVRTC_RGB2:
                case TextureFormat.PVRTC_RGBA2:
                    return Math.Max(width, 16) * Math.Max(height, 8) / 4;
                case TextureFormat.PVRTC_RGB4:
                case TextureFormat.PVRTC_RGBA4:
                    return Math.Max(width, 8) * Math.Max(height, 8) / 2;
                default:
                    return -1;
            }
        }
    }
}
