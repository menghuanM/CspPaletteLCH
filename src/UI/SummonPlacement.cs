using System;

namespace CspPalette.UI
{
    /// <summary>
    /// 「右键把面板叫到光标旁边」的摆放算法。
    ///
    /// 纯粹是几何，不碰 WPF/Win32，好让断言测试直接调它。所有坐标都用
    /// <b>屏幕物理像素</b>：鼠标钩子给的、GetMonitorInfo 给的、SetWindowPos 要的
    /// 都是这个单位，中间不掺 DIP，换显示器/改缩放都不会算歪。
    ///
    /// 规则（用户定的）：面板在光标的**右上边**——面板的左下角对着光标右上方一点点；
    /// 右边位置不够就整个翻到光标左侧，上面不够就翻到光标下方，最后再夹进工作区。
    /// </summary>
    public static class SummonPlacement
    {
        /// <summary>光标和面板之间留的空当（物理像素）：正好够看见光标那一下吸色。</summary>
        public const double DefaultGap = 18.0;

        /// <summary>
        /// 算出面板左上角应该放哪。
        /// </summary>
        /// <param name="cursorX">光标 X（屏幕物理像素）。</param>
        /// <param name="cursorY">光标 Y（屏幕物理像素）。</param>
        /// <param name="panelW">面板宽。</param>
        /// <param name="panelH">面板高。</param>
        /// <param name="workLeft">工作区左边（不含任务栏的那块）。</param>
        /// <param name="workTop">工作区上边。</param>
        /// <param name="workRight">工作区右边。</param>
        /// <param name="workBottom">工作区下边。</param>
        /// <param name="gap">光标和面板之间的空当。</param>
        /// <param name="left">结果：面板左边界。</param>
        /// <param name="top">结果：面板上边界。</param>
        public static void Place(double cursorX, double cursorY,
                                 double panelW, double panelH,
                                 double workLeft, double workTop, double workRight, double workBottom,
                                 double gap,
                                 out double left, out double top)
        {
            if (gap < 0) gap = 0;
            if (panelW < 0) panelW = 0;
            if (panelH < 0) panelH = 0;

            // ---- 横向：默认贴光标右边，右边放不下就翻到左边 ----
            left = cursorX + gap;
            if (left + panelW > workRight)
            {
                left = cursorX - gap - panelW;
            }

            // ---- 纵向：默认整个挂在光标上方，上面放不下就翻到下方 ----
            top = cursorY - gap - panelH;
            if (top < workTop)
            {
                top = cursorY + gap;
            }

            // ---- 最后夹进工作区：宁可贴着边，也别把面板甩到屏幕外面 ----
            left = Clamp(left, workLeft, workRight - panelW);
            top = Clamp(top, workTop, workBottom - panelH);
        }

        /// <summary>带默认空当的版本。</summary>
        public static void Place(double cursorX, double cursorY,
                                 double panelW, double panelH,
                                 double workLeft, double workTop, double workRight, double workBottom,
                                 out double left, out double top)
        {
            Place(cursorX, cursorY, panelW, panelH,
                  workLeft, workTop, workRight, workBottom, DefaultGap, out left, out top);
        }

        /// <summary>夹到 [lo, hi]；区间反过来（面板比工作区还大）时优先服从 lo。</summary>
        private static double Clamp(double v, double lo, double hi)
        {
            if (hi < lo) hi = lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
