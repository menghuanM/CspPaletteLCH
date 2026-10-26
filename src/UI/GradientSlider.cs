using System;
using System.Windows;
using System.Windows.Media;

namespace CspPalette.UI
{
    /// <summary>
    /// 一条横向渐变滑块，照 CSP 面板的样式：一条渐变的轨道，正下方一个跟着值走的小三角手柄。
    /// 渐变由外部通过 ColorAt / InGamutAt 回调提供：固定另外两个轴，
    /// 沿本轴采样得到颜色。超出 sRGB 色域的区段按设计渲染成灰色。
    /// 点轨道任意位置即可跳转，按住拖动可连续改值；双击 = 锁定。
    /// </summary>
    public class GradientSlider : FrameworkElement
    {
        /// <summary>t ∈ [0,1]，返回该位置应该显示的颜色。</summary>
        public Func<double, Color> ColorAt;

        /// <summary>t ∈ [0,1]，返回该位置是否在 sRGB 色域内。为 null 表示全部在色域内。</summary>
        public Func<double, bool> InGamutAt;

        /// <summary>
        /// 拖动时把请求值夹到合法范围（锁定其它轴后不让颜色越出 sRGB）。
        /// 为 null 表示不限制。
        /// </summary>
        public Func<double, double> ClampValue;

        private const int GradientSteps = 128;

        // 控件高度按这三段分：轨道 / 轨道与手柄之间的缝 / 手柄。比例照 CSP 截图量出来的观感。
        // **轨道在控件里垂直居中**（用户："色条的中心的和两边的字中心点不在一起"）：
        // 左右两边的轴名和数值都是按行中线摆的，所以轨道中线也必须落在行中线上；
        // 手柄挂在轨道下面，只能占剩下的那点高度，所以比早先矮一些。
        private const double TrackRatio = 0.52;
        private const double MarkerGapRatio = 0.05;
        private const double MarkerHeightRatio = 0.17;
        private const double MarkerWidthRatio = 0.34;

        /// <summary>
        /// 运行时覆盖上面那几个比例。**只有 tools\StyleLab 会碰它**，
        /// 正式运行时全是 NaN，表示"用代码里的常量"。
        ///
        /// 这么做是为了让调参台能直接驱动真的 GradientSlider，而不是画个近似图；
        /// 面板本身一行都不用改。
        /// </summary>
        public sealed class StyleOverrideValues
        {
            public double TrackRatio = double.NaN;
            public double MarkerHeightRatio = double.NaN;
            public double MarkerGapRatio = double.NaN;
        }

        /// <summary>样式覆盖（调参台用）。正式运行时保持全 NaN。</summary>
        public static readonly StyleOverrideValues StyleOverride = new StyleOverrideValues();

        // ---- 单条滑条的覆盖（临时弹出面板用）----
        //
        // 弹窗的三条色条比主面板更粗（用户在调参台里定的 trackRatio = 0.95），
        // 而主面板要保持原来的观感，所以覆盖必须能**按控件**给，不能全局。
        // NaN = 用默认值。

        /// <summary>轨道占控件高度的比例。NaN = 走自适应默认值。</summary>
        public double TrackRatioOverride = double.NaN;
        /// <summary>手柄小三角的高度占比。NaN = 用默认值。</summary>
        public double MarkerHeightRatioOverride = double.NaN;
        /// <summary>轨道与手柄之间的缝占比。NaN = 用默认值。</summary>
        public double MarkerGapRatioOverride = double.NaN;

        private static bool Overridden(double v)
        {
            return !double.IsNaN(v);
        }

        /// <summary>
        /// 轨道实际占控件高度的比例，按优先级取值：
        /// 单控件覆盖 &gt; 调参台全局覆盖 &gt; 按高度自适应的默认值。
        ///
        /// 光用固定的 0.52 不够：面板缩到最小（142 宽）时一行只有 16px 高，
        /// 轨道就只剩 8px，可手柄和小三角还要占地方，结果三条色条之间空得厉害
        /// （用户截图："上下距离太大了"）。
        ///
        /// 所以矮的时候把比例提上去——但也得给手柄留出至少能看清的几像素，
        /// 不能把 16px 全吃掉。高的时候仍按 0.52，保持原来的观感。
        /// </summary>
        private double TrackRatioFor(double controlHeight)
        {
            if (Overridden(TrackRatioOverride)) return TrackRatioOverride;
            if (Overridden(StyleOverride.TrackRatio)) return StyleOverride.TrackRatio;

            if (controlHeight <= 1) return TrackRatio;

            // 手柄至少要这么多：小三角 + 一点缝，不然会被挤成一条线看不见
            double markerReserve = Math.Max(4.0, controlHeight * (MarkerHeightRatioFor() + MarkerGapRatioFor()));

            // 让轨道尽量占满"控件高度 - 手柄预留"，但不超过 0.72
            double roomy = (controlHeight - markerReserve) / controlHeight;
            double byRoom = Math.Min(roomy, 0.72);

            // 控件够高时按老比例；矮的时候取"能占多少占多少"
            return controlHeight >= 28.0 ? TrackRatio : Math.Max(TrackRatio, byRoom);
        }

