using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace CspPalette.UI
{
    /// <summary>
    /// 屏幕几何：某一点所在那块显示器的工作区（已经扣掉任务栏）。
    ///
    /// 单独拆出来是因为两块地方都要用：右键召唤面板的摆放（PaletteWindow）和
    /// 放大镜的摆放（GradientSlider → Magnifier）。以前放大镜用的是
    /// <c>SystemParameters.WorkArea</c>——那是**主显示器**的工作区，
    /// 面板拖到第二块屏上时会把放大镜夹到主屏边缘去。
    ///
    /// 单位：正常情况下是**屏幕物理像素**（MonitorFromPoint / GetMonitorInfo 给的）。
    /// 只有系统接口整个失败时退回 WPF 的虚拟屏幕，那时是 DIP——查不到显示器是
    /// 极端情况，不再为它单独做换算。
    /// </summary>
    internal static class ScreenArea
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        /// <summary>
        /// 屏幕坐标 (x, y) 落在哪块显示器上，取那块显示器的工作区。
        /// 坐标是物理像素；拿不到就退回整个虚拟屏幕。
        /// </summary>
        public static void GetWorkArea(int x, int y,
                                       out double left, out double top,
                                       out double right, out double bottom)
        {
            POINT p;
            p.X = x;
            p.Y = y;

            MONITORINFO info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            IntPtr monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                left = info.rcWork.Left;
                top = info.rcWork.Top;
                right = info.rcWork.Right;
                bottom = info.rcWork.Bottom;
                return;
            }

            left = SystemParameters.VirtualScreenLeft;
            top = SystemParameters.VirtualScreenTop;
            right = left + SystemParameters.VirtualScreenWidth;
            bottom = top + SystemParameters.VirtualScreenHeight;
        }
    }
}
