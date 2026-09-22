using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CspPalette.Screen
{
    public struct CaptureRect
    {
        public int X, Y, Width, Height;
    }

    /// <summary>
    /// 用 GDI 截屏并转成灰度，供二维码解码用。不依赖任何第三方库。
    /// </summary>
    public static class ScreenCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public uint[] bmiColors;
        }

        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const uint DIB_RGB_COLORS = 0;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height,
                                          IntPtr srcDc, int srcX, int srcY, int rop);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hBitmap, uint start, uint lines,
                                            byte[] bits, ref BITMAPINFO info, uint usage);

        // ==================================================================
        // 找 CSP 主窗口
        // ==================================================================

        public static readonly string[] CspProcessNames = new string[] { "CLIPStudioPaint" };

        /// <summary>
        /// 找 Clip Studio Paint 的主窗口。找不到返回 IntPtr.Zero。
        /// </summary>
        public static IntPtr FindCspWindow()
        {
            for (int i = 0; i < CspProcessNames.Length; i++)
            {
                Process[] procs;
                try
                {
                    procs = Process.GetProcessesByName(CspProcessNames[i]);
                }
                catch (Exception)
                {
                    continue;
                }
                for (int j = 0; j < procs.Length; j++)
                {
                    try
                    {
                        IntPtr h = procs[j].MainWindowHandle;
                        if (h != IntPtr.Zero) return h;
                    }
                    catch (Exception)
                    {
                        // 忽略
                    }
                    finally
                    {
                        procs[j].Dispose();
                    }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>取窗口在屏幕上的矩形（含边框）。</summary>
        public static bool TryGetWindowRect(IntPtr hwnd, out CaptureRect rect)
        {
            rect = new CaptureRect();
            if (hwnd == IntPtr.Zero) return false;
            RECT r;
            if (!GetWindowRect(hwnd, out r)) return false;
            rect.X = r.Left;
            rect.Y = r.Top;
            rect.Width = r.Right - r.Left;
            rect.Height = r.Bottom - r.Top;
            return rect.Width > 0 && rect.Height > 0;
        }

        /// <summary>整个虚拟桌面的矩形。</summary>
        public static CaptureRect GetVirtualScreenRect()
        {
            CaptureRect r = new CaptureRect();
            r.X = GetSystemMetrics(76);   // SM_XVIRTUALSCREEN
            r.Y = GetSystemMetrics(77);   // SM_YVIRTUALSCREEN
            r.Width = GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
            r.Height = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
            if (r.Width <= 0 || r.Height <= 0)
            {
                r.X = 0; r.Y = 0;
                r.Width = GetSystemMetrics(0);
                r.Height = GetSystemMetrics(1);
            }
            return r;
        }

        // ==================================================================
        // 截屏 -> 灰度
        // ==================================================================

        /// <summary>
        /// 截取屏幕上一块区域，输出灰度数组（行优先，每像素一字节，0 = 黑，255 = 白）。
        /// 失败返回 null。调用方可以通过 maxDimension 限制输出尺寸以加快速度。
        /// </summary>
        public static byte[] CaptureGray(CaptureRect region, int maxDimension, out int outWidth, out int outHeight)
        {
            outWidth = 0;
            outHeight = 0;

            if (region.Width <= 0 || region.Height <= 0) return null;

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return null;

                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return null;

                bitmap = CreateCompatibleBitmap(screenDc, region.Width, region.Height);
                if (bitmap == IntPtr.Zero) return null;

                oldBitmap = SelectObject(memDc, bitmap);

                bool ok = BitBlt(memDc, 0, 0, region.Width, region.Height,
                                 screenDc, region.X, region.Y, SRCCOPY | CAPTUREBLT);
                if (!ok) return null;

                BITMAPINFO info = new BITMAPINFO();
                info.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                info.bmiHeader.biWidth = region.Width;
                info.bmiHeader.biHeight = -region.Height;  // 负数 = 自顶向下
                info.bmiHeader.biPlanes = 1;
                info.bmiHeader.biBitCount = 32;
                info.bmiHeader.biCompression = 0;          // BI_RGB
                info.bmiColors = new uint[256];

                int stride = region.Width * 4;
                byte[] pixels = new byte[stride * region.Height];
                int lines = GetDIBits(memDc, bitmap, 0, (uint)region.Height, pixels, ref info, DIB_RGB_COLORS);
                if (lines == 0) return null;

                return ToGrayScaled(pixels, region.Width, region.Height, maxDimension, out outWidth, out outHeight);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>
        /// BGRA 像素转灰度，必要时做整数倍降采样（取块内平均，避免丢失细小模块）。
        /// </summary>
        private static byte[] ToGrayScaled(byte[] bgra, int width, int height, int maxDimension,
                                           out int outWidth, out int outHeight)
        {
            int step = 1;
            if (maxDimension > 0)
            {
                while (width / (step * 2) >= maxDimension && height / (step * 2) >= maxDimension)
                {
                    step *= 2;
                }
            }

            if (step == 1)
            {
                byte[] gray = new byte[width * height];
                for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
                {
                    int b = bgra[p];
                    int g = bgra[p + 1];
                    int r = bgra[p + 2];
                    gray[i] = (byte)((r * 77 + g * 151 + b * 28) >> 8);
                }
                outWidth = width;
                outHeight = height;
                return gray;
            }

            int w = width / step;
            int h = height / step;
            byte[] result = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < step; dy++)
                    {
                        int row = (y * step + dy) * width;
                        for (int dx = 0; dx < step; dx++)
                        {
                            int p = (row + x * step + dx) * 4;
                            int b = bgra[p];
                            int g = bgra[p + 1];
                            int r = bgra[p + 2];
                            sum += (r * 77 + g * 151 + b * 28) >> 8;
                        }
                    }
                    result[y * w + x] = (byte)(sum / (step * step));
                }
            }
            outWidth = w;
            outHeight = h;
            return result;
        }
    }
}