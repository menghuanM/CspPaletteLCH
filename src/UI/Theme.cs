using System.Windows.Media;

namespace CspPalette.UI
{
    /// <summary>面板的配色与字体，集中放一处方便统一调整。</summary>
    public static class Theme
    {
        public static readonly Brush PanelBackground = Frozen(0x3C, 0x3C, 0x3C);
        public static readonly Brush PanelBorder = Frozen(0x2A, 0x2A, 0x2A);

        public static readonly Brush StatusText = Frozen(0x9A, 0x9A, 0x9A);

        /// <summary>标题栏右上角那两个小按钮的字形和悬停底色。</summary>
        public static readonly Brush CaptionGlyph = Frozen(0xD0, 0xD0, 0xD0);
        public static readonly Brush CaptionButtonHover = Frozen(0x55, 0x55, 0x55);

        /// <summary>被锁定的滑块套一圈红边。</summary>
        public static readonly Brush LockBorder = Frozen(0xE0, 0x3B, 0x3B);
        /// <summary>正在被拖动的那条滑块套一圈白边。</summary>
        public static readonly Brush DragBorder = Frozen(0xFF, 0xFF, 0xFF);

        /// <summary>滑块右边数值框里的数字和箭头。</summary>
        public static readonly Brush FieldText = Frozen(0xE6, 0xE6, 0xE6);
        /// <summary>数值框里那两个箭头所在的浅色小方块。</summary>
        public static readonly Brush SpinBackground = Frozen(0x4A, 0x4A, 0x4A);
        /// <summary>滑块左边的轴名（L / C / H）。</summary>
        public static readonly Brush AxisLabel = Frozen(0xC8, 0xC8, 0xC8);

        /// <summary>滑块手柄（轨道下面那个小三角）。</summary>
        public static readonly Brush MarkerFill = Frozen(0xE6, 0xE6, 0xE6);
        /// <summary>手柄的描边：停在渐变亮的一端（明度条右端是纯白）时纯浅色会看不见。</summary>
        public static readonly Brush MarkerEdge = Frozen(0x1E, 0x1E, 0x1E);

        public static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI, Segoe UI");

        private static Brush Frozen(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}