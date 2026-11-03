// Application / tray icon.
//
// The product is "two cursors", so the mark is two overlapping pointers: the rear one
// faded, the front one solid. Drawn as vector paths and rasterised per size, because a
// single 32x32 bitmap looks ragged once Windows asks for 20px or 40px.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace CursorMirror
{
    internal static class IconFactory
    {
        // Sizes the tray needs. 32bpp DIB frames are what System.Drawing.Icon can read,
        // and they are bulky, so this list stays short: together they cost ~26 KB.
        private static readonly int[] TraySizes = new int[] { 16, 20, 24, 32, 40, 48 };

        // The embedded .ico additionally carries a 256px frame for Explorer's extra-large
        // view. That one is stored as PNG: as a DIB it would be 270 KB on its own, which is
        // 71% of the whole icon file. The shell reads PNG-in-ICO natively since Vista.
        private static readonly int[] FileSizes = new int[] { 16, 20, 24, 32, 40, 48, 256 };

        private const int PngThreshold = 128;

        public static Bitmap Render(int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float pad = size * 0.02f;
                RectangleF tile = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);

                // Rounded tile with a soft vertical gradient.
                using (GraphicsPath tp = Rounded(tile, size * 0.235f))
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new RectangleF(tile.X, tile.Y - 1, tile.Width, tile.Height + 2),
                    Color.FromArgb(0x3A, 0x9B, 0xF5),
                    Color.FromArgb(0x0B, 0x5C, 0xC4),
                    LinearGradientMode.Vertical))
                {
                    g.FillPath(bg, tp);
                }

                // At 16-20px a second pointer just becomes mud; show one clean arrow.
                bool dual = size >= 24;

                if (dual)
                {
                    // Rear cursor: offset up-right, translucent so the pair reads as
                    // "two pointers, one lagging behind".
                    DrawPointer(g, size, 0.50f, 0.44f, 0.19f, Color.FromArgb(110, 255, 255, 255));
                    // Front cursor: solid white, the hero of the mark.
                    DrawPointer(g, size, 0.60f, 0.15f, 0.30f, Color.White);
                }
                else
                {
                    DrawPointer(g, size, 0.70f, 0.15f, 0.15f, Color.White);
                }
            }

            return bmp;
        }

        // Draws the pointer at (x,y) with the given height fraction of the icon.
        private static void DrawPointer(Graphics g, int size, float heightFrac, float x, float y, Color color)
        {
            float h = size * heightFrac;
            float w = h * 0.72f;
            RectangleF box = new RectangleF(size * x, size * y, w, h);

            PointF[] unit = new PointF[]
            {
                new PointF(0.00f, 0.00f),
                new PointF(0.00f, 0.80f),
                new PointF(0.19f, 0.60f),
                new PointF(0.33f, 0.94f),
                new PointF(0.49f, 0.87f),
                new PointF(0.35f, 0.54f),
                new PointF(0.60f, 0.51f)
            };

            PointF[] pts = new PointF[unit.Length];
            for (int i = 0; i < unit.Length; i++)
            {
                pts[i] = new PointF(box.Left + unit[i].X * box.Width,
                                    box.Top + unit[i].Y * box.Height);
            }

            using (SolidBrush b = new SolidBrush(color))
            {
                g.FillPolygon(b, pts);
            }
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > Math.Min(r.Width, r.Height)) d = Math.Min(r.Width, r.Height);
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // In-memory icon for the tray. DIB frames only: System.Drawing.Icon cannot decode
        // PNG-compressed frames and would render garbage.
        public static byte[] BuildIco()
        {
            return Build(TraySizes);
        }

        // .ico file for --makeicon and for embedding via /win32icon.
        public static byte[] BuildFileIco()
        {
            return Build(FileSizes);
        }

        private static byte[] Build(int[] sizes)
        {
            List<byte[]> frames = new List<byte[]>();
            for (int i = 0; i < sizes.Length; i++)
            {
                frames.Add(sizes[i] >= PngThreshold ? BuildPngFrame(sizes[i]) : BuildDibFrame(sizes[i]));
            }

            using (MemoryStream ico = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ico))
            {
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // type: icon
                w.Write((ushort)sizes.Length);

                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int s = sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);            // palette
                    w.Write((byte)0);            // reserved
                    w.Write((ushort)1);          // planes
                    w.Write((ushort)32);         // bpp
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }

                for (int i = 0; i < frames.Count; i++)
                {
                    w.Write(frames[i]);
                }

                w.Flush();
                return ico.ToArray();
            }
        }

        private static byte[] BuildPngFrame(int size)
        {
            using (Bitmap bmp = Render(size))
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // BITMAPINFOHEADER + bottom-up BGRA + 1bpp AND mask, the format .ico always accepts.
        private static byte[] BuildDibFrame(int size)
        {
            using (Bitmap bmp = Render(size))
            {
                byte[] pixels = new byte[size * size * 4];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        int di = ((size - 1 - y) * size + x) * 4;
                        // DIB stores non-premultiplied BGRA.
                        pixels[di + 0] = c.B;
                        pixels[di + 1] = c.G;
                        pixels[di + 2] = c.R;
                        pixels[di + 3] = c.A;
                    }
                }

                int maskStride = ((size + 31) / 32) * 4;
                byte[] mask = new byte[maskStride * size];

                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(40);                 // biSize
                    w.Write(size);               // biWidth
                    w.Write(size * 2);           // biHeight (colour + mask)
                    w.Write((ushort)1);          // biPlanes
                    w.Write((ushort)32);         // biBitCount
                    w.Write(0);                  // biCompression = BI_RGB
                    w.Write(pixels.Length + mask.Length);
                    w.Write(0);                  // biXPelsPerMeter
                    w.Write(0);                  // biYPelsPerMeter
                    w.Write(0);                  // biClrUsed
                    w.Write(0);                  // biClrImportant
                    w.Write(pixels);
                    w.Write(mask);
                    w.Flush();
                    return ms.ToArray();
                }
            }
        }

        public static Icon Create()
        {
            using (MemoryStream ms = new MemoryStream(BuildIco()))
            {
                return new Icon(ms);
            }
        }

        public static void SaveIco(string path)
        {
            File.WriteAllBytes(path, BuildFileIco());
        }
    }
}
