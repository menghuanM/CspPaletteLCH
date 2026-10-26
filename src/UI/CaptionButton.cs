using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CspPalette.UI
{
    /// <summary>
    /// 标题栏右上角的小按钮（📌 锁定 / — 折叠 / ✕ 收起），照 CSP 的"颜色滑块"面板：
    /// 平时只有一个浅灰字形，鼠标移上去整块变亮。
    /// 不用 WPF 的 Button，省掉控件模板那一套，样式也好跟着面板一起缩放。
    ///
    /// 有两种画法：
    ///   · 文字字形（— ✕）—— 直接用 TextBlock
    ///   · 自绘图形（📌）—— emoji 在不同系统上字形差很多、还不跟着字号缩放，
    ///     所以图钉是自己画的线稿，尺寸和颜色都能跟着按钮走
    /// </summary>
    public class CaptionButton : Border
    {
        /// <summary>字形怎么画。</summary>
        public enum GlyphKind
        {
            /// <summary>文字字形（— ✕ 这类）。</summary>
            Text,
            /// <summary>自绘的图钉线稿。</summary>
            Pin
        }

        private readonly GlyphKind _kind;
        private readonly TextBlock _glyph;
        private readonly PinVisual _pin;

        private bool _hover;
        private bool _pressed;
        private bool _active;

        public event EventHandler Click;

        public CaptionButton(string glyph)
        {
            _kind = GlyphKind.Text;
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;

            _glyph = new TextBlock();
            _glyph.Text = glyph;
            _glyph.Foreground = Theme.CaptionGlyph;
            _glyph.FontFamily = Theme.UiFont;
            _glyph.HorizontalAlignment = HorizontalAlignment.Center;
            _glyph.VerticalAlignment = VerticalAlignment.Center;
            _glyph.IsHitTestVisible = false;
            Child = _glyph;

            WireMouse();
        }

        /// <summary>建一个自绘图形的按钮（图钉）。</summary>
        public CaptionButton(GlyphKind kind)
        {
            _kind = kind;
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;

            _pin = new PinVisual();
            _pin.Foreground = Theme.CaptionGlyph;
            _pin.IsHitTestVisible = false;

            // 初始状态要和外部看到的 _active 一致（false）。
            // 少了这一句，外面调 IsActive = false 时会被 "值没变就直接 return" 挡掉，
            // PinVisual 内部还停在 true，于是禁用状态永远不画斜线。
            _pin.Active = _active;
            Child = _pin;

            WireMouse();
        }

        private void WireMouse()
        {
            MouseEnter += delegate { _hover = true; UpdateVisual(); };
            MouseLeave += delegate { _hover = false; UpdateVisual(); };
            MouseLeftButtonDown += OnMouseDown;
            MouseLeftButtonUp += OnMouseUp;
        }

        /// <summary>
        /// 按钮是否处于"已启用"状态。
        ///
        /// 对图钉来说这一位决定了画法：**启用 = 正常图钉**（表示"已锁定"），
        /// **禁用 = 图钉上压一道斜线**（表示"没锁"）。
        /// 和鼠标悬停不同：鼠标移开之后状态仍然看得出来。
        /// </summary>
        public bool IsActive
        {
            get { return _active; }
            set
            {
                if (_active == value) return;
                _active = value;
                if (_pin != null) _pin.Active = value;
                UpdateVisual();
            }
        }

        /// <summary>缩放时定尺寸：只给高度，宽度和字形按它等比算。</summary>
        public void SetSize(double height)
        {
            double w = Math.Round(height * 1.45);
            if (Math.Abs(Width - w) < 0.5 && Math.Abs(Height - height) < 0.5) return;
            Width = w;
            Height = height;

            if (_kind == GlyphKind.Text && _glyph != null)
            {
                _glyph.FontSize = Math.Round(height * 0.60);
            }
            if (_pin != null)
            {
                _pin.InvalidateVisual();
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _pressed = true;
            UpdateVisual();
            // 抓住鼠标，这样即使移到按钮外面再松开也还能收到 MouseUp
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            bool wasPressed = _pressed;
            _pressed = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            UpdateVisual();
            e.Handled = true;

            if (!wasPressed) return;

            // 松开时指针已经移出按钮范围就当取消
            Point p = e.GetPosition(this);
            if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight) return;

            if (Click != null) Click(this, EventArgs.Empty);
        }

        private void UpdateVisual()
        {
            bool lit = _hover || _pressed;

            // 文字按钮（— ✕）：启用时保持常亮底色，表示"这个是当前生效的"
            // 图钉按钮：启用应该画正常图钉、禁用画带斜线的，所以**不**给它常亮底色——
            // 否则"亮着"和"锁着"两个语义会撞在一起，反而看不出到底锁没锁。
            bool wantActiveBg = (_kind == GlyphKind.Text) && _active;

            Background = lit ? Theme.CaptionButtonHover
                       : (wantActiveBg ? Theme.CaptionButtonActive : Brushes.Transparent);

            Brush fg = (lit || wantActiveBg) ? Brushes.White : Theme.CaptionGlyph;
            if (_glyph != null) _glyph.Foreground = fg;
            if (_pin != null)
            {
                _pin.Foreground = fg;
                _pin.InvalidateVisual();
            }
        }

        /// <summary>
        /// 自绘的图钉线稿（竖着的一枚图钉：圆头 + 尖脚）。
        ///
        /// 为什么不用 emoji 📌：它在不同系统/字体下字形差很大，有些字体根本不显示，
        /// 而且不跟着 FontSize 缩放——面板放大时图钉还是个小组件，很突兀。
        /// 自己画就能按按钮尺寸等比缩放，颜色也跟主题走。
        /// 未锁定时在它上面画一道斜线（跟"禁用"的通用符号一致）。
        /// </summary>
        private sealed class PinVisual : FrameworkElement
        {
            private bool _active = true;
            private Brush _fg = Brushes.White;

            public bool Active
            {
                get { return _active; }
                set { if (_active != value) { _active = value; InvalidateVisual(); } }
            }

            public Brush Foreground
            {
                get { return _fg; }
                set { if (!ReferenceEquals(_fg, value)) { _fg = value; InvalidateVisual(); } }
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth;
                double h = ActualHeight;
                if (w <= 2 || h <= 2) return;

                // 图形要旋转 45° 摆（斜着才像图钉），所以可用尺寸得按**旋转后的
                // 包围盒**算：竖着时长 s、宽 2*capW 的图形，转过 45° 之后
                // 对角线约为 s*1.414，必须放进 min(w,h) 里。
                // 早先直接用 min(w,h)*0.94，旋转后两头都被裁掉了。
                double s = Math.Min(w, h) * 0.72;
                if (s <= 1) return;

                double cx = w / 2.0;
                double cy = h / 2.0;

                // 经典图钉（大头针）的轮廓，斜着放：钉帽在**右上**、针尖指**左下**。
                // 结构从上到下：上盘 → 细腰 → 下盘 → 针。
                // **只画轮廓线，不填色**（用户："只要边框线条，不要颜色"）。
                //
                // 角度是 +45（原来是 -45）：这就是用户要的"按中心水平镜像"。
                // 图钉在它自己的坐标系里左右对称，所以没法靠 ScaleTransform(-1,1) 翻——
                // 那样翻完一模一样，只有把旋转方向反过来才看得出镜像。
                // 附带好处：禁用那道斜线走的是左上→右下，现在正好和钉身形成交叉，
                // 不再和钉身平行。
                dc.PushTransform(new TranslateTransform(cx, cy));
                dc.PushTransform(new RotateTransform(45.0));

                // 线宽。20x15 那种小按钮上 2px 就是下限，再细就糊；
                // 这里按 s 走，s 本身已经按旋转包围盒缩过了。
                Pen pen = new Pen(_fg, Math.Max(1.4, s * 0.11));
                pen.StartLineCap = PenLineCap.Round;
                pen.EndLineCap = PenLineCap.Round;
                pen.LineJoin = PenLineJoin.Round;

                // 经典图钉：**上小盘 + 细腰 + 下大盘 + 长针**。
                // 两个盘不一样大：靠钉帽那头（屏幕上在右上）小一点，
                // 靠针那头大一点，才有"大头针"的层次感。
                // （用户："上面扁盘小一点"。）
                double top = -s * 0.50;
                double capTopW = s * 0.22;       // 上盘半宽（小）
                double capTopH = s * 0.10;       // 上盘高
                double capBotW = s * 0.30;       // 下盘半宽（大）
                double capBotH = s * 0.13;       // 下盘高
                double waistW = s * 0.09;        // 腰半宽（收窄）
                double waistH = s * 0.16;        // 腰的高（必须够，否则被圆角吃掉看不见）

                double y0 = top;
                double y1 = y0 + capTopH;        // 上盘底
                double y2 = y1 + waistH;         // 腰底 = 下盘顶
                double y3 = y2 + capBotH;        // 下盘底
                double tipY = top + s;

                // 上盘（小）、下盘（大）：扁圆角矩形，只描边
                dc.DrawRoundedRectangle(null, pen,
                    new Rect(-capTopW, y0, capTopW * 2, capTopH), capTopH * 0.5, capTopH * 0.5);
                dc.DrawRoundedRectangle(null, pen,
                    new Rect(-capBotW, y2, capBotW * 2, capBotH), capBotH * 0.5, capBotH * 0.5);

                // 腰：两条竖线
                dc.DrawLine(pen, new Point(-waistW, y1), new Point(-waistW, y2));
                dc.DrawLine(pen, new Point(waistW, y1), new Point(waistW, y2));

                // 针：一条竖线
                dc.DrawLine(pen, new Point(0, y3), new Point(0, tipY));

                dc.Pop();   // 旋转
                dc.Pop();   // 平移

                // ---- 未锁定：压一道斜线（"禁用"）----
                // 画在旋转外面，直接用屏幕坐标，不用反算角度。
                if (!_active)
                {
                    // 斜线长度按**按钮**尺寸算，不能按 s：s 已经被旋转包围盒缩过一道，
                    // 再拿它算斜线就短得盖不住图形，两种状态看起来一模一样
                    // （冒烟测试抓到过：12 像素 vs 12 像素）。
                    double btn = Math.Min(w, h);
                    Pen slash = new Pen(_fg, Math.Max(1.4, btn * 0.11));
                    slash.StartLineCap = PenLineCap.Round;
                    slash.EndLineCap = PenLineCap.Round;

                    double pad = btn * 0.42;
                    dc.DrawLine(slash,
                        new Point(cx - pad, cy - pad),
                        new Point(cx + pad, cy + pad));
                }
            }
        }
    }
}
