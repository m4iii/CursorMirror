// Win11-style flyout menu for CursorMirror.
//
// WinForms ContextMenuStrip renders with the classic 9x theme, which looks dated next
// to Windows 11. This is a borderless form painted with GDI+ using the Win11 palette,
// DWM rounded corners, a real switch control and a 1..15 size slider.
//
// All UI strings are written as \uXXXX escapes so the file stays pure ASCII and cannot
// be mangled by whatever codepage a build script happens to save it in.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CursorMirror
{
    internal sealed class MenuModel
    {
        public Func<bool> GetEnabled;
        public Action<bool> SetEnabled;

        // 1..15 mirrors the Windows "change pointer size" slider; 0 means follow the system.
        public Func<int> GetLevel;
        public Action<int> SetLevel;

        public Func<bool> GetFlipH;
        public Action<bool> SetFlipH;

        public Func<bool> GetOnlyMstsc;
        public Action<bool> SetOnlyMstsc;

        public Action OnExit;
        public Action OnCommit;
    }

    internal sealed class Theme
    {
        public bool Dark;
        public Color Background;
        public Color Foreground;
        public Color Subtle;
        public Color Hover;
        public Color Separator;
        public Color Track;
        public Color Accent;

        public static Theme Current()
        {
            Theme t = new Theme();
            t.Dark = IsDark();

            if (t.Dark)
            {
                t.Background = Color.FromArgb(43, 43, 43);
                t.Foreground = Color.FromArgb(255, 255, 255);
                t.Subtle = Color.FromArgb(160, 160, 160);
                t.Hover = Color.FromArgb(58, 58, 58);
                t.Separator = Color.FromArgb(63, 63, 63);
                t.Track = Color.FromArgb(74, 74, 74);
                t.Accent = Color.FromArgb(76, 194, 255);
            }
            else
            {
                t.Background = Color.FromArgb(243, 243, 243);
                t.Foreground = Color.FromArgb(26, 26, 26);
                t.Subtle = Color.FromArgb(96, 96, 96);
                t.Hover = Color.FromArgb(229, 229, 229);
                t.Separator = Color.FromArgb(222, 222, 222);
                t.Track = Color.FromArgb(214, 214, 214);
                t.Accent = Color.FromArgb(0, 103, 192);
            }

            return t;
        }

        private static bool IsDark()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
            }
            catch
            {
            }
            return false;
        }
    }

    internal sealed class ModernMenu : Form
    {
        private const int LEVELS = 15;

        // Logical (96-DPI) metrics. Every value is multiplied by the monitor scale factor
        // at construction, otherwise the flyout looks cramped on a 200% display because
        // GDI text scales with DPI while hard-coded pixel metrics do not.
        private const int LOGICAL_WIDTH = 336;
        private const int LOGICAL_PAD = 14;
        private const int LOGICAL_ROW_H = 40;
        private const int LOGICAL_HEADER_H = 50;
        private const int LOGICAL_SEP_H = 9;
        private const int LOGICAL_SLIDER_H = 68;
        private const int LOGICAL_BOTTOM_PAD = 8;

        private readonly int S;
        private readonly int WIDTH;
        private readonly int PAD;
        private readonly int ROW_H;
        private readonly int HEADER_H;
        private readonly int SEP_H;
        private readonly int SLIDER_H;
        private readonly int BOTTOM_PAD;

        private enum Kind { Header, Separator, Toggle, Slider, Choice, Action }

        private sealed class Row
        {
            public Kind Kind;
            public Rectangle Bounds;
            public string Text;
            public string Value;
            public bool Checked;
            public int Id;
        }

        private readonly MenuModel model;
        private readonly Theme theme;
        private readonly List<Row> rows = new List<Row>();
        private readonly Font font;
        private readonly Font fontSub;
        private readonly Font fontHeader;

        private int hover = -1;
        private bool dragging;
        private bool closing;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public ModernMenu(MenuModel model)
        {
            S = DpiScale();
            WIDTH = Scaled(LOGICAL_WIDTH);
            PAD = Scaled(LOGICAL_PAD);
            ROW_H = Scaled(LOGICAL_ROW_H);
            HEADER_H = Scaled(LOGICAL_HEADER_H);
            SEP_H = Scaled(LOGICAL_SEP_H);
            SLIDER_H = Scaled(LOGICAL_SLIDER_H);
            BOTTOM_PAD = Scaled(LOGICAL_BOTTOM_PAD);

            this.model = model;
            theme = Theme.Current();

            font = PickFont(10.0f, FontStyle.Regular);
            fontSub = PickFont(9.0f, FontStyle.Regular);
            fontHeader = PickFont(9.0f, FontStyle.Regular);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = theme.Background;
            Padding = Padding.Empty;
            Text = "CursorMirror";

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            BuildRows();
            ClientSize = new Size(WIDTH, HeightFor(rows));

            Deactivate += delegate { if (!dragging) CloseMenu(); };
            KeyPreview = true;
        }

        private int Scaled(int logical)
        {
            return (int)Math.Round((double)logical * S);
        }

        private static int DpiScale()
        {
            try
            {
                int dpi = Native.GetDpiForSystem();
                if (dpi > 0)
                {
                    int s = (int)Math.Round(dpi / 96.0);
                    return Math.Max(1, s);
                }
            }
            catch
            {
            }
            return 1;
        }

        private static Font PickFont(float size, FontStyle style)
        {
            string[] preferred = new string[] { "Segoe UI Variable Text", "Segoe UI" };
            InstalledFontCollection installed = new InstalledFontCollection();
            for (int i = 0; i < preferred.Length; i++)
            {
                for (int f = 0; f < installed.Families.Length; f++)
                {
                    if (string.Equals(installed.Families[f].Name, preferred[i], StringComparison.OrdinalIgnoreCase))
                    {
                        try { return new Font(preferred[i], size, style); }
                        catch { }
                    }
                }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2 (Win11 22000+)
            try
            {
                int pref = 2;
                DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
            }
            catch
            {
            }
        }

        private void BuildRows()
        {
            rows.Clear();

            // The header carries the master switch: on = the tool is active, off = the
            // user is not using it at all and the mirror stays hidden.
            Row header = new Row();
            header.Kind = Kind.Header;
            header.Id = 1;
            header.Text = "CursorMirror";
            header.Checked = model.GetEnabled();
            rows.Add(header);

            rows.Add(Separator());

            Row slider = new Row();
            slider.Kind = Kind.Slider;
            slider.Id = 2;
            slider.Text = "\u5149\u6807\u5927\u5c0f";
            rows.Add(slider);

            Row flip = new Row();
            flip.Kind = Kind.Toggle;
            flip.Id = 3;
            flip.Text = "\u6c34\u5e73\u955c\u50cf";
            flip.Checked = model.GetFlipH();
            rows.Add(flip);

            rows.Add(Separator());

            Row scope = new Row();
            scope.Kind = Kind.Choice;
            scope.Id = 4;
            scope.Text = "\u7ed8\u5236\u8303\u56f4";
            scope.Checked = model.GetOnlyMstsc();
            scope.Value = scope.Checked ? "\u4ec5 mstsc \u7a97\u53e3" : "\u6240\u6709\u7a97\u53e3";
            rows.Add(scope);

            rows.Add(Separator());

            Row quit = new Row();
            quit.Kind = Kind.Action;
            quit.Id = 5;
            quit.Text = "\u9000\u51fa";
            rows.Add(quit);
        }

        private static Row Separator()
        {
            Row r = new Row();
            r.Kind = Kind.Separator;
            return r;
        }

        private int HeightFor(List<Row> list)
        {
            int h = 0;
            for (int i = 0; i < list.Count; i++)
            {
                h += RowHeight(list[i].Kind);
            }
            return h + BOTTOM_PAD;
        }

        private int RowHeight(Kind kind)
        {
            switch (kind)
            {
                case Kind.Header: return HEADER_H;
                case Kind.Separator: return SEP_H;
                case Kind.Slider: return SLIDER_H;
                default: return ROW_H;
            }
        }

        private void LayoutRows()
        {
            int y = 0;
            int w = ClientSize.Width;

            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                int h = RowHeight(r.Kind);

                if (r.Kind == Kind.Header || r.Kind == Kind.Separator)
                {
                    r.Bounds = new Rectangle(0, y, w, h);
                }
                else
                {
                    r.Bounds = new Rectangle(PAD - 6, y, w - (PAD - 6) * 2, h);
                }

                y += h;
            }
        }

        private Rectangle TrackRect(Row r)
        {
            int left = r.Bounds.Left + Scaled(6);
            int right = r.Bounds.Right - Scaled(6);
            int thumb = Scaled(18);
            return new Rectangle(left, r.Bounds.Top + Scaled(36), right - left, thumb);
        }

        private int LevelAt(int x, Rectangle track)
        {
            int thumb = track.Height;
            int inner = track.Width - thumb;
            if (inner <= 0) return 1;
            double t = (double)(x - track.Left - thumb / 2) / inner;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return 1 + (int)Math.Round(t * (LEVELS - 1));
        }

        public void ShowAt(Point screen)
        {
            BuildRows();
            ClientSize = new Size(WIDTH, HeightFor(rows));
            LayoutRows();

            Rectangle screenRect = Screen.FromPoint(screen).WorkingArea;
            int x = screen.X;
            int y = screen.Y;

            if (x + Width > screenRect.Right) x = screenRect.Right - Width;
            if (y + Height > screenRect.Bottom) y = screen.Y - Height;
            if (y < screenRect.Top) y = screenRect.Top;
            if (x < screenRect.Left) x = screenRect.Left;

            // Round the actual window region so the flyout looks right on Windows 10 too,
            // where DWMWA_WINDOW_CORNER_PREFERENCE is not honoured.
            using (GraphicsPath shape = Rounded(new Rectangle(0, 0, Width, Height), Scaled(8)))
            {
                Region old = Region;
                Region = new Region(shape);
                if (old != null) old.Dispose();
            }

            Location = new Point(x, y);
            Show();
            SetForegroundWindow(Handle);
            Invalidate();
        }

        private void CloseMenu()
        {
            if (closing) return;
            closing = true;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (SolidBrush bg = new SolidBrush(theme.Background))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            LayoutRows();

            for (int i = 0; i < rows.Count; i++)
            {
                DrawRow(g, rows[i], i == hover);
            }

            // Hairline border, the way Win11 flyouts separate from the backdrop.
            using (GraphicsPath edge = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Scaled(8)))
            using (Pen p = new Pen(theme.Separator))
            {
                g.DrawPath(p, edge);
            }
        }

        private void DrawRow(Graphics g, Row r, bool hot)
        {
            // Everything below the header is inert while the master switch is off.
            bool dim = r.Kind != Kind.Header && r.Kind != Kind.Action && !model.GetEnabled();

            switch (r.Kind)
            {
                case Kind.Header:
                    {
                        Rectangle sw = HeaderSwitch(r);
                        int gap = Scaled(12);
                        int titleW = Scaled(118);

                        TextRenderer.DrawText(g, r.Text, fontHeader,
                            new Rectangle(PAD, 0, titleW, HEADER_H),
                            theme.Foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                        // Sub-label states plainly whether the tool is in use, right-aligned
                        // in the space between the title and the switch.
                        string state = r.Checked ? "\u5df2\u542f\u7528" : "\u5df2\u505c\u7528";
                        int stateLeft = PAD + titleW;
                        Rectangle stateRect = new Rectangle(stateLeft, 0, sw.Left - gap - stateLeft, HEADER_H);
                        TextRenderer.DrawText(g, state, fontSub, stateRect,
                            r.Checked ? theme.Subtle : theme.Accent,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                        DrawSwitch(g, sw, r.Checked);
                    }
                    break;

                case Kind.Separator:
                    using (Pen p = new Pen(theme.Separator))
                    {
                        int y = r.Bounds.Top + SEP_H / 2;
                        g.DrawLine(p, PAD, y, ClientSize.Width - PAD, y);
                    }
                    break;

                case Kind.Toggle:
                    if (hot) DrawHover(g, r.Bounds);
                    {
                        int sw = Scaled(40);
                        int sh = Scaled(20);
                        TextRenderer.DrawText(g, r.Text, font,
                            new Rectangle(r.Bounds.Left + Scaled(8), r.Bounds.Top, r.Bounds.Width - sw - Scaled(28), r.Bounds.Height),
                            dim ? Dim(theme.Foreground) : theme.Foreground,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        DrawSwitch(g, new Rectangle(r.Bounds.Right - sw - Scaled(12), r.Bounds.Top + (r.Bounds.Height - sh) / 2, sw, sh), r.Checked, dim);
                    }
                    break;

                case Kind.Choice:
                    if (hot) DrawHover(g, r.Bounds);
                    {
                        int w = r.Bounds.Width;
                        TextRenderer.DrawText(g, r.Text, font,
                            new Rectangle(r.Bounds.Left + Scaled(8), r.Bounds.Top, w / 2 - Scaled(8), r.Bounds.Height),
                            dim ? Dim(theme.Foreground) : theme.Foreground,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        TextRenderer.DrawText(g, r.Value, fontSub,
                            new Rectangle(r.Bounds.Left + w / 2, r.Bounds.Top, w / 2 - Scaled(10), r.Bounds.Height),
                            Dim(theme.Subtle), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                    break;

                case Kind.Action:
                    if (hot) DrawHover(g, r.Bounds);
                    TextRenderer.DrawText(g, r.Text, font,
                        new Rectangle(r.Bounds.Left + Scaled(8), r.Bounds.Top, r.Bounds.Width - Scaled(16), r.Bounds.Height),
                        theme.Foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    break;

                case Kind.Slider:
                    {
                        int labelH = Scaled(34);
                        TextRenderer.DrawText(g, r.Text, font,
                            new Rectangle(r.Bounds.Left + Scaled(8), r.Bounds.Top, Scaled(160), labelH),
                            dim ? Dim(theme.Foreground) : theme.Foreground,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                        // Just the number, matching the Windows slider's step count.
                        int level = model.GetLevel();
                        int effective = level <= 0 ? NearestSystemLevel() : level;
                        string value = effective.ToString();
                        TextRenderer.DrawText(g, value, fontSub,
                            new Rectangle(r.Bounds.Right - Scaled(180), r.Bounds.Top, Scaled(172), labelH),
                            Dim(theme.Subtle), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                        DrawSlider(g, r, effective, dim);
                    }
                    break;
            }
        }

        // Header switch rectangle, shared by painting and hit testing.
        private Rectangle HeaderSwitch(Row r)
        {
            int sw = Scaled(40);
            int sh = Scaled(20);
            return new Rectangle(r.Bounds.Right - PAD - sw, (HEADER_H - sh) / 2, sw, sh);
        }

        // Blends a colour toward the background so inactive rows read as disabled.
        private Color Dim(Color c)
        {
            if (model.GetEnabled()) return c;
            return Blend(c, theme.Background, 0.62);
        }

        private static Color Blend(Color fore, Color back, double amount)
        {
            int a = (int)Math.Round(fore.A + (back.A - fore.A) * amount);
            int r = (int)Math.Round(fore.R + (back.R - fore.R) * amount);
            int g = (int)Math.Round(fore.G + (back.G - fore.G) * amount);
            int b = (int)Math.Round(fore.B + (back.B - fore.B) * amount);
            return Color.FromArgb(a, r, g, b);
        }

        private void DrawHover(Graphics g, Rectangle r)
        {
            Rectangle rr = new Rectangle(r.Left, r.Top + Scaled(2), r.Width, r.Height - Scaled(4));
            using (GraphicsPath path = Rounded(rr, Scaled(6)))
            using (SolidBrush b = new SolidBrush(theme.Hover))
            {
                g.FillPath(b, path);
            }
        }

        private void DrawSwitch(Graphics g, Rectangle r, bool on)
        {
            DrawSwitch(g, r, on, false);
        }

        private void DrawSwitch(Graphics g, Rectangle r, bool on, bool dim)
        {
            using (GraphicsPath path = Rounded(r, r.Height / 2))
            using (SolidBrush b = new SolidBrush(dim ? Dim(on ? theme.Accent : theme.Track)
                                                     : (on ? theme.Accent : theme.Track)))
            {
                g.FillPath(b, path);
            }

            int inset = Scaled(3);
            int knob = r.Height - inset * 2;
            int kx = on ? r.Right - knob - inset : r.Left + inset;
            using (SolidBrush kb = new SolidBrush(dim ? Dim(Color.White) : Color.White))
            {
                g.FillEllipse(kb, new Rectangle(kx, r.Top + inset, knob, knob));
            }
        }

        private void DrawSlider(Graphics g, Row r, int level, bool dim)
        {
            Rectangle track = TrackRect(r);
            int cy = track.Top + track.Height / 2;

            Rectangle bar = new Rectangle(track.Left, cy - Scaled(2), track.Width, Scaled(4));
            using (GraphicsPath path = Rounded(bar, 2))
            using (SolidBrush b = new SolidBrush(theme.Track))
            {
                g.FillPath(b, path);
            }

            int thumbSize = track.Height;
            int inner = track.Width - thumbSize;
            int knobX = track.Left + thumbSize / 2 + (int)Math.Round((double)(level - 1) / (LEVELS - 1) * inner);

            if (knobX > track.Left + thumbSize / 2)
            {
                Rectangle filled = new Rectangle(track.Left, cy - Scaled(2), knobX - track.Left, Scaled(4));
                using (GraphicsPath path = Rounded(filled, 2))
                using (SolidBrush b = new SolidBrush(theme.Accent))
                {
                    g.FillPath(b, path);
                }
            }

            int r1 = Scaled(9);
            using (SolidBrush b = new SolidBrush(theme.Accent))
            {
                g.FillEllipse(b, new Rectangle(knobX - r1, cy - r1, r1 * 2, r1 * 2));
            }
            int r2 = Scaled(4);
            using (SolidBrush b = new SolidBrush(Color.White))
            {
                g.FillEllipse(b, new Rectangle(knobX - r2, cy - r2, r2 * 2, r2 * 2));
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            int limit = Math.Min(r.Width, r.Height);
            if (d <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            // A pill has radius == height/2, so clamp instead of falling back to a
            // rectangle. The old <= test made every switch render as a plain box.
            if (d > limit) d = limit;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private int NearestSystemLevel()
        {
            int px = Native.GetSystemMetrics(Native.SM_CXCURSOR);
            int best = 1;
            int bestDiff = int.MaxValue;
            for (int i = 1; i <= LEVELS; i++)
            {
                int diff = Math.Abs(LevelPixels(i) - px);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }

        internal static int LevelPixels(int level)
        {
            int logical = SizeLadder.LevelSize(level);
            double scale = SystemScale();
            int px = (int)Math.Round(logical * scale);
            return Math.Max(8, Math.Min(512, px));
        }

        private static double SystemScale()
        {
            try
            {
                int dpi = Native.GetDpiForSystem();
                if (dpi > 0) return dpi / 96.0;
            }
            catch
            {
            }
            return 1.0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (dragging)
            {
                Row s = FindRow(Kind.Slider);
                if (s != null)
                {
                    int level = LevelAt(e.X, TrackRect(s));
                    if (level != model.GetLevel())
                    {
                        model.SetLevel(level);
                        Invalidate();
                    }
                }
                return;
            }

            int idx = RowAt(e.Location);
            if (idx != hover)
            {
                hover = idx;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            int idx = RowAt(e.Location);
            if (idx < 0) return;

            Row r = rows[idx];

            if (r.Kind == Kind.Header)
            {
                if (HeaderSwitch(r).Contains(e.Location))
                {
                    model.SetEnabled(!model.GetEnabled());
                    Commit();
                }
                return;
            }

            // While the master switch is off nothing else reacts.
            if (!model.GetEnabled()) return;

            switch (r.Kind)
            {
                case Kind.Toggle:
                    model.SetFlipH(!model.GetFlipH());
                    Commit();
                    break;

                case Kind.Choice:
                    model.SetOnlyMstsc(!model.GetOnlyMstsc());
                    Commit();
                    break;

                case Kind.Action:
                    model.OnExit();
                    CloseMenu();
                    break;

                case Kind.Slider:
                    dragging = true;
                    Capture = true;
                    model.SetLevel(LevelAt(e.X, TrackRect(r)));
                    Commit();
                    break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false;
                Capture = false;
                Commit();
            }
        }

        private void Commit()
        {
            BuildRows();
            Invalidate();
            if (model.OnCommit != null) model.OnCommit();
        }

        private int RowAt(Point p)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.Kind == Kind.Separator) continue;
                if (r.Kind == Kind.Header)
                {
                    if (HeaderSwitch(r).Contains(p)) return i;
                    continue;
                }
                if (!model.GetEnabled() && r.Kind != Kind.Action) continue;
                if (r.Bounds.Contains(p)) return i;
            }
            return -1;
        }

        private Row FindRow(Kind kind)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind == kind) return rows[i];
            }
            return null;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) CloseMenu();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                CloseMenu();
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }
    }

    // The 15 steps Windows offers in Settings > Accessibility > Mouse pointer size.
    internal static class SizeLadder
    {
        private static readonly int[] Sizes = new int[]
        {
            32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120, 128, 144, 160
        };

        public static int Count { get { return Sizes.Length; } }

        public static int LevelSize(int level)
        {
            if (level < 1) level = 1;
            if (level > Sizes.Length) level = Sizes.Length;
            return Sizes[level - 1];
        }
    }
}