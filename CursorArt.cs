// The cursor shapes Windows itself uses, as hard-coded polygon data.
//
// Source: the vector artwork Microsoft ships in C:\Windows\Cursors\*.svg (viewBox 0 0 32 32).
// The coordinates there use a Y-UP axis, so every shape is flipped vertically once at
// startup; after that everything is plain screen space (Y down).
//
// One entry point: Render(kind, size) -> premultiplied BGRA pixels + hotspot.
// No file access, no XML parsing, no upscaling of a 64px bitmap.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CursorMirror
{
    internal enum CursorKind
    {
        Unknown,
        Arrow,
        IBeam,
        Cross,
        SizeNS,
        SizeWE,
        SizeAll,
        UpArrow,
        Hand,
        No
    }

    internal sealed class VectorCursor
    {
        public int Width;
        public int Height;
        public int HotspotX;
        public int HotspotY;
        public byte[] Pixels;   // premultiplied BGRA
    }

    internal static class CursorArt
    {
        // Authored coordinate space of the Windows artwork.
        private const float Authored = 32f;

        private sealed class Shape
        {
            public PointF[][] Loops;
            public Color Fill = Color.FromArgb(255, 255, 254);
            public Color Stroke = Color.FromArgb(0, 0, 1);
            public float StrokeWidth = 1f;
            public bool Filled = true;
        }

        // X,Y pairs straight out of the Microsoft SVGs. Hotspots are the real ones from the
        // matching .cur files (all Aero cursors are 0,0 except the text caret and friends).
        private static readonly Dictionary<CursorKind, Shape> Shapes = Build();

        private static Dictionary<CursorKind, Shape> Build()
        {
            Dictionary<CursorKind, Shape> d = new Dictionary<CursorKind, Shape>();

            // arrow.svg
            d[CursorKind.Arrow] = Poly(
                new float[]
                {
                    12.73606f, 19.04075f,
                    7.80159f, 19.04075f,
                    9.82813f, 14.91828f,
                    6.88341f, 13.42515f,
                    4.62301f, 18.05872f,
                    1.26394f, 14.78295f,
                    1.26394f, 30.57485f
                });

            // ibeam.svg
            d[CursorKind.IBeam] = Poly(
                new float[]
                {
                    21f, 9.5f, 21f, 7.5f, 17f, 7.5f, 17f, 8f, 15f, 8f,
                    15f, 7.5f, 11f, 7.5f, 11f, 9.5f, 15f, 9.5f, 15f, 22.5f,
                    11f, 22.5f, 11f, 24.5f, 15f, 24.5f, 15f, 24f, 17f, 24f,
                    17f, 24.5f, 21f, 24.5f, 21f, 22.5f, 17f, 22.5f, 17f, 9.5f
                });

            // cross.svg
            d[CursorKind.Cross] = Poly(
                new float[]
                {
                    15f, 15f, 15f, 7f, 17f, 7f, 17f, 15f, 25f, 15f, 25f, 17f,
                    17f, 17f, 17f, 25f, 15f, 25f, 15f, 17f, 7f, 17f, 7f, 15f
                });

            // up.svg
            d[CursorKind.UpArrow] = Poly(
                new float[]
                {
                    4.64286f, 25.73214f, 4.64286f, 12.83929f, 7.35714f, 12.83929f,
                    7.35714f, 25.73214f, 10.75f, 25.73214f, 10.75f, 26.41072f,
                    6f, 31.16072f, 1.25f, 26.41072f, 1.25f, 25.73214f
                });

            // ns.svg (stroke-width is slightly under 1 in the original)
            Shape ns = Poly(
                new float[]
                {
                    3.71429f, 14.21429f, 0.5f, 14.21429f, 0.5f, 13.57143f,
                    5f, 9.07143f, 9.5f, 13.57143f, 9.5f, 14.21429f,
                    6.28571f, 14.21429f, 6.28571f, 25.78572f, 9.5f, 25.78572f,
                    9.5f, 26.42857f, 5f, 30.92857f, 0.5f, 26.42857f,
                    0.5f, 25.78572f, 3.71429f, 25.78572f
                });
            ns.StrokeWidth = 0.99351f;
            d[CursorKind.SizeNS] = ns;

            // ew.svg
            d[CursorKind.SizeWE] = Poly(
                new float[]
                {
                    6.04412f, 27.32353f, 17.95588f, 27.32353f, 17.95588f, 30.63235f,
                    18.61765f, 30.63235f, 23.25f, 26f, 18.61765f, 21.36765f,
                    17.95588f, 21.36765f, 17.95588f, 24.67647f, 6.04412f, 24.67647f,
                    6.04412f, 21.36765f, 5.38235f, 21.36765f, 0.75f, 26f,
                    5.38235f, 30.63235f, 6.04412f, 30.63235f
                });

            // move.svg
            d[CursorKind.SizeAll] = Poly(
                new float[]
                {
                    6.04412f, 21.32353f, 6.04412f, 24.63235f, 5.38235f, 24.63235f,
                    0.75f, 20f, 5.38235f, 15.36765f, 6.04412f, 15.36765f,
                    6.04412f, 18.67647f, 10.67647f, 18.67647f, 10.67647f, 14.04412f,
                    7.36765f, 14.04412f, 7.36765f, 13.38235f, 12f, 8.75f,
                    16.63235f, 13.38235f, 16.63235f, 14.04412f, 13.32353f, 14.04412f,
                    13.32353f, 18.67647f, 17.95588f, 18.67647f, 17.95588f, 15.36765f,
                    18.61765f, 15.36765f, 23.25f, 20f, 18.61765f, 24.63235f,
                    17.95588f, 24.63235f, 17.95588f, 21.32353f, 13.32353f, 21.32353f,
                    13.32353f, 25.95588f, 16.63235f, 25.95588f, 16.63235f, 26.61765f,
                    12f, 31.25f, 7.36765f, 26.61765f, 7.36765f, 25.95588f,
                    10.67647f, 25.95588f, 10.67647f, 21.32353f
                });

            // unavail.svg - the classic "no" sign: a ring with a diagonal bar. Built from a
            // generated ellipse rather than parsed from the original bezier path.
            d[CursorKind.No] = NoSign();

            // link.svg - hand cursor. Its artwork is a stack of open bezier strokes, which
            // needs a full SVG path parser; skipped on purpose so it falls back to the
            // bitmap path instead of rendering something wrong.

            // Flip Y once: the source artwork is authored Y-up.
            foreach (KeyValuePair<CursorKind, Shape> kv in d)
            {
                PointF[][] loops = kv.Value.Loops;
                for (int i = 0; i < loops.Length; i++)
                {
                    for (int j = 0; j < loops[i].Length; j++)
                    {
                        loops[i][j] = new PointF(loops[i][j].X, Authored - loops[i][j].Y);
                    }
                }
            }

            return d;
        }

        private static Shape Poly(float[] xy)
        {
            PointF[] pts = new PointF[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = new PointF(xy[i * 2], xy[i * 2 + 1]);
            }

            Shape s = new Shape();
            s.Loops = new PointF[][] { pts };
            return s;
        }

        // Circle outline plus a 45-degree bar, matching unavail.svg's geometry
        // (centre 9,23 radius 8 in authored space).
        private static Shape NoSign()
        {
            const float cx = 9f, cy = 23f, r = 8f;
            const int steps = 48;

            PointF[] ring = new PointF[steps];
            for (int i = 0; i < steps; i++)
            {
                double a = i * Math.PI * 2 / steps;
                ring[i] = new PointF((float)(cx + Math.Cos(a) * r), (float)(cy + Math.Sin(a) * r));
            }

            // Bar across the ring at 45 degrees, inset so it stops at the outline.
            float k = r * 0.7071f;
            PointF[] bar = new PointF[]
            {
                new PointF(cx - k, cy - k),
                new PointF(cx + k, cy + k),
                new PointF(cx + k, cy + k),
                new PointF(cx - k, cy - k)
            };

            Shape s = new Shape();
            s.Loops = new PointF[][] { ring, bar };
            s.Filled = false;          // outline only
            return s;
        }

        public static bool HasShape(CursorKind kind)
        {
            return Shapes.ContainsKey(kind);
        }

        // Hotspot in canvas space, derived from where the real cursor's tip sits.
        // Arrow and UpArrow anchor on their tip (the point that must land on the pointer);
        // symmetric shapes anchor on the glyph centre, which is what Windows uses.
        private static PointF Anchor(CursorKind kind, Shape shape, float scale)
        {
            RectangleF b = Bounds(shape);

            switch (kind)
            {
                case CursorKind.Arrow:
                    return new PointF(b.Left * scale, b.Top * scale);
                case CursorKind.UpArrow:
                    return new PointF((b.Left + b.Width / 2f) * scale, b.Top * scale);
                default:
                    return new PointF((b.Left + b.Width / 2f) * scale, (b.Top + b.Height / 2f) * scale);
            }
        }

        // THE function: kind + size in, premultiplied bitmap out.
        public static VectorCursor Render(CursorKind kind, int size, Color? tint, int alphaPercent)
        {
            Shape shape;
            if (!Shapes.TryGetValue(kind, out shape)) return null;
            if (size < 2 || size > 1024) return null;

            // The SVG viewBox (0 0 32 32) IS the cursor canvas, so map it 1:1. Fitting the
            // glyph's bounding box to the square instead would draw it ~2.6x too big: the
            // real 128px arrow only covers 39% x 59% of its bitmap.
            float scale = size / Authored;

            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    using (GraphicsPath path = new GraphicsPath())
                    {
                        for (int l = 0; l < shape.Loops.Length; l++)
                        {
                            PointF[] src = shape.Loops[l];
                            if (src.Length < 2) continue;

                            PointF[] mapped = new PointF[src.Length];
                            for (int i = 0; i < src.Length; i++)
                            {
                                mapped[i] = new PointF(
                                    src[i].X * scale,
                                    src[i].Y * scale);
                            }

                            if (src.Length == 2 && !shape.Filled)
                            {
                                path.AddLine(mapped[0], mapped[1]);
                            }
                            else
                            {
                                path.AddPolygon(mapped);
                            }
                        }

                        float pen = Math.Max(1f, size * (shape.StrokeWidth / Authored));
                        using (Pen p = new Pen(shape.Stroke, pen))
                        {
                            p.LineJoin = LineJoin.Miter;
                            p.MiterLimit = 10f;
                            g.DrawPath(p, path);
                        }

                        if (shape.Filled)
                        {
                            using (SolidBrush brush = new SolidBrush(shape.Fill))
                            {
                                g.FillPath(brush, path);
                            }
                        }
                    }
                }

                VectorCursor result = new VectorCursor();
                result.Width = size;
                result.Height = size;
                PointF anchor = Anchor(kind, shape, scale);
                result.HotspotX = Math.Max(0, Math.Min(size - 1, (int)Math.Round(anchor.X)));
                result.HotspotY = Math.Max(0, Math.Min(size - 1, (int)Math.Round(anchor.Y)));
                result.Pixels = ToPremultiplied(bmp, tint, alphaPercent);
                return result;
            }
        }

        private static RectangleF Bounds(Shape shape)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            for (int l = 0; l < shape.Loops.Length; l++)
            {
                PointF[] pts = shape.Loops[l];
                for (int i = 0; i < pts.Length; i++)
                {
                    if (pts[i].X < minX) minX = pts[i].X;
                    if (pts[i].Y < minY) minY = pts[i].Y;
                    if (pts[i].X > maxX) maxX = pts[i].X;
                    if (pts[i].Y > maxY) maxY = pts[i].Y;
                }
            }
            return new RectangleF(minX, minY, maxX - minX, maxY - minY);
        }

        private static byte[] ToPremultiplied(Bitmap bmp, Color? tint, int alphaPercent)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            byte[] dst = new byte[w * h * 4];

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] src = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride),
                        src, y * w * 4, w * 4);
                }

                for (int i = 0; i < src.Length; i += 4)
                {
                    int a = src[i + 3];
                    if (alphaPercent < 100) a = a * alphaPercent / 100;
                    if (a == 0) continue;

                    int b = src[i + 0];
                    int g = src[i + 1];
                    int r = src[i + 2];

                    if (tint.HasValue)
                    {
                        r = tint.Value.R;
                        g = tint.Value.G;
                        b = tint.Value.B;
                    }

                    dst[i + 0] = (byte)(b * a / 255);
                    dst[i + 1] = (byte)(g * a / 255);
                    dst[i + 2] = (byte)(r * a / 255);
                    dst[i + 3] = (byte)a;
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return dst;
        }
    }
}