        private double MarkerHeightRatioFor()
        {
            if (Overridden(MarkerHeightRatioOverride)) return MarkerHeightRatioOverride;
            return Overridden(StyleOverride.MarkerHeightRatio)
                ? StyleOverride.MarkerHeightRatio : MarkerHeightRatio;
        }

        private double MarkerGapRatioFor()
        {
            if (Overridden(MarkerGapRatioOverride)) return MarkerGapRatioOverride;
            return Overridden(StyleOverride.MarkerGapRatio)
                ? StyleOverride.MarkerGapRatio : MarkerGapRatio;
        }

        /// <summary>手柄（小三角）的宽度。</summary>
        private static double MarkerWidth(double controlHeight)
        {
            return Math.Max(7.0, controlHeight * MarkerWidthRatio);
        }

        /// <summary>轨道两端的圆角。</summary>
        private double CornerRadius(double controlHeight)
        {
            double ratio = Overridden(TrackRatioOverride) ? TrackRatioOverride : TrackRatio;
            return Math.Min(Math.Max(2.0, controlHeight * ratio * 0.22), 6.0);
        }

        /// <summary>锁定 / 拖动时那圈边框留出来的余地。</summary>
        private static double EdgeInset(double controlHeight)
        {
            return Math.Max(2.0, controlHeight * 0.06);
        }

        private double _value;
        private bool _dragging;
        private bool _locked;

        private LinearGradientBrush _brush;

        /// <summary>被锁定（双击切换）。锁定的那条滑块画一圈红边框。</summary>
        public bool Locked
        {
            get { return _locked; }
            set
            {
                if (_locked == value) return;
                _locked = value;
                InvalidateVisual();
            }
        }

        /// <summary>双击切换锁定时触发。</summary>
        public event EventHandler LockToggled;

        private void RaiseLockToggled()
        {
            EventHandler handler = LockToggled;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>归一化值 0..1。</summary>
        public double Value
        {
            get { return _value; }
            set
            {
                double v = value;
                if (v < 0) v = 0;
                if (v > 1) v = 1;
                if (Math.Abs(v - _value) < 1e-9) return;
                _value = v;
                InvalidateVisual();
                RaiseValueChanged();
            }
        }

        public event EventHandler ValueChanged;

        /// <summary>用户是不是正按着这条滑块在拖。</summary>
        public bool IsDragging
        {
            get { return _dragging; }
        }

        private void RaiseValueChanged()
        {
            EventHandler handler = ValueChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>另外两个轴变了、渐变需要重画时调用。</summary>
        public void InvalidateGradient()
        {
            _brush = null;
            InvalidateVisual();
        }

        // ==================================================================
        // 渲染
        // ==================================================================

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 1 || h <= 1 || ColorAt == null) return;

            // 画一层透明底，让整块区域都可点击
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            double edge = EdgeInset(h);
            double markerW = MarkerWidth(h);
            // 两端要留出手柄宽度的一半，否则手柄会跑出控件
            double trackLeft = markerW / 2.0;
            double trackRight = w - markerW / 2.0;
            double trackW = trackRight - trackLeft;
            if (trackW <= 1) return;

            // 轨道垂直居中：中线 = 控件中线 = 左右轴名/数值的中线。
            // 占比随控件高度自适应（见 TrackRatioFor）：面板缩到最小的时候
            // 一行只有 16px，固定 0.52 会让色条细得只剩 8px、三条之间全是空档。
            double trackH = Math.Max(4.0, h * TrackRatioFor(h));
            double trackTop = (h - trackH) / 2.0;
            double corner = CornerRadius(h);

            // 锁定的套红边，正在拖的套白边（同时成立时红边优先）
            Brush border = _locked ? Theme.LockBorder : (_dragging ? Theme.DragBorder : null);
            if (border != null)
            {
                Rect borderRect = new Rect(trackLeft - edge, edge * 0.5,
                                           trackW + edge * 2, h - edge);
                dc.DrawRoundedRectangle(null, new Pen(border, 2.0), borderRect,
                                        corner + edge, corner + edge);
            }

            Rect trackRect = new Rect(trackLeft, trackTop, trackW, trackH);
            dc.DrawRoundedRectangle(BuildBrush(), null, trackRect, corner, corner);

            // 手柄：轨道正下方的小三角（照 CSP 截图），尖朝上、跟着值左右走。
            // 外面描一圈深色：停在渐变亮的那一头（明度条右端是纯白）时纯浅色会看不见。
            double cx = trackLeft + trackW * _value;
            double markerTop = trackTop + trackH + h * MarkerGapRatioFor();
            double markerH = Math.Max(3.0, h * MarkerHeightRatioFor());

            StreamGeometry geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(cx, markerTop), true, true);
                ctx.LineTo(new Point(cx - markerW / 2.0, markerTop + markerH), true, false);
                ctx.LineTo(new Point(cx + markerW / 2.0, markerTop + markerH), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(Theme.MarkerFill, new Pen(Theme.MarkerEdge, 1.0), geo);
        }

