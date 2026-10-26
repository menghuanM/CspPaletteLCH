using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CspPalette.UI
{
    /// <summary>
    /// 拖滑条时弹出来的放大镜：把光标附近那段渐变放大画一遍，
    /// 让人看清"再挪一个像素会变成什么颜色"。
    ///
    /// 形状是**倒水滴**（上宽下尖），尖头指着下面那条滑条，
    /// 一眼就能看出它在说哪一行。
    ///
    /// 为什么必须是独立的窗口，而不是面板里的一个元素：
    /// 主面板只有 80~130px 高，放大镜要出现在滑条**上方**，那块地方在窗口外面。
    /// 挂在 _grid 里会被窗口边界直接裁掉（试过，什么都看不见）。
    ///
    /// 和临时取色面板同样的窗口风格：NOACTIVATE + TOOLWINDOW，
    /// 不抢 CSP 前台、不进任务栏和 Alt+Tab。它只是"看一眼"，不该有任何存在感。
    /// </summary>
    public class Magnifier : Window
    {
        /// <summary>按住多久之后才弹出来。太短会让人"刚点一下就被挡住"。</summary>
        public const int ShowDelayMs = 420;

        /// <summary>
        /// 放大倍数：窗口宽度覆盖原轨道的多少分之一。
        ///
        /// 12 倍是试出来的：L 滑条在低彩度时的颜色变化本来就很小，
        /// 6 倍放大之后左右两端几乎看不出差别（截图上是一整片橙），
        /// 放大镜就失去了意义。12 倍能明显看出"往左更暗、往右更亮"。
        /// </summary>
        private const double Zoom = 12.0;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private readonly DispatcherTimer _timer;
        private readonly MagnifierSurface _surface;

        private bool _armed;
        private Func<double, Color> _colorAt;
        private Func<double, bool> _inGamutAt;
        private double _focus;

        public Magnifier()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;       // 不抢前台
            ResizeMode = ResizeMode.NoResize;
            Title = "CspPaletteMagnifier";
            Focusable = false;

            _surface = new MagnifierSurface(this);
            _surface.IsHitTestVisible = false;
            Content = _surface;

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(ShowDelayMs);
            _timer.Tick += delegate { OnDelayElapsed(); };

            SourceInitialized += delegate
            {
                try
                {
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    if (h == IntPtr.Zero) return;
                    int ex = GetWindowLong(h, GWL_EXSTYLE);
                    SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                }
                catch (Exception)
                {
                }
            };
        }

        /// <summary>放大镜是不是正在显示。</summary>
        public bool IsShowing { get; private set; }

        /// <summary>整个窗口的宽度（含水滴的底宽）。</summary>
        public double BoxWidth { get; set; }

        /// <summary>水滴主体的高度（不含下方尖角）。</summary>
        public double BodyHeight { get; set; }

        /// <summary>尖角的高度。整个窗口高度 = BodyHeight + TailHeight。</summary>
        public double TailHeight { get; set; }

        /// <summary>
        /// 按住滑条时调用。第一次调用起计时，到点才弹出来；
        /// 之后每次都只更新焦点，不重新计时（否则手一直微动就永远等不到弹出）。
        /// </summary>
        public void Arm(Func<double, Color> colorAt, Func<double, bool> inGamutAt, double focus)
        {
            _colorAt = colorAt;
            _inGamutAt = inGamutAt;
            _focus = focus;

            if (IsShowing)
            {
                Reposition();
                _surface.InvalidateVisual();
                return;
            }

            if (_armed) return;      // 已经在等，别重置
            _armed = true;
            _timer.Start();
        }

        /// <summary>松手 / 取消：立刻收起。放大镜不该有"残留"的观感。</summary>
        public void Disarm()
        {
            _armed = false;
            _timer.Stop();
            if (!IsShowing) return;
            IsShowing = false;
            Hide();
        }

        private void OnDelayElapsed()
        {
            _timer.Stop();
            if (!_armed) return;

            double w = BoxWidth > 1 ? BoxWidth : 120.0;
            double body = BodyHeight > 1 ? BodyHeight : 28.0;
            double tail = TailHeight > 1 ? TailHeight : 8.0;

            Width = w;
            Height = body + tail;

            IsShowing = true;
            Reposition();
            Opacity = 0.0;
            Show();
            BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(1.0,
                    new Duration(TimeSpan.FromMilliseconds(90))));
            _surface.InvalidateVisual();
        }

        /// <summary>
        /// 摆在**正在拖的那条滑条**正上方：水滴的尖角刚好落在滑条顶部，
        /// 横向对准"当前值对应的轨道位置"。
        /// </summary>
        private void Reposition()
        {
            if (_trackWidth <= 1) return;

            double cx = _hostScreenX + _trackLeft + _trackWidth * _focus;
            double dipLeft = cx - Width / 2.0;

            // 尖角要落在滑条**顶部**，所以窗口底边 = 滑条顶部 - 一点点缝
            double dipTop = _rowScreenY - Height - Gap;

            // 夹到工作区里：贴边时不能跑出屏幕
            double vLeft = SystemParameters.WorkArea.Left;
            double vTop = SystemParameters.WorkArea.Top;
            double vRight = SystemParameters.WorkArea.Right;

            if (dipLeft < vLeft + 2) dipLeft = vLeft + 2;
            if (dipLeft + Width > vRight - 2) dipLeft = vRight - 2 - Width;

            // 上面实在放不下（滑条贴着屏幕顶边）：翻到这条滑条下方，
            // 尖角朝上。这里只做兜底，正常情况不会走到。
            if (dipTop < vTop + 2)
            {
                dipTop = _rowScreenY + _rowScreenH + Gap;
                _surface.Flipped = true;
            }
            else
            {
                _surface.Flipped = false;
            }

            Left = Math.Round(dipLeft);
            Top = Math.Round(dipTop);
        }

        // ---- 由面板在布局变化时喂进来 ----

        /// <summary>滑条轨道左端在宿主（面板）里的 x。</summary>
        private double _trackLeft;
        /// <summary>滑条轨道的宽度。</summary>
        private double _trackWidth;
        /// <summary>当前这条滑条那**一行**的屏幕 y（放大镜要贴到它上方）。</summary>
        private double _rowScreenY;
        /// <summary>那一行的高度。</summary>
        private double _rowScreenH;
        /// <summary>宿主窗口左上角的屏幕坐标。</summary>
        private double _hostScreenX;

        /// <summary>尖角离滑条顶部的空隙。</summary>
        private const double Gap = 2.0;

        /// <summary>
        /// 每次按下 / 拖动时由滑条调用：告诉放大镜"我在哪一行、轨道多宽"。
        /// </summary>
        public void SetAnchor(double trackLeft, double trackWidth,
                              double rowScreenY, double rowScreenH, double hostScreenX)
        {
            _trackLeft = trackLeft;
            _trackWidth = trackWidth;
            _rowScreenY = rowScreenY;
            _rowScreenH = rowScreenH;
            _hostScreenX = hostScreenX;
            if (IsShowing) Reposition();
        }

        /// <summary>真正画放大内容的那块（倒水滴）。</summary>
        private sealed class MagnifierSurface : FrameworkElement
        {
            private readonly Magnifier _owner;

            public MagnifierSurface(Magnifier owner)
            {
                _owner = owner;
            }

            /// <summary>尖角朝上（贴顶翻到下方时用）。</summary>
            public bool Flipped;

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth;
                double h = ActualHeight;
                if (w <= 1 || h <= 1) return;
                if (_owner._colorAt == null) return;

                double tail = _owner.TailHeight > 1 ? _owner.TailHeight : 8.0;
                if (tail > h * 0.5) tail = h * 0.5;
                double radius = Theme.MagnifierCornerRadius;
                double inset = Theme.MagnifierInset;

                // 水滴**占满整个窗口高度**（含尾尖）。早先只画到 bodyH，
                // 尾巴那块留成空白，看起来就是个圆角矩形。
                Geometry shape = BuildDroplet(w, h, radius, tail, Flipped, 0);

                // 背板
                dc.PushClip(shape);
                dc.DrawRectangle(Theme.MagnifierBackground, null, new Rect(0, 0, w, h));
                dc.Pop();

                // ---- 放大的渐变：以当前值为中心，横向覆盖 1/Zoom 的原轨道 ----
                double half = 1.0 / (2.0 * Zoom);
                double lo = _owner._focus - half;
                double hi = _owner._focus + half;

                LinearGradientBrush brush = new LinearGradientBrush();
                brush.StartPoint = new Point(0, 0);
                brush.EndPoint = new Point(1, 0);
                brush.MappingMode = BrushMappingMode.RelativeToBoundingBox;

                const int Steps = 48;
                for (int i = 0; i <= Steps; i++)
                {
                    double u = (double)i / Steps;
                    double t = lo + (hi - lo) * u;

                    Color c;
                    if (t < 0 || t > 1)
                    {
                        // 出了滑条范围：用背板色，一眼看出到头了
                        c = Theme.MagnifierBackgroundColor;
                    }
                    else
                    {
                        try { c = _owner._colorAt(t); }
                        catch (Exception) { c = Colors.Gray; }

                        if (_owner._inGamutAt != null)
                        {
                            bool inGamut = true;
                            try { inGamut = _owner._inGamutAt(t); }
                            catch (Exception) { inGamut = true; }
                            if (!inGamut) c = Color.FromRgb(0x8A, 0x8A, 0x8A);
                        }
                    }
                    brush.GradientStops.Add(new GradientStop(c, u));
                }

                // 渐变铺在"内缩一圈"的水滴里，露出一圈深色当边框
                double iw = w - inset * 2;
                double ih = h - inset * 2;
                if (iw > 2 && ih > 4)
                {
                    Geometry inner = BuildDroplet(iw, ih, Math.Max(1, radius - inset),
                        Math.Max(2, tail - inset * 0.5), Flipped, 0);
                    dc.PushTransform(new TranslateTransform(inset, inset));
                    dc.PushClip(inner);
                    dc.DrawRectangle(brush, null, new Rect(0, 0, iw, ih));
                    dc.Pop();
                    dc.Pop();
                }

                // 中间一条竖线：标出当前值落在这段放大的哪个位置
                double midX = Math.Round(w / 2.0);
                dc.DrawLine(new Pen(Theme.MagnifierCenterLine, 1.0),
                    new Point(midX, inset + 1),
                    new Point(midX, Math.Max(inset + 2, h - tail - inset)));

                // 外框
                dc.DrawGeometry(null, new Pen(Theme.MagnifierBorder, 1.0),
                    BuildDroplet(w - 1, h - 1, radius, tail - 0.5, Flipped, 0.5));
            }

            /// <summary>
            /// 倒水滴：**整条侧边都在往内收**，不是"直筒 + 小尾巴"。
            ///
            /// 早先两侧从上到下都是直的，只有最后几行才收，渲染出来核对过：
            /// 前 38 行宽度一直是满宽 120，最后 8 行才从 118 掉到 44 ——
            /// 看起来就是个圆角矩形加一个三角，完全不像水滴。
            ///
            /// 现在从顶部往下约 40% 处开始，两侧用贝塞尔平滑收到最底下一点。
            /// flipped 时上下翻转（尖朝上），给"贴屏幕顶边"的兜底用。
            /// </summary>
            private static Geometry BuildDroplet(double w, double h, double r,
                                                 double tail, bool flipped, double offset)
            {
                double top = offset;
                double bottom = offset + h;
                double left = offset;
                double right = offset + w;
                double cx = (left + right) / 2.0;

                if (r > w / 2.0) r = w / 2.0;
                if (r > h / 4.0) r = h / 4.0;

                // 侧面开始明显内收的位置
                double shoulder = top + h * 0.40;

                StreamGeometry geo = new StreamGeometry();
                using (StreamGeometryContext ctx = geo.Open())
                {
                    if (!flipped)
                    {
                        ctx.BeginFigure(new Point(left + r, top), true, true);
                        ctx.LineTo(new Point(right - r, top), true, false);
                        ctx.ArcTo(new Point(right, top + r), new Size(r, r), 0, false,
                                  SweepDirection.Clockwise, true, false);

                        // 右外侧：直一小段，然后用两段贝塞尔一路收到尖
                        ctx.LineTo(new Point(right, shoulder), true, false);
                        ctx.BezierTo(
                            new Point(right, shoulder + h * 0.26),
                            new Point(cx + w * 0.20, bottom - h * 0.16),
                            new Point(cx, bottom), true, false);

                        // 左半边镜像
                        ctx.BezierTo(
                            new Point(cx - w * 0.20, bottom - h * 0.16),
                            new Point(left, shoulder + h * 0.26),
                            new Point(left, shoulder), true, false);

                        ctx.LineTo(new Point(left, top + r), true, false);
                        ctx.ArcTo(new Point(left + r, top), new Size(r, r), 0, false,
                                  SweepDirection.Clockwise, true, false);
                    }
                    else
                    {
                        double shoulderB = bottom - h * 0.40;

                        ctx.BeginFigure(new Point(cx, top), true, true);
                        ctx.BezierTo(
                            new Point(cx + w * 0.20, top + h * 0.16),
                            new Point(right, shoulderB - h * 0.26),
                            new Point(right, shoulderB), true, false);

                        ctx.LineTo(new Point(right, bottom - r), true, false);
                        ctx.ArcTo(new Point(right - r, bottom), new Size(r, r), 0, false,
                                  SweepDirection.Clockwise, true, false);
                        ctx.LineTo(new Point(left + r, bottom), true, false);
                        ctx.ArcTo(new Point(left, bottom - r), new Size(r, r), 0, false,
                                  SweepDirection.Clockwise, true, false);

                        ctx.LineTo(new Point(left, shoulderB), true, false);
                        ctx.BezierTo(
                            new Point(left, shoulderB - h * 0.26),
                            new Point(cx - w * 0.20, top + h * 0.16),
                            new Point(cx, top), true, false);
                    }
                }
                geo.Freeze();
                return geo;
            }
        }
    }
}
