// CursorMirror - draw a second (local) mouse cursor on top of an mstsc session window.
//
// Why: mstsc hides the OS cursor inside the session area and paints the server-driven
// remote cursor itself. This tool re-paints a copy of the cursor at the *local* pointer
// position, so the fast local one and the delayed remote one are both visible.
//
// Design notes:
//  - layered window + UpdateLayeredWindow(ULW_ALPHA): real per-pixel alpha, no color key.
//  - cursor bitmap extracted from GetCursorInfo/CopyIcon/GetIconInfo (32bpp + AND mask,
//    monochrome AND/XOR reconstruction).
//  - click-through, no activation, tool window, topmost.
//  - gated on the foreground process being mstsc/msrdc and the pointer inside its client area.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CursorMirror
{
    internal static class Native
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;

        public const uint WS_POPUP = 0x80000000u;
        public const uint WS_EX_LAYERED = 0x00080000u;
        public const uint WS_EX_TRANSPARENT = 0x00000020u;
        public const uint WS_EX_NOACTIVATE = 0x08000000u;
        public const uint WS_EX_TOOLWINDOW = 0x00000080u;
        public const uint WS_EX_TOPMOST = 0x00000008u;

        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;

        public const int GA_ROOT = 2;
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int HTTRANSPARENT = -1;
        public const int MA_NOACTIVATE = 3;

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string name);

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int WM_DESTROY = 0x0002;
        public const int WM_TIMER = 0x0113;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_DPICHANGED = 0x02E0;

        // Tray icon (Shell_NotifyIcon). We drive this directly instead of using
        // WinForms' NotifyIcon: that class keeps its own hidden window and message
        // context, which does not play well with this app's raw Win32 message loop
        // (the icon silently never appeared in the shell).
        public const int WM_APP = 0x8000;
        public const int WM_CONTEXTMENU = 0x007B;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;

        public const int NIM_ADD = 0x00000000;
        public const int NIM_MODIFY = 0x00000001;
        public const int NIM_DELETE = 0x00000002;
        public const int NIM_SETVERSION = 0x00000004;

        public const int NIF_MESSAGE = 0x00000001;
        public const int NIF_ICON = 0x00000002;
        public const int NIF_TIP = 0x00000004;
        public const int NIF_SHOWTIP = 0x00000080;

        public const int NOTIFYICON_VERSION_4 = 4;

        // Callback events delivered in LOWORD(lParam) when version 4 is set.
        public const int NIN_SELECT = 0x0400;
        public const int NIN_KEYSELECT = 0x0401;
        public const int NIN_BALLOONUSERCLICK = 0x0405;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public Guid guidItem;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

        public const int CURSOR_SHOWING = 0x00000001;
        public const int IDC_ARROW = 32512;
        public const int IDC_IBEAM = 32513;
        public const int IDC_WAIT = 32514;
        public const int IDC_CROSS = 32515;
        public const int IDC_UPARROW = 32516;
        public const int IDC_SIZENWSE = 32642;
        public const int IDC_SIZENESW = 32643;
        public const int IDC_SIZEWE = 32644;
        public const int IDC_SIZENS = 32645;
        public const int IDC_SIZEALL = 32646;
        public const int IDC_NO = 32648;
        public const int IDC_HAND = 32649;

        public const int SM_CXCURSOR = 13;
        public const int SM_CYCURSOR = 14;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern int GetDpiForSystem();

        public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int x, int y) { cx = x; cy = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public int fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, int gaFlags);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll")]
        public static extern IntPtr CopyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("kernel32.dll")]
        public static extern uint GetTickCount();

        // A topmost window still loses to windows that are created topmost later (the IME
        // candidate list, some notification popups). Re-asserting HWND_TOPMOST on a timer
        // is the only reliable way to stay above them without a global hook.
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
            StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        // Returns the executable file name without extension, or "" when unavailable.
        public static string GetProcessName(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    StringBuilder sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size))
                    {
                        string full = sb.ToString(0, size);
                        string file = full;
                        int slash = full.LastIndexOf('\\');
                        if (slash >= 0) file = full.Substring(slash + 1);
                        int dot = file.LastIndexOf('.');
                        if (dot > 0) file = file.Substring(0, dot);
                        if (file.Length > 0) return file;
                    }
                }
                finally
                {
                    CloseHandle(h);
                }
            }

            // Fallback for the rare case the handle cannot be opened.
            try
            {
                return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                return "";
            }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint TimeBeginPeriod(uint uPeriod);

        [DllImport("gdi32.dll")]
        public static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
            IntPtr lpvBits, IntPtr lpbmi, uint uUsage);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, IntPtr pbmi, uint usage, out IntPtr ppvBits,
            IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    }

    // A cursor rendered into premultiplied BGRA, ready for ULW_ALPHA.
    internal sealed class CursorShape
    {
        public int Width;
        public int Height;
        public int HotspotX;
        public int HotspotY;
        public byte[] Pixels;
    }

    internal static class CursorImageFactory
    {
        // BITMAPINFOHEADER field offsets.
        private const int BI_SIZE = 40;

        public static CursorShape Extract(IntPtr hCursor, Color? tint, int alphaPercent, int fixedSize,
            bool flipHorizontal, bool flipVertical)
        {
            return Extract(hCursor, tint, alphaPercent, fixedSize, flipHorizontal, flipVertical, true);
        }

        public static CursorShape Extract(IntPtr hCursor, Color? tint, int alphaPercent, int fixedSize,
            bool flipHorizontal, bool flipVertical, bool allowVector)
        {
            if (hCursor == IntPtr.Zero) return null;

            IntPtr hIcon = Native.CopyIcon(hCursor);
            if (hIcon == IntPtr.Zero) return null;

            try
            {
                Native.ICONINFO ii;
                if (!Native.GetIconInfo(hIcon, out ii)) return null;

                try
                {
                    CursorShape shape = Build(ii, tint, alphaPercent, fixedSize, hCursor, allowVector);
                    if (shape != null && (flipHorizontal || flipVertical))
                    {
                        Flip(shape, flipHorizontal, flipVertical);
                    }
                    return shape;
                }                finally
                {
                    if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                    if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                }
            }
            finally
            {
                Native.DestroyIcon(hIcon);
            }
        }

        // Mirrors the glyph in place, moving the hotspot with it so it still lands on the pointer.
        private static void Flip(CursorShape shape, bool horizontal, bool vertical)
        {
            int w = shape.Width;
            int h = shape.Height;
            byte[] src = shape.Pixels;
            byte[] dst = new byte[src.Length];

            for (int y = 0; y < h; y++)
            {
                int sy = vertical ? (h - 1 - y) : y;
                for (int x = 0; x < w; x++)
                {
                    int sx = horizontal ? (w - 1 - x) : x;
                    int si = (sy * w + sx) * 4;
                    int di = (y * w + x) * 4;
                    dst[di + 0] = src[si + 0];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = src[si + 3];
                }
            }

            shape.Pixels = dst;
            if (horizontal) shape.HotspotX = w - 1 - shape.HotspotX;
            if (vertical) shape.HotspotY = h - 1 - shape.HotspotY;
        }

        private static CursorShape Build(Native.ICONINFO ii, Color? tint, int alphaPercent, int fixedSize,
            IntPtr hCursor, bool allowVector)
        {
            CursorShape raw = BuildRaw(ii, tint, alphaPercent);
            if (raw == null) return null;

            int targetW, targetH;
            if (fixedSize > 0)
            {
                targetW = fixedSize;
                targetH = fixedSize;
            }
            else
            {
                int metricW = Native.GetSystemMetrics(Native.SM_CXCURSOR);
                int metricH = Native.GetSystemMetrics(Native.SM_CYCURSOR);
                targetW = Math.Max(raw.Width, metricW);
                targetH = Math.Max(raw.Height, metricH);
            }

            if (targetW < 8 || targetH < 8 || targetW > 512 || targetH > 512)
            {
                targetW = raw.Width;
                targetH = raw.Height;
            }

            // Redraw from the hard-coded Windows vector data when the shape is known:
            // upscaling the 64px bitmap to 320px is what made the big steps look blurry.
            if (allowVector)
            {
                CursorKind kind = Classify(hCursor);
                VectorCursor vec = CursorArt.Render(kind, targetW, tint, alphaPercent);
                if (vec != null)
                {
                    CursorShape shape = new CursorShape();
                    shape.Width = vec.Width;
                    shape.Height = vec.Height;
                    shape.HotspotX = vec.HotspotX;
                    shape.HotspotY = vec.HotspotY;
                    shape.Pixels = vec.Pixels;
                    return shape;
                }
            }

            return ScaleToSystemSize(raw, fixedSize);
        }

        // Maps a system cursor handle to a shape CursorArt knows how to draw.
        // Maps a live cursor handle to a shape CursorArt can draw.
        //
        // Do NOT compare the handle against the IDC_* constants: LoadCursor returns an
        // allocated handle (0x10003 for the arrow), so the resource id never appears in it.
        // The reliable test is identity against the handles LoadCursor hands back, which are
        // stable for the session and follow the user's cursor scheme.
        private static Dictionary<IntPtr, CursorKind> cursorKinds;

        private static CursorKind Classify(IntPtr hCursor)
        {
            if (hCursor == IntPtr.Zero) return CursorKind.Unknown;

            if (cursorKinds == null)
            {
                Dictionary<IntPtr, CursorKind> map = new Dictionary<IntPtr, CursorKind>();
                AddKind(map, Native.IDC_ARROW, CursorKind.Arrow);
                AddKind(map, Native.IDC_IBEAM, CursorKind.IBeam);
                AddKind(map, Native.IDC_CROSS, CursorKind.Cross);
                AddKind(map, Native.IDC_UPARROW, CursorKind.UpArrow);
                AddKind(map, Native.IDC_SIZENS, CursorKind.SizeNS);
                AddKind(map, Native.IDC_SIZEWE, CursorKind.SizeWE);
                AddKind(map, Native.IDC_SIZEALL, CursorKind.SizeAll);
                AddKind(map, Native.IDC_NO, CursorKind.No);
                AddKind(map, Native.IDC_HAND, CursorKind.Hand);
                cursorKinds = map;
            }

            CursorKind kind;
            return cursorKinds.TryGetValue(hCursor, out kind) ? kind : CursorKind.Unknown;
        }

        private static void AddKind(Dictionary<IntPtr, CursorKind> map, int id, CursorKind kind)
        {
            IntPtr h = Native.LoadCursor(IntPtr.Zero, new IntPtr(id));
            if (h != IntPtr.Zero && !map.ContainsKey(h)) map[h] = kind;
        }

        private static CursorShape BuildRaw(Native.ICONINFO ii, Color? tint, int alphaPercent)
        {
            int w, h;
            Native.BITMAP bm = new Native.BITMAP();

            if (ii.hbmColor != IntPtr.Zero)
            {
                if (Native.GetObject(ii.hbmColor, Marshal.SizeOf(typeof(Native.BITMAP)), ref bm) == 0) return null;
                w = bm.bmWidth;
                h = bm.bmHeight;
            }
            else
            {
                if (ii.hbmMask == IntPtr.Zero) return null;
                if (Native.GetObject(ii.hbmMask, Marshal.SizeOf(typeof(Native.BITMAP)), ref bm) == 0) return null;
                w = bm.bmWidth;
                h = bm.bmHeight / 2;
            }

            if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;

            byte[] color = null;
            if (ii.hbmColor != IntPtr.Zero)
            {
                color = GetBits(ii.hbmColor, w, h, 32);
            }

            int mw = 0, mh = 0;
            byte[] mask = null;
            if (ii.hbmMask != IntPtr.Zero)
            {
                Native.BITMAP bmm = new Native.BITMAP();
                if (Native.GetObject(ii.hbmMask, Marshal.SizeOf(typeof(Native.BITMAP)), ref bmm) != 0)
                {
                    mw = bmm.bmWidth;
                    mh = bmm.bmHeight;
                    if (mw > 0 && mh > 0 && mw <= 512 && mh <= 512)
                    {
                        mask = GetBits(ii.hbmMask, mw, mh, 1);
                    }
                }
            }

            int maskStride = ((mw + 31) / 32) * 4;

            // Returns 1 when the AND-mask bit is set (that pixel is transparent).
            Func<int, int, int> andBit = delegate(int x, int y)
            {
                if (mask == null || x < 0 || y < 0 || x >= mw || y >= mh) return 0;
                int index = y * maskStride + (x >> 3);
                if (index < 0 || index >= mask.Length) return 0;
                return (mask[index] >> (7 - (x & 7))) & 1;
            };

            Func<int, int, int> xorBit = delegate(int x, int y)
            {
                if (mask == null) return 0;
                return andBit(x, y + h);
            };

            byte[] outPixels = new byte[w * h * 4];
            bool monochrome = ii.hbmColor == IntPtr.Zero;

            if (!monochrome && color != null)
            {
                bool anyAlpha = false;
                for (int i = 3; i < color.Length; i += 4)
                {
                    if (color[i] != 0) { anyAlpha = true; break; }
                }

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int si = (y * w + x) * 4;
                        byte b = color[si + 0];
                        byte g = color[si + 1];
                        byte r = color[si + 2];
                        int a = anyAlpha ? color[si + 3] : (andBit(x, y) != 0 ? 0 : 255);
                        WritePixel(outPixels, (y * w + x) * 4, r, g, b, a, tint, alphaPercent);
                    }
                }
            }
            else if (monochrome)
            {
                // AND mask = top half, XOR mask = bottom half.
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int a = andBit(x, y);
                        int xi = xorBit(x, y);
                        int alpha;
                        int value;
                        if (a == 1 && xi == 0) { alpha = 0; value = 0; }
                        else if (a == 1 && xi == 1) { alpha = 255; value = 255; }
                        else if (a == 0 && xi == 0) { alpha = 255; value = 0; }
                        else { alpha = 255; value = 255; }
                        WritePixel(outPixels, (y * w + x) * 4, (byte)value, (byte)value, (byte)value,
                            alpha, tint, alphaPercent);
                    }
                }
            }
            else
            {
                return null;
            }

            CursorShape shape = new CursorShape();
            shape.Width = w;
            shape.Height = h;
            shape.HotspotX = ii.xHotspot;
            shape.HotspotY = ii.yHotspot;
            if (shape.HotspotX < 0) shape.HotspotX = 0;
            if (shape.HotspotY < 0) shape.HotspotY = 0;
            if (shape.HotspotX >= w) shape.HotspotX = w - 1;
            if (shape.HotspotY >= h) shape.HotspotY = h - 1;
            shape.Pixels = outPixels;

            return shape;
        }

        // The DIB we get back is in logical pixels; Windows may paint the real cursor
        // larger on a scaled display. SM_CXCURSOR already includes the DPI factor for a
        // per-monitor-aware process, so match against it directly - never multiply again.
        private static CursorShape ScaleToSystemSize(CursorShape src, int fixedSize)
        {
            int targetW;
            int targetH;

            if (fixedSize > 0)
            {
                // Explicit step chosen by the user - take it verbatim.
                targetW = fixedSize;
                targetH = fixedSize;
            }
            else
            {
                // Auto: never shrink. If Windows already handed us a large bitmap
                // (accessibility "change pointer size" enlarges it), keep it.
                int metricW = Native.GetSystemMetrics(Native.SM_CXCURSOR);
                int metricH = Native.GetSystemMetrics(Native.SM_CYCURSOR);
                targetW = Math.Max(src.Width, metricW);
                targetH = Math.Max(src.Height, metricH);
            }

            if (targetW == src.Width && targetH == src.Height) return src;
            if (targetW < 8 || targetH < 8 || targetW > 512 || targetH > 512) return src;

            return Resize(src, targetW, targetH);
        }

        private static CursorShape Resize(CursorShape src, int dstW, int dstH)
        {
            int srcW = src.Width;
            int srcH = src.Height;
            byte[] dst = new byte[dstW * dstH * 4];

            // Separable bilinear in premultiplied space, two passes.
            bool upscale = dstW > srcW || dstH > srcH;
            double scaleX = (double)srcW / dstW;
            double scaleY = (double)srcH / dstH;

            byte[] tmp = new byte[dstW * srcH * 4];

            // Horizontal pass: srcW -> dstW, keep srcH rows.
            for (int y = 0; y < srcH; y++)
            {
                for (int x = 0; x < dstW; x++)
                {
                    double fx = (x + 0.5) * scaleX - 0.5;
                    int x0 = (int)Math.Floor(fx);
                    double tx = fx - x0;
                    int x1 = x0 + 1;
                    if (x0 < 0) { x0 = 0; tx = 0; }
                    if (x1 >= srcW) { x1 = srcW - 1; }
                    if (x0 >= srcW) { x0 = srcW - 1; }

                    int i0 = (y * srcW + x0) * 4;
                    int i1 = (y * srcW + x1) * 4;
                    int o = (y * dstW + x) * 4;

                    for (int c = 0; c < 4; c++)
                    {
                        double v = src.Pixels[i0 + c] * (1 - tx) + src.Pixels[i1 + c] * tx;
                        if (v < 0) v = 0;
                        if (v > 255) v = 255;
                        tmp[o + c] = (byte)(v + 0.5);
                    }
                }
            }

            // Vertical pass: srcH -> dstH.
            for (int y = 0; y < dstH; y++)
            {
                double fy = (y + 0.5) * scaleY - 0.5;
                int y0 = (int)Math.Floor(fy);
                double ty = fy - y0;
                int y1 = y0 + 1;
                if (y0 < 0) { y0 = 0; ty = 0; }
                if (y1 >= srcH) { y1 = srcH - 1; }
                if (y0 >= srcH) { y0 = srcH - 1; }

                for (int x = 0; x < dstW; x++)
                {
                    int i0 = (y0 * dstW + x) * 4;
                    int i1 = (y1 * dstW + x) * 4;
                    int o = (y * dstW + x) * 4;

                    for (int c = 0; c < 4; c++)
                    {
                        double v = tmp[i0 + c] * (1 - ty) + tmp[i1 + c] * ty;
                        if (v < 0) v = 0;
                        if (v > 255) v = 255;
                        dst[o + c] = (byte)(v + 0.5);
                    }
                }
            }

            CursorShape result = new CursorShape();
            result.Width = dstW;
            result.Height = dstH;
            result.Pixels = dst;

            double hx = (double)src.HotspotX * dstW / srcW;
            double hy = (double)src.HotspotY * dstH / srcH;
            result.HotspotX = (int)Math.Round(hx);
            result.HotspotY = (int)Math.Round(hy);
            if (result.HotspotX < 0) result.HotspotX = 0;
            if (result.HotspotY < 0) result.HotspotY = 0;
            if (result.HotspotX >= dstW) result.HotspotX = dstW - 1;
            if (result.HotspotY >= dstH) result.HotspotY = dstH - 1;

            return result;
        }

        private static void WritePixel(byte[] dst, int offset, byte r, byte g, byte b, int alpha,
            Color? tint, int alphaPercent)
        {
            if (alpha <= 0) { dst[offset + 0] = 0; dst[offset + 1] = 0; dst[offset + 2] = 0; dst[offset + 3] = 0; return; }

            if (tint.HasValue)
            {
                // Blend the glyph toward the tint colour so the "local" copy is identifiable.
                Color t = tint.Value;
                int mix = 70; // percent of tint
                r = (byte)((r * (100 - mix) + t.R * mix) / 100);
                g = (byte)((g * (100 - mix) + t.G * mix) / 100);
                b = (byte)((b * (100 - mix) + t.B * mix) / 100);
            }

            int a = alpha * alphaPercent / 100;
            if (a <= 0) { dst[offset + 0] = 0; dst[offset + 1] = 0; dst[offset + 2] = 0; dst[offset + 3] = 0; return; }

            // Premultiply for ULW_ALPHA.
            dst[offset + 0] = (byte)(b * a / 255);
            dst[offset + 1] = (byte)(g * a / 255);
            dst[offset + 2] = (byte)(r * a / 255);
            dst[offset + 3] = (byte)a;
        }

        // Returns top-down scanlines in the requested bit depth.
        private static byte[] GetBits(IntPtr hBitmap, int w, int h, int bpp)
        {
            IntPtr hdc = Native.CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;

            IntPtr bmi = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int stride = bpp == 32 ? w * 4 : ((w + 31) / 32) * 4;
                int size = stride * h;
                bmi = Marshal.AllocHGlobal(BI_SIZE + 256 * 4);
                buffer = Marshal.AllocHGlobal(size);

                for (int i = 0; i < BI_SIZE + 256 * 4; i++) Marshal.WriteByte(bmi, i, 0);
                for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);

                Marshal.WriteInt32(bmi, 0, BI_SIZE);          // biSize
                Marshal.WriteInt32(bmi, 4, w);                // biWidth
                Marshal.WriteInt32(bmi, 8, -h);               // biHeight (top-down)
                Marshal.WriteInt16(bmi, 12, 1);               // biPlanes
                Marshal.WriteInt16(bmi, 14, (short)bpp);      // biBitCount
                Marshal.WriteInt32(bmi, 16, 0);               // BI_RGB

                int lines = Native.GetDIBits(hdc, hBitmap, 0, (uint)h, buffer, bmi, 0);
                if (lines == 0) return null;

                byte[] result = new byte[size];
                Marshal.Copy(buffer, result, 0, size);
                return result;
            }
            finally
            {
                if (bmi != IntPtr.Zero) Marshal.FreeHGlobal(bmi);
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                Native.DeleteDC(hdc);
            }
        }
    }

    // A 32bpp top-down DIB section bound to a memory DC, reused across frames.
    internal sealed class LayeredSurface : IDisposable
    {
        private IntPtr hdc;
        private IntPtr hBitmap;
        private IntPtr oldBitmap;
        private IntPtr bits;
        private int width;
        private int height;

        public IntPtr Hdc { get { return hdc; } }
        public int Width { get { return width; } }
        public int Height { get { return height; } }

        public bool EnsureSize(int w, int h)
        {
            if (hdc != IntPtr.Zero && w == width && h == height) return true;
            Release();

            hdc = Native.CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;

            IntPtr bmi = Marshal.AllocHGlobal(40 + 256 * 4);
            try
            {
                for (int i = 0; i < 40 + 256 * 4; i++) Marshal.WriteByte(bmi, i, 0);
                Marshal.WriteInt32(bmi, 0, 40);
                Marshal.WriteInt32(bmi, 4, w);
                Marshal.WriteInt32(bmi, 8, -h);
                Marshal.WriteInt16(bmi, 12, 1);
                Marshal.WriteInt16(bmi, 14, 32);
                Marshal.WriteInt32(bmi, 16, 0);

                hBitmap = Native.CreateDIBSection(hdc, bmi, 0, out bits, IntPtr.Zero, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(bmi);
            }

            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                Release();
                return false;
            }

            oldBitmap = Native.SelectObject(hdc, hBitmap);
            width = w;
            height = h;
            return true;
        }

        public void Upload(byte[] pixels)
        {
            if (bits == IntPtr.Zero) return;
            int size = width * height * 4;
            if (pixels == null || pixels.Length < size) return;
            Marshal.Copy(pixels, 0, bits, size);
        }

        private void Release()
        {
            if (hdc != IntPtr.Zero)
            {
                if (oldBitmap != IntPtr.Zero) Native.SelectObject(hdc, oldBitmap);
                Native.DeleteDC(hdc);
            }
            if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
            hdc = IntPtr.Zero;
            hBitmap = IntPtr.Zero;
            oldBitmap = IntPtr.Zero;
            bits = IntPtr.Zero;
            width = 0;
            height = 0;
        }

        public void Dispose()
        {
            Release();
        }
    }

    internal enum GateMode
    {
        Mstsc = 0,   // draw whenever the pointer is over a target window - focus independent
        Auto = 1,    // legacy alias for Mstsc
        Always = 2   // draw everywhere (for testing outside RDP)
    }

    internal sealed class Options
    {
        public GateMode Mode = GateMode.Mstsc;
        public int Alpha = 100;
        public int Level = 0;   // 0 = follow the system cursor, 1..15 = Windows pointer-size step
        public bool FlipH = true;   // mirror horizontally by default so the two cursors are distinct
        public bool FlipV = false;
        public bool Vector = true;  // redraw known cursors as paths instead of upscaling a bitmap
        public Color? Tint = null;
        public string Processes = "mstsc,msrdc";
        public string SelfTest = null;
        public string Probe = null;
        public string Shot = null;
        public bool StartDisabled;
        public string MakeIcon = null;
        public string Log = null;

        // Settings live next to the exe so a double-click keeps the last tuned values.
        // Only appearance is persisted; paused/enabled is deliberately not, so the app
        // always starts in its working state.
        public static string ConfigPath
        {
            get
            {
                try
                {
                    string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    string dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "CursorMirror.ini");
                }
                catch
                {
                }
                return Path.Combine(Environment.CurrentDirectory, "CursorMirror.ini");
            }
        }

        public void LoadFromConfig()
        {
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) return;

                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "size":
                            {
                                string s = val.ToLowerInvariant();
                                if (s == "auto" || s == "0" || s.Length == 0)
                                {
                                    Level = 0;
                                }
                                else
                                {
                                    int n;
                                    if (int.TryParse(s, out n)) Level = Math.Max(1, Math.Min(SizeLadder.Count, n));
                                }
                            }
                            break;
                        case "alpha":
                            {
                                int n;
                                if (int.TryParse(val, out n)) Alpha = Math.Max(10, Math.Min(100, n));
                            }
                            break;
                        case "flip":
                            {
                                string f = val.ToLowerInvariant();
                                FlipH = f == "h" || f == "both" || f == "1" || f == "yes" || f == "true";
                                FlipV = f == "v" || f == "both";
                            }
                            break;
                        case "mode":
                            {
                                string m = val.ToLowerInvariant();
                                if (m == "auto") Mode = GateMode.Auto;
                                else if (m == "always") Mode = GateMode.Always;
                                else Mode = GateMode.Mstsc;
                            }
                            break;
                        case "proc":
                            if (val.Length > 0) Processes = val;
                            break;
                        case "vector":
                            {
                                string vv = val.ToLowerInvariant();
                                Vector = !(vv == "off" || vv == "0" || vv == "false" || vv == "no");
                            }
                            break;
                        case "tint":
                            {
                                try
                                {
                                    if (val.Length > 0) Tint = ColorTranslator.FromHtml(val.StartsWith("#") ? val : "#" + val);
                                }
                                catch
                                {
                                }
                            }
                            break;
                    }
                }
            }
            catch
            {
            }
        }

        public void SaveConfig()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; CursorMirror settings - edit and restart, or use the tray menu / hotkeys");
                sb.AppendLine("; size  = 1..15 (same steps as Settings > Accessibility > Mouse pointer size), or auto");
                sb.AppendLine("; flip  = h | v | both | off");
                sb.AppendLine("; mode  = mstsc | always");
                sb.AppendLine();
                sb.AppendLine("size=" + (Level > 0 ? Level.ToString() : "auto"));
                sb.AppendLine("alpha=" + Alpha);
                sb.AppendLine("flip=" + (FlipH && FlipV ? "both" : FlipH ? "h" : FlipV ? "v" : "off"));
                sb.AppendLine("vector=" + (Vector ? "on" : "off"));
                sb.AppendLine("mode=" + Mode.ToString().ToLowerInvariant());
                sb.AppendLine("proc=" + Processes);
                if (Tint.HasValue)
                {
                    sb.AppendLine("tint=" + Tint.Value.R.ToString("X2") + Tint.Value.G.ToString("X2") + Tint.Value.B.ToString("X2"));
                }
                File.WriteAllText(ConfigPath, sb.ToString());
            }
            catch
            {
            }
        }

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            // Cached appearance first, then let command-line flags override it.
            o.LoadFromConfig();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string v = null;
                int eq = a.IndexOf('=');
                if (a.StartsWith("--") && eq > 0)
                {
                    v = a.Substring(eq + 1);
                    a = a.Substring(0, eq);
                }
                else if (a.StartsWith("--") && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    v = args[i + 1];
                    i++;
                }

                switch (a.ToLowerInvariant())
                {
                    case "--mode":
                        if (v != null)
                        {
                            string m = v.ToLowerInvariant();
                            if (m == "auto") o.Mode = GateMode.Auto;
                            else if (m == "always") o.Mode = GateMode.Always;
                            else o.Mode = GateMode.Mstsc;
                        }
                        break;
                    case "--alpha":
                        if (v != null)
                        {
                            int n;
                            if (int.TryParse(v, out n)) o.Alpha = Math.Max(10, Math.Min(100, n));
                        }
                        break;
                    case "--scale":
                    case "--size":
                        if (v != null)
                        {
                            string sv = v.ToLowerInvariant();
                            if (sv == "auto" || sv == "0")
                            {
                                o.Level = 0;
                            }
                            else
                            {
                                int n;
                                if (int.TryParse(sv, out n))
                                {
                                    o.Level = Math.Max(1, Math.Min(SizeLadder.Count, n));
                                }
                            }
                        }
                        break;
                    case "--tint":
                        if (v != null)
                        {
                            try { o.Tint = ColorTranslator.FromHtml(v.StartsWith("#") ? v : "#" + v); }
                            catch { }
                        }
                        break;
                    case "--proc":
                        if (v != null) o.Processes = v;
                        break;
                    case "--flip":
                        if (v == null)
                        {
                            o.FlipH = true;
                        }
                        else
                        {
                            string fv = v.ToLowerInvariant();
                            if (fv == "0" || fv == "no" || fv == "off" || fv == "none")
                            {
                                o.FlipH = false;
                                o.FlipV = false;
                            }
                            else if (fv == "both")
                            {
                                o.FlipH = true;
                                o.FlipV = true;
                            }
                            else if (fv == "v" || fv == "vertical")
                            {
                                o.FlipH = false;
                                o.FlipV = true;
                            }
                            else
                            {
                                o.FlipH = true;
                                o.FlipV = false;
                            }
                        }
                        break;
                    case "--fliph":
                        o.FlipH = true;
                        break;
                    case "--flipv":
                        o.FlipV = true;
                        break;
                    case "--noflip":
                        o.FlipH = false;
                        o.FlipV = false;
                        break;
                    case "--selftest":
                        o.SelfTest = v == null ? "cursor-selftest.png" : v;
                        break;
                    case "--probe":
                        o.Probe = v;
                        break;
                    case "--shot":
                        o.Shot = v == null ? "menu.png" : v;
                        break;
                    case "--off":
                        o.StartDisabled = true;
                        break;
                    case "--makeicon":
                        o.MakeIcon = v == null ? "CursorMirror.ico" : v;
                        break;
                    case "--vector":
                        if (v != null)
                        {
                            string sv = v.ToLowerInvariant();
                            o.Vector = !(sv == "off" || sv == "0" || sv == "false" || sv == "no");
                        }
                        break;
                    case "--log":
                        o.Log = v;
                        break;
                }
            }
            return o;
        }
    }

    internal sealed class Overlay
    {
        private const int TIMER_ID = 1;
        private const int HK_TOGGLE = 1;
        private const int HK_MODE = 2;
        private const int HK_QUIT = 3;
        private const int HK_BIGGER = 4;
        private const int HK_SMALLER = 5;

        private static Overlay s_current;
        private static Native.WndProcDelegate s_procDelegate;

        private readonly Options options;
        private readonly LayeredSurface surface = new LayeredSurface();
        private readonly Dictionary<uint, string> processNames = new Dictionary<uint, string>();

        private IntPtr hwnd;
        private bool visible;
        private bool enabled = true;
        private IntPtr lastCursorHandle;
        private IntPtr lastKnownCursor;
        private CursorShape shape;
        private int refreshCounter;
        // Native tray state. Kept as primitives rather than a NotifyIcon so the icon is
        // owned by this app's own window and message loop.
        private const int TRAY_ID = 1;
        private const int TRAY_CALLBACK = Native.WM_APP + 1;
        private static readonly uint taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
        private Icon trayIcon;
        private bool trayAdded;
        private string trayTip = "CursorMirror";
        private ModernMenu menu;
        private int lastLoggedSize;
        private string className;
        private List<IntPtr> targetWindows;
        private long targetsStamp;

        public Overlay(Options opts)
        {
            options = opts;
            s_current = this;
        }

        public void Run()
        {
            className = "CursorMirror.Overlay." + Guid.NewGuid().ToString("N");

            s_procDelegate = new Native.WndProcDelegate(StaticWindowProc);

            Native.WNDCLASSEX wc = new Native.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(Native.WNDCLASSEX));
            wc.style = 0;
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_procDelegate);
            wc.hInstance = Native.GetModuleHandle(null);
            wc.lpszClassName = className;
            wc.hCursor = Native.LoadCursor(IntPtr.Zero, new IntPtr(Native.IDC_ARROW));

            if (Native.RegisterClassEx(ref wc) == 0)
            {
                return;
            }

            uint exStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE |
                           Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;

            hwnd = Native.CreateWindowEx(exStyle, className, "CursorMirror", Native.WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // 1 ms timer resolution so the overlay tracks the pointer frame-accurately.
            Native.TimeBeginPeriod(1);
            Native.SetTimer(hwnd, new IntPtr(TIMER_ID), 8, IntPtr.Zero);

            Native.RegisterHotKey(hwnd, HK_TOGGLE, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.P);
            Native.RegisterHotKey(hwnd, HK_MODE, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.M);
            Native.RegisterHotKey(hwnd, HK_QUIT, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.Q);
            // OEM_PLUS / OEM_MINUS so it works on the main keyboard row.
            Native.RegisterHotKey(hwnd, HK_BIGGER, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0xBB);
            Native.RegisterHotKey(hwnd, HK_SMALLER, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0xBD);

            CreateTrayIcon();

            Native.MSG msg;
            while (true)
            {
                int r = Native.GetMessage(out msg, IntPtr.Zero, 0, 0);
                if (r == 0 || r == -1) break;
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }

            Cleanup();
        }

        private static IntPtr StaticWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            Overlay o = s_current;
            if (o != null) return o.WindowProc(hWnd, msg, wParam, lParam);
            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // "TaskbarCreated" is a runtime-registered message, so it cannot be a case label.
            if (msg == taskbarCreatedMessage && msg != 0)
            {
                // Explorer restarted: the shell drops every icon, so put ours back.
                trayAdded = false;
                CreateTrayIcon();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case Native.WM_TIMER:
                    if (wParam.ToInt64() == TIMER_ID)
                    {
                        try
                        {
                            Tick();
                        }
                        catch (Exception ex)
                        {
                            Program.Log("tick error: " + ex.Message);
                            Hide();
                        }
                    }
                    return IntPtr.Zero;

                case Native.WM_NCHITTEST:
                    return new IntPtr(Native.HTTRANSPARENT);

                case Native.WM_MOUSEACTIVATE:
                    return new IntPtr(Native.MA_NOACTIVATE);

                case Native.WM_HOTKEY:
                    HandleHotKey(wParam.ToInt32());
                    return IntPtr.Zero;

                case TRAY_CALLBACK:
                    {
                        // With NOTIFYICON_VERSION_4 the event code is the low word of lParam
                        // and the anchor point is packed into wParam.
                        int evt = (int)(lParam.ToInt64() & 0xFFFF);
                        if (evt == Native.NIN_SELECT || evt == Native.NIN_KEYSELECT ||
                            evt == Native.WM_CONTEXTMENU)
                        {
                            ShowMenu(new Point((short)(wParam.ToInt64() & 0xFFFF),
                                               (short)((wParam.ToInt64() >> 16) & 0xFFFF)));
                        }
                        return IntPtr.Zero;
                    }

                // Explorer restarted: the shell drops every icon, so put ours back.
                case Native.WM_DISPLAYCHANGE:
                case Native.WM_SETTINGCHANGE:
                case Native.WM_DPICHANGED:
                    lastCursorHandle = IntPtr.Zero;
                    shape = null;
                    return IntPtr.Zero;

                case Native.WM_DESTROY:
                    Native.PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void HandleHotKey(int id)
        {
            if (id == HK_TOGGLE)
            {
                enabled = !enabled;
                if (!enabled) Hide();
                UpdateTrayText();
            }
            else if (id == HK_MODE)
            {
                options.Mode = options.Mode == GateMode.Always ? GateMode.Mstsc : GateMode.Always;
                lastCursorHandle = IntPtr.Zero;
                options.SaveConfig();
                UpdateTrayText();
            }
            else if (id == HK_QUIT)
            {
                Native.DestroyWindow(hwnd);
            }
            else if (id == HK_BIGGER)
            {
                StepSize(1);
            }
            else if (id == HK_SMALLER)
            {
                StepSize(-1);
            }
        }

        // Pixel size passed to the extractor: 0 means "auto" (never shrink the real cursor).
        private int TargetPixels()
        {
            return TargetPixelsFor(options);
        }

        private static int TargetPixelsFor(Options o)
        {
            if (o.Level <= 0) return 0;
            return ModernMenu.LevelPixels(o.Level);
        }

        private void StepSize(int direction)
        {
            int current = CurrentLevel();
            int next = Math.Max(1, Math.Min(SizeLadder.Count, current + direction));
            SetLevel(next);
        }

        // The level the mirror is effectively using; "auto" is resolved to the nearest step.
        private int CurrentLevel()
        {
            if (options.Level > 0) return options.Level;

            int px = shape != null ? shape.Width : Native.GetSystemMetrics(Native.SM_CXCURSOR);
            int best = 1;
            int bestDiff = int.MaxValue;
            for (int i = 1; i <= SizeLadder.Count; i++)
            {
                int diff = Math.Abs(ModernMenu.LevelPixels(i) - px);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }

        private void SetLevel(int level)
        {
            options.Level = Math.Max(1, Math.Min(SizeLadder.Count, level));
            lastCursorHandle = IntPtr.Zero;
            shape = null;
            refreshCounter = 0;
            options.SaveConfig();
            UpdateTrayText();
        }

        private void Tick()
        {
            if (!enabled)
            {
                Hide();
                return;
            }

            Native.POINT pt;
            if (!Native.GetCursorPos(out pt))
            {
                Hide();
                return;
            }

            if (!ShouldDraw(pt))
            {
                Hide();
                return;
            }

            Native.CURSORINFO ci = new Native.CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));

            IntPtr hCursor = IntPtr.Zero;
            if (Native.GetCursorInfo(ref ci) && ci.hCursor != IntPtr.Zero)
            {
                hCursor = ci.hCursor;
                lastKnownCursor = hCursor;
            }
            if (hCursor == IntPtr.Zero)
            {
                // mstsc sets a null cursor inside the session, so GetCursorInfo stops
                // reporting a shape. Reuse the last real one we saw.
                hCursor = lastKnownCursor;
            }
            if (hCursor == IntPtr.Zero)
            {
                hCursor = Native.LoadCursor(IntPtr.Zero, new IntPtr(Native.IDC_ARROW));
            }
            if (hCursor == IntPtr.Zero)
            {
                Hide();
                return;
            }

            // Re-extract when the shape changes, and periodically in case of animated cursors.
            refreshCounter++;
            if (hCursor != lastCursorHandle || shape == null || refreshCounter >= 40)
            {
                refreshCounter = 0;
                lastCursorHandle = hCursor;
                shape = CursorImageFactory.Extract(hCursor, options.Tint, options.Alpha, TargetPixels(), options.FlipH, options.FlipV, options.Vector);
            }

            if (shape == null)
            {
                Hide();
                return;
            }

            if (options.Log != null && shape.Width != lastLoggedSize)
            {
                lastLoggedSize = shape.Width;
                Program.Log("shape=" + shape.Width + "x" + shape.Height + " level=" + options.Level +
                    " pixels=" + TargetPixels() + " flip=" + options.FlipH);
            }

            Draw(pt);
        }

        private bool ShouldDraw(Native.POINT pt)
        {
            if (options.Mode == GateMode.Always) return true;

            // Draw whenever the pointer sits over a target window - focus is irrelevant.
            // mstsc hides the OS cursor inside its client area even when inactive, so
            // gating on the foreground window made the local cursor vanish.
            List<IntPtr> targets = CurrentTargetWindows();
            for (int i = 0; i < targets.Count; i++)
            {
                if (PointInClientArea(targets[i], pt))
                {
                    return true;
                }
            }

            return false;
        }

        // Cached top-level windows belonging to the configured client processes.
        private List<IntPtr> CurrentTargetWindows()
        {
            long now = Environment.TickCount;
            if (targetWindows != null && now - targetsStamp < 500)
            {
                return targetWindows;
            }

            targetsStamp = now;
            List<IntPtr> found = new List<IntPtr>();

            try
            {
                Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (hWnd == hwnd) return true;
                    if (!Native.IsWindowVisible(hWnd)) return true;

                    uint pid;
                    Native.GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == 0) return true;
                    if (!IsTargetProcess(pid)) return true;

                    // Skip tool windows (palette/helper windows) that share the process.
                    int ex = Native.GetWindowLong(hWnd, Native.GWL_EXSTYLE);
                    if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;

                    Native.RECT rc;
                    if (!Native.GetClientRect(hWnd, out rc)) return true;
                    if (rc.Right - rc.Left <= 0 || rc.Bottom - rc.Top <= 0) return true;

                    found.Add(hWnd);
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }

            targetWindows = found;
            if (options.Log != null)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("targets=" + found.Count);
                for (int i = 0; i < found.Count; i++)
                {
                    Native.RECT rc;
                    Native.POINT o = new Native.POINT(0, 0);
                    if (Native.GetClientRect(found[i], out rc) && Native.ClientToScreen(found[i], ref o))
                    {
                        sb.Append(string.Format(" [{0}: {1},{2}-{3},{4}]", found[i],
                            o.X, o.Y, o.X + (rc.Right - rc.Left), o.Y + (rc.Bottom - rc.Top)));
                    }
                }
                Program.Log(sb.ToString());
            }
            return targetWindows;
        }

        private bool PointInClientArea(IntPtr hwnd, Native.POINT pt)
        {
            Native.RECT rc;
            if (!Native.GetClientRect(hwnd, out rc)) return false;

            Native.POINT origin = new Native.POINT(0, 0);
            if (!Native.ClientToScreen(hwnd, ref origin)) return false;

            int left = origin.X + rc.Left;
            int top = origin.Y + rc.Top;
            int right = origin.X + rc.Right;
            int bottom = origin.Y + rc.Bottom;

            return pt.X >= left && pt.X < right && pt.Y >= top && pt.Y < bottom;
        }

        private bool IsTargetProcess(uint pid)
        {
            string name;
            if (processNames.TryGetValue(pid, out name))
            {
                return name.Length > 0;
            }

            name = "";
            try
            {
                name = Native.GetProcessName(pid);
            }
            catch
            {
                name = "";
            }

            processNames[pid] = name;

            if (name.Length == 0) return false;

            string[] wanted = options.Processes.Split(',');
            for (int i = 0; i < wanted.Length; i++)
            {
                string w = wanted[i].Trim();
                if (w.Length == 0) continue;
                if (string.Equals(name, w, StringComparison.OrdinalIgnoreCase)) return true;
                if (w.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(name, w.Substring(0, w.Length - 4), StringComparison.OrdinalIgnoreCase)) return true;
            }

            // Do not cache negative results forever (pid reuse).
            processNames.Remove(pid);
            return false;
        }

        private void Draw(Native.POINT pt)
        {
            if (!surface.EnsureSize(shape.Width, shape.Height))
            {
                Hide();
                return;
            }

            surface.Upload(shape.Pixels);

            Native.POINT dst = new Native.POINT(pt.X - shape.HotspotX, pt.Y - shape.HotspotY);
            Native.POINT src = new Native.POINT(0, 0);
            Native.SIZE size = new Native.SIZE(shape.Width, shape.Height);

            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.BlendOp = Native.AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = Native.AC_SRC_ALPHA;

            if (!visible)
            {
                Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
                visible = true;
                ForceTopmost();
            }

            Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, surface.Hdc, ref src,
                0, ref blend, Native.ULW_ALPHA);

            // The IME candidate window and friends are topmost too and win by being created
            // later, which hides the mirror cursor behind them. Push back periodically; doing
            // it every frame (8 ms) would fight the shell for no gain.
            uint now = Native.GetTickCount();
            if (now - lastTopmostAssert >= TopmostIntervalMs)
            {
                ForceTopmost();
                lastTopmostAssert = now;
            }
        }

        private const uint TopmostIntervalMs = 150;
        private uint lastTopmostAssert;

        private void ForceTopmost()
        {
            try
            {
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE |
                    Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }
            catch
            {
            }
        }

        private void Hide()
        {
            if (!visible) return;
            Native.ShowWindow(hwnd, Native.SW_HIDE);
            visible = false;
        }

        private void CreateTrayIcon()
        {
            try
            {
                trayIcon = MakeTrayIcon();

                Native.NOTIFYICONDATA data = new Native.NOTIFYICONDATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
                data.hWnd = hwnd;
                data.uID = TRAY_ID;
                data.uFlags = Native.NIF_ICON | Native.NIF_TIP |
                              Native.NIF_MESSAGE | Native.NIF_SHOWTIP;
                data.uCallbackMessage = TRAY_CALLBACK;
                data.hIcon = trayIcon.Handle;
                data.szTip = trayTip;
                data.szInfo = string.Empty;
                data.szInfoTitle = string.Empty;
                data.guidItem = Guid.Empty;

                trayAdded = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
                int err = Marshal.GetLastWin32Error();

                if (!trayAdded)
                {
                    Program.Log("tray NIM_ADD failed, win32=" + err
                        + " (" + new System.ComponentModel.Win32Exception(err).Message + ")");
                    return;
                }

                // Version 4 delivers click events with the anchor point in wParam and
                // shows the tooltip without needing a separate NIM_MODIFY dance.
                data.uVersion = Native.NOTIFYICON_VERSION_4;
                Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);

                UpdateTrayText();

                // Ask the shell where it put the icon. hr==0 means it really is on a
                // taskbar; anything else means the shell silently dropped it, which is
                // exactly the failure that looked like "no icon".
                Native.NOTIFYICONIDENTIFIER id = new Native.NOTIFYICONIDENTIFIER();
                id.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONIDENTIFIER));
                id.hWnd = hwnd;
                id.uID = TRAY_ID;
                id.guidItem = Guid.Empty;

                Native.RECT rect;
                int hr = Native.Shell_NotifyIconGetRect(ref id, out rect);
                string where = hr == 0
                    ? ("rect=" + rect.Left + "," + rect.Top + "-" + rect.Right + "," + rect.Bottom)
                    : ("hr=0x" + hr.ToString("X8"));
                Program.Log("tray icon added, " + where);
            }
            catch (Exception ex)
            {
                trayIcon = null;
                trayAdded = false;
                Program.Log("tray icon FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void RemoveTrayIcon()
        {
            if (!trayAdded) return;

            try
            {
                Native.NOTIFYICONDATA data = new Native.NOTIFYICONDATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
                data.hWnd = hwnd;
                data.uID = TRAY_ID;
                data.guidItem = Guid.Empty;
                Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            }
            catch
            {
            }

            trayAdded = false;
        }


        private void ShowMenu(Point screen)
        {
            if (menu != null && menu.Visible)
            {
                menu.Close();
                menu = null;
                return;
            }

            MenuModel model = new MenuModel();
            model.GetEnabled = delegate { return enabled; };
            model.SetEnabled = delegate(bool v)
            {
                enabled = v;
                if (!v) Hide();
                UpdateTrayText();
            };
            model.GetLevel = delegate { return options.Level; };
            model.SetLevel = delegate(int level)
            {
                if (level <= 0)
                {
                    options.Level = 0;
                    lastCursorHandle = IntPtr.Zero;
                    shape = null;
                    refreshCounter = 0;
                    options.SaveConfig();
                }
                else
                {
                    SetLevel(level);
                }
            };
            model.GetFlipH = delegate { return options.FlipH; };
            model.SetFlipH = delegate(bool v)
            {
                options.FlipH = v;
                lastCursorHandle = IntPtr.Zero;
                shape = null;
                refreshCounter = 0;
                options.SaveConfig();
            };
            model.GetOnlyMstsc = delegate { return options.Mode == GateMode.Mstsc; };
            model.SetOnlyMstsc = delegate(bool v)
            {
                options.Mode = v ? GateMode.Mstsc : GateMode.Always;
                lastCursorHandle = IntPtr.Zero;
                options.SaveConfig();
            };
            model.OnExit = delegate { Native.DestroyWindow(hwnd); };
            model.OnCommit = delegate
            {
                UpdateTrayText();
            };

            menu = new ModernMenu(model);
            menu.FormClosed += delegate { menu = null; };
            menu.ShowAt(screen);
        }

        private void UpdateTrayText()
        {
            if (!trayAdded) return;

            string size = options.Level > 0 ? ("Lv" + options.Level) : "auto";
            string text = "CursorMirror - " + size + (enabled ? "" : " (paused)");
            // szTip is 128 chars in NOTIFYICONDATA; the shell truncates at 63 anyway.
            if (text.Length > 63) text = text.Substring(0, 63);
            if (text == trayTip) return;
            trayTip = text;

            try
            {
                Native.NOTIFYICONDATA data = new Native.NOTIFYICONDATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
                data.hWnd = hwnd;
                data.uID = TRAY_ID;
                data.uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP;
                data.szTip = text;
                data.szInfo = string.Empty;
                data.szInfoTitle = string.Empty;
                data.guidItem = Guid.Empty;
                Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
            }
            catch
            {
            }
        }

        private static Icon MakeTrayIcon()
        {
            return IconFactory.Create();
        }

        private static PointF[] Grow(PointF[] pts, float amount)
        {
            float cx = 0, cy = 0;
            for (int i = 0; i < pts.Length; i++) { cx += pts[i].X; cy += pts[i].Y; }
            cx /= pts.Length;
            cy /= pts.Length;

            PointF[] result = new PointF[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                float dx = pts[i].X - cx;
                float dy = pts[i].Y - cy;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.01f) { result[i] = pts[i]; continue; }
                result[i] = new PointF(pts[i].X + dx / len * amount, pts[i].Y + dy / len * amount);
            }
            return result;
        }

        private void Cleanup()
        {
            Native.KillTimer(hwnd, new IntPtr(TIMER_ID));
            Native.UnregisterHotKey(hwnd, HK_TOGGLE);
            Native.UnregisterHotKey(hwnd, HK_MODE);
            Native.UnregisterHotKey(hwnd, HK_QUIT);
            Native.UnregisterHotKey(hwnd, HK_BIGGER);
            Native.UnregisterHotKey(hwnd, HK_SMALLER);

            RemoveTrayIcon();
            if (trayIcon != null)
            {
                trayIcon.Dispose();
                trayIcon = null;
            }

            surface.Dispose();
            if (hwnd != IntPtr.Zero)
            {
                Native.DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
            }
            s_current = null;
        }
    }

    internal static class Program
    {
        private static string logPath;

        internal static void Log(string message)
        {
            if (logPath == null) return;
            try
            {
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Options options = Options.Parse(args);
            logPath = options.Log;

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log("UNHANDLED: " + e.ExceptionObject);
            };

            try
            {
                EnableDpiAwareness();

                if (options.SelfTest != null)
                {
                    RunSelfTest(options);
                    return;
                }

                if (options.Probe != null)
                {
                    RunProbe(options);
                    return;
                }

                if (options.Shot != null)
                {
                    RunShot(options);
                    return;
                }

                if (options.MakeIcon != null)
                {
                    IconFactory.SaveIco(options.MakeIcon);
                    return;
                }

                try { Application.EnableVisualStyles(); }
                catch { }

                Log("starting overlay, mode=" + options.Mode);
                Overlay overlay = new Overlay(options);
                overlay.Run();
                Log("overlay exited normally");
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                throw;
            }
        }

        private static void EnableDpiAwareness()
        {
            try
            {
                if (Native.SetProcessDpiAwarenessContext(new IntPtr(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)))
                    return;
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try { Native.SetProcessDPIAware(); }
            catch { }
        }

        // Answers "would the mirror be drawn at this screen point?" without showing anything.
        private static void RunProbe(Options options)
        {
            StringBuilder log = new StringBuilder();
            string[] parts = options.Probe.Split(',');
            int px = 0, py = 0;
            if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out px) || !int.TryParse(parts[1].Trim(), out py))
            {
                log.AppendLine("usage: --probe x,y");
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "cursorprobe.log"), log.ToString());
                return;
            }

            log.AppendLine("probe point = " + px + "," + py);
            log.AppendLine("procs = " + options.Processes);

            int count = 0;
            bool hit = false;
            int seen = 0;
            StringBuilder dbg = new StringBuilder();

            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(hWnd)) return true;

                uint pid;
                Native.GetWindowThreadProcessId(hWnd, out pid);
                if (pid == 0) return true;

                string name = Native.GetProcessName(pid);
                if (name.Length == 0) name = "?";

                seen++;
                if (seen <= 40) dbg.AppendLine("  seen: " + name + " pid=" + pid);

                bool match = false;
                string[] wanted = options.Processes.Split(',');
                for (int i = 0; i < wanted.Length; i++)
                {
                    string w = wanted[i].Trim();
                    if (w.Length == 0) continue;
                    if (w.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) w = w.Substring(0, w.Length - 4);
                    if (string.Equals(name, w, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                }
                if (!match) return true;

                int ex = Native.GetWindowLong(hWnd, Native.GWL_EXSTYLE);
                bool tool = (ex & Native.WS_EX_TOOLWINDOW) != 0;

                Native.RECT rc;
                Native.POINT o = new Native.POINT(0, 0);
                if (!Native.GetClientRect(hWnd, out rc)) return true;
                if (!Native.ClientToScreen(hWnd, ref o)) return true;

                int l = o.X, t = o.Y;
                int r = o.X + (rc.Right - rc.Left), b = o.Y + (rc.Bottom - rc.Top);
                if (r - l <= 0 || b - t <= 0) return true;

                bool inside = px >= l && px < r && py >= t && py < b;
                count++;
                log.AppendLine(string.Format("window {0} proc={1} tool={2} rect={3},{4}-{5},{6} inside={7}",
                    hWnd, name, tool, l, t, r, b, inside));

                if (inside && !tool) hit = true;
                return true;
            }, IntPtr.Zero);

            log.AppendLine("candidate windows = " + count);
            log.AppendLine("visible windows seen = " + seen);
            log.AppendLine("--- dump ---");
            log.Append(dbg.ToString());
            log.AppendLine("verdict = " + (options.Mode == GateMode.Always ? "draw (mode=always)" : (hit ? "DRAW" : "hidden")));

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "cursorprobe.log"), log.ToString());
        }

        // Renders the flyout menu to a PNG so its appearance can be reviewed without
        // clicking the tray icon. Used for visual checks during development.
        private static void RunShot(Options options)
        {
            try { Application.EnableVisualStyles(); }
            catch { }

            MenuModel model = new MenuModel();
            model.GetEnabled = delegate { return !options.StartDisabled; };
            model.SetEnabled = delegate(bool v) { };
            model.GetLevel = delegate { return options.Level; };
            model.SetLevel = delegate(int v) { };
            model.GetFlipH = delegate { return options.FlipH; };
            model.SetFlipH = delegate(bool v) { };
            model.GetOnlyMstsc = delegate { return options.Mode == GateMode.Mstsc; };
            model.SetOnlyMstsc = delegate(bool v) { };
            model.OnExit = delegate { };
            model.OnCommit = delegate { };

            using (ModernMenu m = new ModernMenu(model))
            {
                m.CreateControl();
                m.PerformLayout();

                using (Bitmap bmp = new Bitmap(m.Width, m.Height))
                {
                    m.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(options.Shot, System.Drawing.Imaging.ImageFormat.Png);
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("menu.jpg -> " + options.Shot);
                sb.AppendLine("size = " + m.Width + "x" + m.Height);
                sb.AppendLine("level = " + options.Level);
                sb.AppendLine("pixels(level) = " + ModernMenu.LevelPixels(options.Level <= 0 ? 1 : options.Level));
                for (int i = 1; i <= SizeLadder.Count; i++)
                {
                    sb.Append("L" + i + "=" + ModernMenu.LevelPixels(i) + "px  ");
                }
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "menushot.log"), sb.ToString());
            }
        }

        // Renders the current cursor to a PNG so the extraction path can be verified headlessly.
        private static void RunSelfTest(Options options)
        {
            StringBuilder log = new StringBuilder();

            IntPtr arrow = Native.LoadCursor(IntPtr.Zero, new IntPtr(Native.IDC_ARROW));
            log.AppendLine("LoadCursor(IDC_ARROW) = " + arrow.ToString());

            int fixedSize = options.Level > 0 ? ModernMenu.LevelPixels(options.Level) : 0;
            CursorShape shape = CursorImageFactory.Extract(arrow, options.Tint, options.Alpha, fixedSize, options.FlipH, options.FlipV, options.Vector);
            if (shape == null)
            {
                log.AppendLine("Extract failed");
                File.WriteAllText(options.SelfTest + ".log", log.ToString());
                Environment.Exit(2);
                return;
            }

            log.AppendLine(string.Format("size = {0}x{1}, hotspot = {2},{3}, bytes = {4}",
                shape.Width, shape.Height, shape.HotspotX, shape.HotspotY, shape.Pixels.Length));
            log.AppendLine(string.Format("SM_CXCURSOR={0} SM_CYCURSOR={1} GetDpiForSystem={2}",
                Native.GetSystemMetrics(Native.SM_CXCURSOR),
                Native.GetSystemMetrics(Native.SM_CYCURSOR),
                Native.GetDpiForSystem()));

            int opaque = 0;
            for (int i = 3; i < shape.Pixels.Length; i += 4)
            {
                if (shape.Pixels[i] != 0) opaque++;
            }
            log.AppendLine(string.Format("non-transparent pixels = {0} / {1}", opaque, shape.Width * shape.Height));

            using (Bitmap bmp = new Bitmap(shape.Width + 32, shape.Height + 32, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(200, 210, 220));
                }

                for (int y = 0; y < shape.Height; y++)
                {
                    for (int x = 0; x < shape.Width; x++)
                    {
                        int si = (y * shape.Width + x) * 4;
                        int a = shape.Pixels[si + 3];
                        if (a == 0) continue;

                        int b = shape.Pixels[si + 0] * 255 / a;
                        int g2 = shape.Pixels[si + 1] * 255 / a;
                        int r2 = shape.Pixels[si + 2] * 255 / a;
                        if (b > 255) b = 255;
                        if (g2 > 255) g2 = 255;
                        if (r2 > 255) r2 = 255;

                        bmp.SetPixel(x + 16, y + 16, Color.FromArgb(a, r2, g2, b));
                    }
                }

                bmp.Save(options.SelfTest, ImageFormat.Png);
            }

            log.AppendLine("saved " + options.SelfTest);
            File.WriteAllText(options.SelfTest + ".log", log.ToString());
        }
    }
}