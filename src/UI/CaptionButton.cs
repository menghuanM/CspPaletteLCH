using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CspPalette.UI
{
    /// <summary>
    /// 标题栏右上角那两个小按钮（— 折叠 / ✕ 收起），照 CSP 的"颜色滑块"面板：
    /// 平时只有一个浅灰字形，鼠标移上去整块变亮。
    /// 不用 WPF 的 Button，省掉控件模板那一套，样式也好跟着面板一起缩放。
    /// </summary>
    public class CaptionButton : Border
    {
        private readonly TextBlock _glyph;
        private bool _hover;
        private bool _pressed;

        public event EventHandler Click;

        public CaptionButton(string glyph)
        {
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

            MouseEnter += delegate { _hover = true; UpdateVisual(); };
            MouseLeave += delegate { _hover = false; UpdateVisual(); };
            MouseLeftButtonDown += OnMouseDown;
            MouseLeftButtonUp += OnMouseUp;
        }

        /// <summary>缩放时定尺寸：只给高度，宽度和字形按它等比算。</summary>
        public void SetSize(double height)
        {
            double w = Math.Round(height * 1.30);
            if (Math.Abs(Width - w) < 0.5 && Math.Abs(Height - height) < 0.5) return;
            Width = w;
            Height = height;
            _glyph.FontSize = Math.Round(height * 0.60);
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
            Background = lit ? Theme.CaptionButtonHover : Brushes.Transparent;
            _glyph.Foreground = lit ? Brushes.White : Theme.CaptionGlyph;
        }
    }
}