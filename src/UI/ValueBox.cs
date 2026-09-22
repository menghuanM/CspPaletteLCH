using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CspPalette.UI
{
    /// <summary>
    /// 数值框，照 CSP 面板的样式：左边一个数字，右边一个浅色小方块，
    /// 方块里上下两个箭头（点上面加一档、点下面减一档）。
    /// 点数字本身则原地变成可输入，回车或失焦提交、Esc 取消。
    /// 控件本身不认识"值"是什么意思，加一档到底加多少、敲进去的字符串怎么解析，
    /// 都由外面（PaletteWindow）决定。
    /// </summary>
    public class ValueBox : Border
    {
        private readonly TextBlock _text;
        private readonly TextBox _edit;
        private readonly Grid _spin;
        private readonly Border _spinBox;
        private readonly ColumnDefinition _spinCol;
        private readonly Path _upPath;
        private readonly Path _downPath;
        private readonly Border _upHit;
        private readonly Border _downHit;

        private bool _editing;
        private bool _overUp;
        private bool _overDown;

        /// <summary>点上面那个箭头。</summary>
        public event EventHandler StepUp;
        /// <summary>点下面那个箭头。</summary>
        public event EventHandler StepDown;
        /// <summary>用户敲完回车/失焦，e.Text 是原始输入（还没解析）。</summary>
        public event EventHandler<ValueEnteredEventArgs> Entered;
        /// <summary>
        /// 刚要进入输入状态。外层要在这里把窗口切成"可激活"并抢焦点——
        /// 面板平时是 WS_EX_NOACTIVATE（点它不抢 CSP 的前台），
        /// 不这么做的话 TextBox 根本收不到键盘。
        /// </summary>
        public event EventHandler EditStarted;
        /// <summary>输入结束（提交或取消都算），外层在这里把前台还给 CSP。</summary>
        public event EventHandler EditFinished;

        public ValueBox()
        {
            // 数字直接写在面板底色上（照 CSP：数值那一块没有自己的底），
            // 只有箭头方块是块浅色
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;
            VerticalAlignment = VerticalAlignment.Center;

            Grid grid = new Grid();
            ColumnDefinition c0 = new ColumnDefinition();
            _spinCol = new ColumnDefinition();
            _spinCol.Width = new GridLength(24);
            grid.ColumnDefinitions.Add(c0);
            grid.ColumnDefinitions.Add(_spinCol);
            Child = grid;

            _text = new TextBlock();
            _text.Foreground = Theme.FieldText;
            _text.FontFamily = Theme.UiFont;
            _text.FontSize = 10.5;
            _text.VerticalAlignment = VerticalAlignment.Center;
            // 数字靠右贴着手柄方块（用户："上下按钮还是太远了"），
            // 三条数字右边缘对齐，箭头方块也就自然排成一列
            _text.TextAlignment = TextAlignment.Right;
            _text.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_text, 0);
            grid.Children.Add(_text);

            _edit = new TextBox();
            _edit.Foreground = Theme.FieldText;
            _edit.Background = Brushes.Transparent;
            _edit.BorderThickness = new Thickness(0);
            _edit.Padding = new Thickness(0);
            _edit.FontFamily = Theme.UiFont;
            _edit.FontSize = 10.5;
            _edit.VerticalContentAlignment = VerticalAlignment.Center;
            // 输入时只占数字那一格、同样靠右：进入输入态时文字不会跳位置
            _edit.TextAlignment = TextAlignment.Right;
            _edit.Visibility = Visibility.Collapsed;
            _edit.KeyDown += OnEditKeyDown;
            _edit.LostKeyboardFocus += delegate { EndEdit(true); };
            Grid.SetColumn(_edit, 0);
            grid.Children.Add(_edit);

            // 上下箭头：一直显着（照 CSP，不是鼠标移上去才浮出来），
            // 每个装在一个透明 Border 里，整半格都能点
            _spin = new Grid();
            _spin.ColumnDefinitions.Add(new ColumnDefinition());
            _spin.RowDefinitions.Add(new RowDefinition());
            _spin.RowDefinitions.Add(new RowDefinition());
            _spinBox = new Border();
            _spinBox.Background = Theme.SpinBackground;
            _spinBox.CornerRadius = new CornerRadius(3);
            _spinBox.Child = _spin;
            Grid.SetColumn(_spinBox, 1);
            grid.Children.Add(_spinBox);

            _upPath = MakeChevron(true);
            _upHit = MakeHit(_upPath, 0);
            _spin.Children.Add(_upHit);
            _downPath = MakeChevron(false);
            _downHit = MakeHit(_downPath, 1);
            _spin.Children.Add(_downHit);

            MouseLeftButtonDown += OnBoxMouseDown;
        }

        /// <summary>框里显示的文字。</summary>
        public string Text
        {
            get { return _text.Text; }
            set
            {
                _text.Text = value;
                if (!_editing) _edit.Text = value;
            }
        }

        public double TextSize
        {
            get { return _text.FontSize; }
            set
            {
                _text.FontSize = value;
                _edit.FontSize = value;
            }
        }

        public bool IsEditing { get { return _editing; } }

        /// <summary>缩放时定尺寸：高。数字到箭头方块的空、方块本身都按高度等比算。</summary>
        public void SetBox(double height)
        {
            Height = height;
            // 数字靠右，所以这个"空"是它到方块的距离（用户嫌远，压到很小）
            double gap = Math.Max(3.0, Math.Round(height * 0.18));
            _text.Margin = new Thickness(2, 0, gap, 0);
            _edit.Margin = new Thickness(2, 0, gap, 0);
            double spinW = Math.Round(height * 1.0);
            _spinCol.Width = new GridLength(spinW + Math.Max(3.0, Math.Round(height * 0.14)));
            _spinBox.Width = spinW;
            _spinBox.Height = Math.Round(height * 0.92);
            _spinBox.CornerRadius = new CornerRadius(Math.Max(2.0, Math.Round(height * 0.14)));

            double cw = Math.Max(6.0, Math.Round(spinW * 0.42));
            double ch = Math.Max(3.0, Math.Round(height * 0.20));
            double pen = Math.Max(1.2, Math.Round(height * 0.10));
            SetChevron(_upPath, cw, ch, pen);
            SetChevron(_downPath, cw, ch, pen);
        }

        private Border MakeHit(Path chevron, int row)
        {
            Border hit = new Border();
            hit.Background = Brushes.Transparent;
            hit.Child = chevron;
            Grid.SetRow(hit, row);
            hit.MouseEnter += delegate
            {
                if (row == 0) _overUp = true; else _overDown = true;
                UpdateArrows();
            };
            hit.MouseLeave += delegate
            {
                if (row == 0) _overUp = false; else _overDown = false;
                UpdateArrows();
            };
            hit.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (row == 0) Raise(StepUp);
                else Raise(StepDown);
                e.Handled = true;
            };
            return hit;
        }

        private static Path MakeChevron(bool up)
        {
            Path p = new Path();
            p.Stroke = Theme.FieldText;
            p.Fill = null;
            p.StrokeThickness = 1.5;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.StrokeLineJoin = PenLineJoin.Round;
            p.Stretch = Stretch.None;
            p.HorizontalAlignment = HorizontalAlignment.Center;
            p.VerticalAlignment = VerticalAlignment.Center;
            p.IsHitTestVisible = false;
            p.Tag = up;
            p.Data = ChevronGeometry(up, 8, 5);
            return p;
        }

        private static void SetChevron(Path p, double w, double h, double pen)
        {
            p.Data = ChevronGeometry((bool)p.Tag, w, h);
            p.StrokeThickness = pen;
        }

        /// <summary>一个 "∧" 或 "∨" 的折线（只描边、不填充）。</summary>
        private static Geometry ChevronGeometry(bool up, double w, double h)
        {
            PathFigure fig = new PathFigure();
            if (up)
            {
                fig.StartPoint = new Point(0, h);
                fig.Segments.Add(new LineSegment(new Point(w / 2.0, 0), true));
                fig.Segments.Add(new LineSegment(new Point(w, h), true));
            }
            else
            {
                fig.StartPoint = new Point(0, 0);
                fig.Segments.Add(new LineSegment(new Point(w / 2.0, h), true));
                fig.Segments.Add(new LineSegment(new Point(w, 0), true));
            }
            fig.IsClosed = false;
            fig.IsFilled = false;
            PathGeometry geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            return geo;
        }

        private void UpdateArrows()
        {
            if (_editing) return;
            _upPath.Stroke = _overUp ? Brushes.White : Theme.FieldText;
            _downPath.Stroke = _overDown ? Brushes.White : Theme.FieldText;
        }

        private void OnBoxMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_editing) return;
            BeginEdit();
            e.Handled = true;
        }

        private void BeginEdit()
        {
            _editing = true;
            _edit.Text = _text.Text;
            _text.Visibility = Visibility.Collapsed;
            _spinBox.Visibility = Visibility.Collapsed;
            _edit.Visibility = Visibility.Visible;

            // 先让外层把窗口激活（否则 Focus 拿不到键盘），再聚焦
            Raise(EditStarted);
            FocusEditor();
        }

        /// <summary>把键盘焦点交给输入框。窗口还没真正激活时外层会稍后再调一次。</summary>
        public void FocusEditor()
        {
            if (!_editing) return;
            _edit.Focus();
            Keyboard.Focus(_edit);
            // 不默认全选：光标落在末尾，直接接着敲就是往后面加
            _edit.CaretIndex = _edit.Text.Length;
            _edit.SelectionLength = 0;
        }

        private void OnEditKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                EndEdit(true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndEdit(false);
                e.Handled = true;
            }
        }

        /// <summary>结束输入。commit = true 时把内容抛给外层解析。</summary>
        public void EndEdit(bool commit)
        {
            if (!_editing) return;
            _editing = false;

            string typed = _edit.Text;
            _edit.Visibility = Visibility.Collapsed;
            _text.Visibility = Visibility.Visible;
            _spinBox.Visibility = Visibility.Visible;
            _overUp = false;
            _overDown = false;
            UpdateArrows();

            Raise(EditFinished);

            if (!commit) return;
            if (typed == null) typed = "";
            typed = typed.Trim();
            if (typed.Length == 0) return;

            ValueEnteredEventArgs args = new ValueEnteredEventArgs(typed);
            EventHandler<ValueEnteredEventArgs> h = Entered;
            if (h != null) h(this, args);
        }

        private void Raise(EventHandler h)
        {
            if (h != null) h(this, EventArgs.Empty);
        }
    }

    /// <summary>用户在数值框里敲的那串原始文字。</summary>
    public class ValueEnteredEventArgs : EventArgs
    {
        public readonly string Text;

        public ValueEnteredEventArgs(string text)
        {
            Text = text;
        }
    }
}