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

        /// <summary>手柄（小三角）的宽度。</summary>
        private static double MarkerWidth(double controlHeight)
        {
            return Math.Max(7.0, controlHeight * MarkerWidthRatio);
        }

        /// <summary>轨道两端的圆角。</summary>
        private static double CornerRadius(double controlHeight)
        {
            return Math.Min(Math.Max(2.0, controlHeight * TrackRatio * 0.22), 6.0);
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

            // 轨道垂直居中：中线 = 控件中线 = 左右轴名/数值的中线
            double trackH = Math.Max(4.0, h * TrackRatio);
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
            double markerTop = trackTop + trackH + h * MarkerGapRatio;
            double markerH = Math.Max(3.0, h * MarkerHeightRatio);

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
            e.Handled = true;
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging && IsMouseCaptured)
            {
                UpdateFromMouse(e);
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
                e.Handled = true;
            }
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