        private Brush BuildBrush()
        {
            // 渐变只由另外两个轴决定，拖动本轴时不需要重建，这里直接缓存。
            if (_brush != null) return _brush;

            LinearGradientBrush brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0, 0);
            brush.EndPoint = new Point(1, 0);
            // 用相对坐标，轨道定位分开处理
            brush.MappingMode = BrushMappingMode.RelativeToBoundingBox;

            for (int i = 0; i <= GradientSteps; i++)
            {
                double t = (double)i / GradientSteps;
                Color c;
                try
                {
                    c = ColorAt(t);
                }
                catch (Exception)
                {
                    c = Colors.Gray;
                }

                bool inGamut = true;
                if (InGamutAt != null)
                {
                    try { inGamut = InGamutAt(t); }
                    catch (Exception) { inGamut = true; }
                }

                if (!inGamut)
                {
                    // 超出 sRGB 可显示范围：按设计画成灰色
                    byte g = (byte)(0x8A);
                    c = Color.FromRgb(g, g, g);
                }

                brush.GradientStops.Add(new GradientStop(c, t));
            }

            _brush = brush;
            return brush;
        }

        // ==================================================================
        // 鼠标
        // ==================================================================

        protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            // 双击 = 切换锁定，此时不要顺手改值
            if (e.ClickCount == 2)
            {
                Locked = !Locked;
                RaiseLockToggled();
                e.Handled = true;
                return;
            }

            _dragging = true;
            InvalidateVisual();   // 拖动时的白边
            CaptureMouse();
            UpdateFromMouse(e);
            ArmMagnifier();
            e.Handled = true;
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging && IsMouseCaptured)
            {
                UpdateFromMouse(e);
                ArmMagnifier();      // 已经显示着就只跟手更新
                e.Handled = true;
            }
        }

        protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_dragging)
            {
                _dragging = false;
                InvalidateVisual();
                if (IsMouseCaptured) ReleaseMouseCapture();
                if (MagnifierRef != null) MagnifierRef.Disarm();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 拖动时喂给放大镜当前的位置。null 表示不接放大镜。
        /// </summary>
        public Magnifier MagnifierRef;

        private void ArmMagnifier()
        {
            if (MagnifierRef == null) return;

            double w = ActualWidth;
            double h = ActualHeight;
            double markerW = MarkerWidth(h);
            double trackLeft = markerW / 2.0;
            double trackW = w - markerW;

            double offsetX = 0;
            double rowTopScreenY = 0;
            double rowH = h;
            double hostScreenX = 0;

            try
            {
                Window host = Window.GetWindow(this);
                if (host != null)
                {
                    // 滑条左上角 -> 宿主窗口里的坐标（横向用）
                    Point p = TranslatePoint(new Point(0, 0), host);
                    offsetX = p.X;

                    // 滑条左上角 -> 屏幕坐标（纵向用：放大镜要贴在这条滑条上方）
                    Point screen = PointToScreen(new Point(0, 0));
                    rowTopScreenY = screen.Y;
                    rowH = h;

                    Point hostOrigin = host.PointToScreen(new Point(0, 0));
                    hostScreenX = hostOrigin.X;
                }
            }
            catch (Exception)
            {
            }

            MagnifierRef.SetAnchor(trackLeft + offsetX, trackW,
                                   rowTopScreenY, rowH, hostScreenX);
            MagnifierRef.Arm(ColorAt, InGamutAt, _value);
        }

        private void UpdateFromMouse(System.Windows.Input.MouseEventArgs e)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            double markerW = MarkerWidth(h);
            double trackLeft = markerW / 2.0;
            double trackW = w - markerW;
            if (trackW <= 1) return;

            Point p = e.GetPosition(this);
            double t = (p.X - trackLeft) / trackW;

            // 锁定其它轴时，把请求值夹回色域内
            if (ClampValue != null)
            {
                try { t = ClampValue(t); }
                catch (Exception) { }
            }

            Value = t;
        }
    }
}