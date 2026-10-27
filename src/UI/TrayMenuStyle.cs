// TrayMenuStyle.cs
// 托盘右键菜单的深色外观（黑底白字、去掉图标槽、四周留白收窄）。
//
// 面板本身是 WPF + Theme.cs 那套配色；托盘菜单是这个程序里唯一一块
// WinForms 界面（NotifyIcon 只认 WinForms 的 ContextMenuStrip），
// 默认渲染出来是浅灰底、蓝高亮、左边还留一条 24px 的图标槽，
// 跟深色面板摆在一起很跳，所以在这里单独定制。

using System;
using System.Drawing;
using System.Windows.Forms;

namespace CspPalette.UI
{
    /// <summary>
    /// 托盘菜单的统一外观。所有颜色、内边距都收在这一个文件里，
    /// 以后想再调只动这里，不用碰 PaletteWindow。
    /// </summary>
    internal static class TrayMenuStyle
    {
        // ---- 配色 ----
        /// <summary>菜单底色：比面板（#3C3C3C）更黑，落在画布上能分清层次。</summary>
        internal static readonly Color Background = Color.FromArgb(0x1E, 0x1E, 0x1E);
        /// <summary>外框：只比底色亮一点，起个收边作用。</summary>
        internal static readonly Color Border = Color.FromArgb(0x3A, 0x3A, 0x3A);
        /// <summary>鼠标划过时的整行底色。</summary>
        internal static readonly Color Hover = Color.FromArgb(0x3D, 0x3D, 0x3D);
        /// <summary>按下去那一刻的底色，比划过再亮一点。</summary>
        internal static readonly Color Pressed = Color.FromArgb(0x4A, 0x4A, 0x4A);
        /// <summary>文字：白。</summary>
        internal static readonly Color Text = Color.White;
        /// <summary>禁用项文字（已连接时的「重新扫描」）。</summary>
        internal static readonly Color DisabledText = Color.FromArgb(0x8A, 0x8A, 0x8A);

        /// <summary>
        /// 建一个已经调好色的空菜单。
        ///
        /// 紧凑的关键是关掉左右两条默认留白：
        ///   · ShowImageMargin 去掉左边那条 24px 的图标槽；
        ///   · ShowCheckMargin 去掉右边给勾选标记留的空位。
        /// 菜单里既没有图标也没有勾，留着这两条只会把文字推得离边框很远。
        /// </summary>
        internal static ContextMenuStrip CreateMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new Renderer();
            menu.BackColor = Background;
            menu.ForeColor = Text;
            menu.Font = new Font("Microsoft YaHei UI", 9F);
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            // 注意：ContextMenuStrip 布局时会把自己的默认内边距（左 8 / 上 2 / 右 1 / 下 2）
            // 重新算回去，这里设 Padding 是白设；Finish() 里按它实际的值来算尺寸。
            return menu;
        }

        /// <summary>往菜单里加一项，顺便套上紧凑的内边距。</summary>
        internal static ToolStripMenuItem AddItem(ContextMenuStrip menu, string text)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.ForeColor = Text;
            // 上下 2px、左右 10px（96 DPI 下）：文字离边框只剩一条缝，
            // 但点击区域还算宽裕，不至于点错
            item.Margin = Padding.Empty;
            item.Padding = new Padding(Scale(10), Scale(2), Scale(10), Scale(2));
            menu.Items.Add(item);
            return item;
        }

        /// <summary>分隔线：别把上下撑开，贴着项与项之间就行。</summary>
        internal static void AddSeparator(ContextMenuStrip menu)
        {
            ToolStripSeparator line = new ToolStripSeparator();
            line.Margin = new Padding(Scale(2), Scale(3), Scale(2), Scale(3));
            menu.Items.Add(line);
        }

        /// <summary>
        /// 所有项都加完之后调一次，把菜单尺寸钉死。
        ///
        /// ContextMenuStrip 自己算宽度时根本不看项的内边距：它只按文字宽度再加一圈
        /// 固定余量，于是右边空出来的地方总比左边宽一大截（实测左边 13px、右边 38px），
        /// 看起来像文字偏左、菜单右边拖着一条尾巴。这里干脆自己算：
        /// 左边留多少，右边就留多少 —— 边框 1 + 项内边距，两边各一份，
        /// 中间正好放下最宽的那句话（实测改完左边 10px、右边 12px）。
        /// </summary>
        internal static void Finish(ContextMenuStrip menu)
        {
            int text = 0;
            int height = menu.Padding.Vertical;
            foreach (ToolStripItem item in menu.Items)
            {
                height += item.GetPreferredSize(Size.Empty).Height + item.Margin.Vertical;
                if (item is ToolStripSeparator) continue;
                if (!string.IsNullOrEmpty(item.Text))
                {
                    text = Math.Max(text, Measure(item.Text, item.Font));
                }
            }

            int inset = 1 + Scale(10);   // 边框 1 + 项内边距；菜单自己那圈 padding 不算给项
            menu.AutoSize = false;
            menu.Width = inset * 2 + text;
            menu.Height = height;
        }

        /// <summary>量文字宽度。用排版宽度，不要 TextRenderer 默认那圈测量留白。</summary>
        private static int Measure(string text, Font font)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                SizeF size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
                return (int)Math.Ceiling(size.Width);
            }
        }

        /// <summary>
        /// 高 DPI 缩放。程序是 PerMonitorV2 的，字号会跟着 DPI 变，
        /// 内边距如果写死像素，在 150% 屏上就会显得过窄，所以按 DPI 折算一次。
        /// </summary>
        private static int Scale(int value)
        {
            if (value == 0) return 0;
            if (_dpi <= 0)
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    _dpi = g.DpiX;
                }
            }
            return (int)Math.Round(value * _dpi / 96.0);
        }

        private static float _dpi;

        /// <summary>深色调色板：把 Professional 渲染器默认那套浅色全部换掉。</summary>
        private sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Background; } }
            public override Color ImageMarginGradientBegin { get { return Background; } }
            public override Color ImageMarginGradientMiddle { get { return Background; } }
            public override Color ImageMarginGradientEnd { get { return Background; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Pressed; } }
            public override Color MenuItemPressedGradientMiddle { get { return Pressed; } }
            public override Color MenuItemPressedGradientEnd { get { return Pressed; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Border; } }
        }

        /// <summary>渲染器：颜色交给 Colors，文字自己上色（禁用项要压暗一档）。</summary>
        private sealed class Renderer : ToolStripProfessionalRenderer
        {
            internal Renderer() : base(new Colors())
            {
                // 圆角边框那套是给工具栏设计的，下拉菜单上用不着
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Text : DisabledText;
                base.OnRenderItemText(e);
            }
        }
    }
}
