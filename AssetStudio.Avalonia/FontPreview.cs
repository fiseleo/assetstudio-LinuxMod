using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.IO;

namespace AssetStudio.Avalonia
{
    /// <summary>
    /// Renders a font sample sheet with SixLabors.Fonts (no system font registration needed).
    /// </summary>
    public static class FontPreview
    {
        private const string Sample = "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ\n1234567890.:,;'\"(!?)+-*/=";
        private const string Pangram = "The quick brown fox jumps over the lazy dog. 1234567890";

        public static (byte[] bgra, int width, int height, string familyName) Render(byte[] fontData)
        {
            var collection = new FontCollection();
            FontFamily family;
            using (var stream = new MemoryStream(fontData))
            {
                family = collection.Add(stream);
            }

            var lines = new (string text, float size)[]
            {
                (Sample, 16), (Pangram, 12), (Pangram, 18), (Pangram, 24), (Pangram, 36), (Pangram, 48), (Pangram, 60), (Pangram, 72),
            };

            const float margin = 12;
            float width = 0, height = margin;
            var measured = new FontRectangle[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                var font = family.CreateFont(lines[i].size);
                measured[i] = TextMeasurer.Measure(lines[i].text, new TextOptions(font));
                width = Math.Max(width, measured[i].Width);
                height += measured[i].Height + lines[i].size * 0.4f;
            }
            var imageWidth = (int)Math.Min(4096, Math.Ceiling(width + margin * 2));
            var imageHeight = (int)Math.Min(4096, Math.Ceiling(height + margin));

            using var image = new Image<Bgra32>(imageWidth, imageHeight, SixLabors.ImageSharp.Color.White);
            image.Mutate(ctx =>
            {
                var y = margin;
                for (int i = 0; i < lines.Length; i++)
                {
                    var font = family.CreateFont(lines[i].size);
                    ctx.DrawText(lines[i].text, font, SixLabors.ImageSharp.Color.Black, new PointF(margin, y));
                    y += measured[i].Height + lines[i].size * 0.4f;
                }
            });
            var bytes = new byte[imageWidth * imageHeight * 4];
            image.CopyPixelDataTo(bytes);
            return (bytes, imageWidth, imageHeight, family.Name);
        }
    }
}
