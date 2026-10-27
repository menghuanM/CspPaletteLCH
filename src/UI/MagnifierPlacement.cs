using System;

namespace CspPalette.UI
{
    /// <summary>
    /// 放大镜摆在哪：贴在正在拖的那一行**正上方**，横向对准当前值。
    ///
    /// 纯粹是几何，不碰 WPF/Win32，好让断言测试直接调它（tests\MagnifierPlacementTest）。
    /// 所有坐标和尺寸都是 **DIP**：WPF 的 Left/Top/Width/Height 就是 DIP，
    /// 换算（÷ 显示器缩放）由调用方在拿屏幕坐标时做掉，这里只管摆。
    ///
    /// 规则：
    ///   1. 默认整个挂在滑条那一行的上方，尖角离行顶留一条缝；
    ///   2. 上面放不下（面板贴着屏幕顶边）就翻到这一行下方，尖角朝上；
    ///   3. 最后夹进**这一行所在那块显示器**的工作区，不许被屏幕边裁掉。
    /// </summary>
    public static class MagnifierPlacement
    {
        /// <summary>尖角离滑条顶部的空隙（DIP）。</summary>
        public const double DefaultGap = 2.0;

        /// <summary>贴到工作区边上时至少留这么多，免得边框被屏幕边吃掉。</summary>
        public const double EdgeMargin = 2.0;

        /// <summary>
        /// 算放大镜左上角该放哪。
        /// </summary>
        /// <param name="anchorX">当前值在屏幕上的横坐标（放大镜水平居中到这里）。</param>
        /// <param name="rowTop">滑条那一行的上边界（屏幕纵坐标）。</param>
        /// <param name="rowHeight">那一行的高度。</param>
        /// <param name="width">放大镜宽度。</param>
        /// <param name="height">放大镜高度（含尖角）。</param>
        /// <param name="workLeft">工作区左边。</param>
        /// <param name="workTop">工作区上边。</param>
        /// <param name="workRight">工作区右边。</param>
        /// <param name="workBottom">工作区下边。</param>
        /// <param name="gap">尖角和滑条之间的空当。</param>
        /// <param name="left">结果：放大镜左边界。</param>
        /// <param name="top">结果：放大镜上边界。</param>
        /// <param name="above">结果：true = 在滑条上方（尖角朝下）；false = 翻到了下方（尖角朝上）。</param>
        public static void Place(double anchorX,
                                 double rowTop, double rowHeight,
                                 double width, double height,
                                 double workLeft, double workTop, double workRight, double workBottom,
                                 double gap,
                                 out double left, out double top, out bool above)
        {
            if (gap < 0) gap = 0;
            if (width < 0) width = 0;
            if (height < 0) height = 0;
            if (rowHeight < 0) rowHeight = 0;

            left = anchorX - width / 2.0;
            top = rowTop - gap - height;
            above = true;

            // 上面放不下（滑条贴着屏幕顶边）：翻到这一行下方，尖角朝上。
            // 这里只做兜底，正常情况不会走到。
            if (top < workTop + EdgeMargin)
            {
                top = rowTop + rowHeight + gap;
                above = false;
            }

            // 夹进工作区：宁可贴着边，也别让放大镜跑到屏幕外面去
            left = Clamp(left, workLeft + EdgeMargin, workRight - EdgeMargin - width);
            top = Clamp(top, workTop + EdgeMargin, workBottom - EdgeMargin - height);
        }

        /// <summary>带默认空当的版本。</summary>
        public static void Place(double anchorX,
                                 double rowTop, double rowHeight,
                                 double width, double height,
                                 double workLeft, double workTop, double workRight, double workBottom,
                                 out double left, out double top, out bool above)
        {
            Place(anchorX, rowTop, rowHeight, width, height,
                  workLeft, workTop, workRight, workBottom, DefaultGap,
                  out left, out top, out above);
        }

        /// <summary>夹到 [lo, hi]；区间反过来（放大镜比工作区还大）时优先服从 lo。</summary>
        private static double Clamp(double v, double lo, double hi)
        {
            if (hi < lo) hi = lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
