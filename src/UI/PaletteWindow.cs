using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using CspPalette.ColorLib;
using CspPalette.Csp;
using CspPalette.Screen;

namespace CspPalette.UI
{
    /// <summary>
    /// 主面板：照 CSP 自己的样式做的三条 OKLCH 滑条——左边轴名、中间渐变条、
    /// 右边数值 + 上下箭头，条下面那个跟着值走的小三角就是手柄。
    /// 双击某条 = 锁定它（套一圈红边），之后再拖别的两条就不许把颜色拖出 sRGB。
    /// 窗口置顶且带 WS_EX_NOACTIVATE，点它不会把焦点从 CSP 抢走。
    /// </summary>
    public class PaletteWindow : Window
    {
        // ---- OKLCH 三条轴：L 0..100%、C 0..0.4、H 0..360° ----
        private const double OklchMaxC = 0.40;
        /// <summary>彩度那条轴的下标（拖它时不自动收彩度，否则手感就成"停在色域边界"）。</summary>
        private const int ChromaAxis = 1;

        /// <summary>轴名 / 显示量程（滑条位置 t 在这段上线性插值）/ 真实值×Scale = 显示值 / 数值框格式 / 单位 / 上下箭头一档。</summary>
        private static readonly string[] AxisLabels = new string[] { "L", "C", "H" };
        private static readonly double[] AxisMin = new double[] { 0, 0, 0 };
        private static readonly double[] AxisMax = new double[] { 100, OklchMaxC, 360 };
        private static readonly double[] AxisScale = new double[] { 100, 1, 1 };
        private static readonly string[] AxisFormat = new string[] { "0", "0.000", "0" };
        private static readonly string[] AxisSuffix = new string[] { "%", "", "\u00B0" };
        private static readonly double[] AxisStep = new double[] { 1, 0.001, 1 };

        // 界面基准尺寸：面板 460x148 时的观感，缩放时按这个比例放大缩小。
        // 高度不是独立参数，而是由 ContentHeight 按同一个比例算出来的（见"缩放"一节）。
        private const double DesignWidth = 460.0;
        /// <summary>
        /// 最小宽度：照 CSP 的"颜色滑块"面板——它最小能缩到 142x82（4 条滑条）。
        /// 我们只有 3 条，同一个宽度下高度正好也是 82（见 ContentHeight）。
        /// </summary>
        private const double MinPanelWidth = 142.0;
        private const double MaxPanelWidth = 1600.0;

        /// <summary>应用名：窗口标题和托盘提示都用它开头。</summary>
        private const string AppShortName = "CSP 取色面板 OKLCH";

        /// <summary>
        /// 标题栏高度，**固定 18 不随面板缩放**（用户："标题栏高度……只需要18像素"）。
        /// 上面已经没有标题文字了，只剩右边两个小按钮，所以不需要跟着放大。
        /// </summary>
        private const double HeaderBarHeight = 18.0;

        private readonly CspClient _client;
        private readonly PairingScanner _scanner;

        private Grid _grid;
        private Border _root;
        private Grid _header;
        private TextBlock _status;
        private CaptionButton _collapseButton;
        private CaptionButton _hideButton;
        /// <summary>三条滑条各自那一行，折叠时整行收起来。</summary>
        private readonly Grid[] _sliderRows = new Grid[3];
        /// <summary>折叠状态：只剩标题栏（点标题栏上的 — 或双击标题栏切换）。</summary>
        private bool _collapsed;
        /// <summary>
        /// 刚切换过折叠。双击标题栏时，第二下按下就切了折叠，紧接着的 MouseUp
        /// 会跑 SyncRectFromActual —— 那会儿窗口还没按新高度重排完，读到的还是旧高度，
        /// 会把刚设好的高度覆盖回去（表现成"双击了但没折叠，要再点一次"）。
        /// 所以这一次的 MouseUp 跳过高度同步。
        /// </summary>
        private bool _skipHeightSync;

        // ---- 右下角通知区域的托盘图标（退出、还原都在这儿）----
        private System.Windows.Forms.NotifyIcon _tray;
        /// <summary>编进 exe 的程序图标资源名（见 build.ps1 的 /resource）。</summary>
        private const string IconResourceName = "CspPaletteLCH.App.ico";

        private readonly GradientSlider[] _sliders = new GradientSlider[3];
        private readonly TextBlock[] _labels = new TextBlock[3];
        private readonly ValueBox[] _values = new ValueBox[3];
        /// <summary>三条滑条的「轴名」和「数值」两列，缩放时要一起改宽度。</summary>
        private readonly ColumnDefinition[] _labelCols = new ColumnDefinition[3];
        private readonly ColumnDefinition[] _valueCols = new ColumnDefinition[3];

        /// <summary>当前颜色的 OKLCH 三个值（L 0..1、C、H 0..360）。</summary>
        private readonly double[] _axes = new double[3];

        // 还没连上 CSP 时先给一个中间调的橙色当占位，界面一上来就有正常观感
        private byte _rgbR = 230, _rgbG = 170, _rgbB = 110;
        // 两个颜色槽各自的颜色（CSP 那边报回来的），面板改的是 SessionStore 里记着的那个槽
        private byte _mainR = 230, _mainG = 170, _mainB = 110;
        private byte _subR = 230, _subG = 170, _subB = 110;
        private int _colorIndex;

        private bool _suppress;

        // 自己刚写出去的颜色。CSP 常把刚写进去的值原样报回来，照单全收的话
        // 松手一秒后滑块会自己弹回色域内的实际颜色，锁定就白做了。
        private bool _hasLastWrite;
        private int _lastWriteIndex;
        private byte _lastWriteR, _lastWriteG, _lastWriteB;

        // 双击滑块锁定：被锁的那条不动，拖其它两条时不许越出 sRGB
        private readonly bool[] _locked = new bool[3];

        // 自动扫描二维码只自动发起一次，扫满次数后不再自动重试
        private bool _autoScanStarted;

        // ---- 跟着 CSP 显示 / 最小化 ----
        /// <summary>CSP 主程序名（不带 .exe）。</summary>
        private const string CspProcessName = "CLIPStudioPaint";
        /// <summary>多久看一眼 CSP 窗口的状态。比人眨眼慢点就够，别占 CPU。</summary>
        private const double FollowIntervalMs = 600.0;
        private DispatcherTimer _followTimer;
        /// <summary>记住 CSP 主窗口，省得每轮都重新枚举（窗口没了会自动重新找）。</summary>
        private IntPtr _cspWindow = IntPtr.Zero;

        private bool _draggingWindow;
        private POINT _dragStartCursor;
        private RECT _dragStartRect;

        private bool _resizingPanel;
        private int _resizeEdge;          // 位掩码，见 EDGE_* 常量
        private POINT _resizeStartCursor;
        private RECT _resizeStartRect;

        private const int EDGE_LEFT = 1;
        private const int EDGE_RIGHT = 2;
        private const int EDGE_TOP = 4;
        private const int EDGE_BOTTOM = 8;
        /// <summary>面板边缘多宽算作调整大小的热区（DIP）。</summary>
        private const double ResizeZone = 8.0;

        public PaletteWindow(CspClient client, PairingScanner scanner)
        {
            _client = client;
            _scanner = scanner;

            _colorIndex = SessionStore.GetColorIndex();

            SetupWindow();
            BuildUi();

            _client.StateChanged += OnClientStateChanged;
            _client.ColorsRead += OnColorsRead;
            _scanner.StatusChanged += OnScannerStatus;

            Loaded += OnLoaded;
            Closing += OnClosing;
            // 数值框正在输入时，点到别处（或面板失去前台）就自动确认，不必按回车
            PreviewMouseDown += delegate(object s, MouseButtonEventArgs e)
            {
                CommitEditIfOutside(e.OriginalSource);
            };
            Deactivated += delegate { CommitEditIfOutside(null); };
            // 挂在滑块上而不是窗口上：等滑块自己排完版再缩放，位置和尺寸才是新的
            _sliders[0].SizeChanged += delegate { ApplyScale(); };

            // 跟着 CSP 显示 / 最小化
            _followTimer = new DispatcherTimer();
            _followTimer.Interval = TimeSpan.FromMilliseconds(FollowIntervalMs);
            _followTimer.Tick += OnFollowTick;
        }

        // ==================================================================
        // 跟着 CSP 显示 / 最小化
        // ==================================================================

        /// <summary>
        /// 隔一会儿看一眼 CSP 主窗口：它最小化了我们也最小化，它露出来了我们也还原
        /// （用户："CSP软件最小化我们的软件也最小化"）。认不出 CSP 窗口就什么都不做。
        /// </summary>
        private void OnFollowTick(object sender, EventArgs e)
        {
            if (_cspWindow != IntPtr.Zero && !IsWindow(_cspWindow)) _cspWindow = IntPtr.Zero;
            if (_cspWindow == IntPtr.Zero) _cspWindow = FindCspWindow();
            if (_cspWindow == IntPtr.Zero) return;

            if (IsIconic(_cspWindow))
            {
                if (WindowState != System.Windows.WindowState.Minimized)
                {
                    WindowState = System.Windows.WindowState.Minimized;
                }
                return;
            }

            if (WindowState != System.Windows.WindowState.Minimized) return;

            WindowState = System.Windows.WindowState.Normal;
            // 还原有可能顺手把前台抢过来，抢到了就还给 CSP
            IntPtr mine = new WindowInteropHelper(this).Handle;
            if (GetForegroundWindow() == mine) SetForegroundWindow(_cspWindow);
        }

        /// <summary>
        /// CSP 的主窗口：它进程里最大、无主、可见的那个顶层窗口。
        /// 最小化的窗口照样报着正常的还原尺寸，所以按面积挑是对的。
        /// </summary>
        private static IntPtr FindCspWindow()
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(CspProcessName); }
            catch (Exception) { return IntPtr.Zero; }
            if (procs == null || procs.Length == 0) return IntPtr.Zero;

            HashSet<int> pids = new HashSet<int>();
            for (int i = 0; i < procs.Length; i++) pids.Add(procs[i].Id);

            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;   // 附属窗口不算

                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                int area = (r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // ==================================================================
        // 托盘图标（右下角通知区域）
        // ==================================================================

        /// <summary>托盘图标挂上去，右键菜单里是退出。</summary>
        private void CreateTrayIcon()
        {
            _tray = new System.Windows.Forms.NotifyIcon();
            _tray.Icon = LoadAppIcon(16);
            _tray.Text = AppShortName;
            _tray.ContextMenuStrip = BuildTrayMenu();
            // 双击 = 把面板叫回来：标题栏上的 ✕ 只是收起来，这是唯一的入口
            _tray.DoubleClick += delegate { ShowPanel(); };
            _tray.Visible = true;
        }

        /// <summary>右键菜单：显示面板 / 退出。</summary>
        private System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
        {
            System.Windows.Forms.ContextMenuStrip menu = new System.Windows.Forms.ContextMenuStrip();

            System.Windows.Forms.ToolStripMenuItem show =
                new System.Windows.Forms.ToolStripMenuItem("显示面板");
            show.Click += delegate { ShowPanel(); };
            menu.Items.Add(show);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            System.Windows.Forms.ToolStripMenuItem exit =
                new System.Windows.Forms.ToolStripMenuItem("退出");
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);

            return menu;
        }

        /// <summary>
        /// 程序图标：build.ps1 用 /resource 把 src\App.ico（tools\make_icon.ps1 画的）
        /// 编进 exe 了，这里按需要的尺寸取出来——托盘要的就是 16x16 那一张。
        /// </summary>
        private static System.Drawing.Icon LoadAppIcon(int size)
        {
            try
            {
                System.IO.Stream stream =
                    typeof(PaletteWindow).Assembly.GetManifestResourceStream(IconResourceName);
                if (stream == null) return System.Drawing.SystemIcons.Application;
                using (stream)
                {
                    return new System.Drawing.Icon(stream, new System.Drawing.Size(size, size));
                }
            }
            catch (Exception)
            {
                // 资源没编进去（比如手改过 build.ps1）也别让程序起不来
                return System.Drawing.SystemIcons.Application;
            }
        }

        // ==================================================================
        // 窗口设置
        // ==================================================================

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
                                                int x, int y, int cx, int cy, uint flags);

        // ---- 找 CSP 窗口 / 看它是不是最小化了 ----

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint flags);

        private const uint GW_OWNER = 4;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private void SetupWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            // 不上任务栏、也不进 Alt+Tab：退出和还原都走右下角的托盘图标
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            // 关键：显示时不激活窗口，CSP 的前台状态就不会被打断
            ShowActivated = false;
            Title = AppShortName;

            // 只有三条滑条，面板天生就是横的一条：宽度是唯一的尺寸参数，高度按它算
            Width = DesignWidth;
            Height = ContentHeight(1.0);
            MinWidth = MinPanelWidth;
            MinHeight = CollapsedHeight(MinPanelWidth / DesignWidth);

            double left, top, w, h;
            SessionStore.GetWindowRect(out left, out top, out w, out h);
            if (!double.IsNaN(left) && !double.IsNaN(top))
            {
                Left = left;
                Top = top;
            }
            // 只认宽度：高度是算出来的，存下来的高度不看了（早期版本存的是会留空带的老尺寸）
            if (w >= MinPanelWidth && w <= MaxPanelWidth)
            {
                Width = w;
                Height = ContentHeight(ScaleForWidth(w));
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            // NOACTIVATE：点面板不抢 CSP 的前台。
            // TOOLWINDOW：不上任务栏、也不进 Alt+Tab——退出改走右下角托盘图标的右键菜单
            // （用户："把任务栏改到放置到如图，右边的图标上，这样TAB里就不会显示了"）
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        // ==================================================================
        // 界面
        // ==================================================================

        private void BuildUi()
        {
            _root = new Border();
            _root.CornerRadius = new CornerRadius(10);
            _root.Background = Theme.PanelBackground;
            _root.BorderBrush = Theme.PanelBorder;
            _root.BorderThickness = new Thickness(1);
            // 上边距为 0：标题栏紧贴窗口顶边。折叠后窗口就剩"边框 + 标题栏"，
            // 这样展开和折叠两种状态下标题栏的位置、大小完全一样
            // （用户："怎么展开和折叠，标题栏大小不一致"）。
            _root.Padding = new Thickness(8, 0, 8, 8);
            Content = _root;

            Grid grid = new Grid();
            _grid = grid;
            _root.Child = grid;

            // 0 顶栏 / 1-3 三条滑条。没有多余的行：面板高度就是这些内容的高度
            for (int i = 0; i < 4; i++)
            {
                RowDefinition rd = new RowDefinition();
                rd.Height = GridLength.Auto;
                grid.RowDefinitions.Add(rd);
            }

            // ---- 标题栏：左边连接状态，右边 — 和 ✕ ----
            // 整条也是拖动把手（双击 = 展开/折叠）；退出在托盘图标的右键菜单里
            Grid header = new Grid();
            _header = header;
            header.Background = Brushes.Transparent;
            header.Height = HeaderBarHeight;
            header.ColumnDefinitions.Add(new ColumnDefinition());   // 状态（占剩下的）
            header.ColumnDefinitions.Add(new ColumnDefinition());   // —
            header.ColumnDefinitions.Add(new ColumnDefinition());   // ✕
            header.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            header.ColumnDefinitions[1].Width = GridLength.Auto;
            header.ColumnDefinitions[2].Width = GridLength.Auto;
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            _status = new TextBlock();
            _status.Foreground = Theme.StatusText;
            _status.FontFamily = Theme.UiFont;
            _status.FontSize = 11;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.Margin = new Thickness(2, 0, 6, 0);
            _status.Visibility = Visibility.Collapsed;
            Grid.SetColumn(_status, 0);
            header.Children.Add(_status);

            // — 折叠成只剩标题栏（再点一次展开，或者双击标题栏）
            _collapseButton = new CaptionButton("\u2014");
            _collapseButton.VerticalAlignment = VerticalAlignment.Center;
            _collapseButton.ToolTip = "折叠成只剩标题栏（再点一次展开，双击标题栏也行）";
            _collapseButton.Click += delegate { ToggleCollapse(); };
            Grid.SetColumn(_collapseButton, 1);
            header.Children.Add(_collapseButton);

            // ✕ 不退出程序，只是把面板收起来；从托盘图标能再打开
            _hideButton = new CaptionButton("\u2715");
            _hideButton.VerticalAlignment = VerticalAlignment.Center;
            _hideButton.ToolTip = "收起面板（点托盘图标可以再打开）";
            _hideButton.Click += delegate { HidePanel(); };
            Grid.SetColumn(_hideButton, 2);
            header.Children.Add(_hideButton);

            header.MouseLeftButtonDown += OnHeaderMouseDown;
            header.MouseMove += OnHeaderMouseMove;
            header.MouseLeftButtonUp += OnHeaderMouseUp;

            // ---- 三条滑条：轴名 + 渐变 + 数值 ----
            for (int axis = 0; axis < 3; axis++)
            {
                CreateSliderRow(axis + 1, axis);
            }

            // 面板空白处也能拖动窗口或拖边缘改大小
            _root.PreviewMouseLeftButtonDown += OnRootMouseDown;
            _root.PreviewMouseMove += OnHeaderMouseMove;
            _root.PreviewMouseLeftButtonUp += OnHeaderMouseUp;

            // 双击滑块 = 锁定，锁定后不许把颜色拖出 sRGB
            for (int axis = 0; axis < 3; axis++)
            {
                int which = axis;
                _sliders[axis].LockToggled += delegate { OnLockToggled(which); };
            }

            RebuildClampers();
            RebuildGradients();
            SyncSlidersFromAxes();
            ShowStatus("等待扫描二维码");
        }

        /// <summary>一行滑条：左边轴名、中间渐变、右边数值 + 上下箭头。</summary>
        private void CreateSliderRow(int row, int axis)
        {
            Grid line = new Grid();
            line.Margin = new Thickness(0, 1, 0, 1);
            line.ColumnDefinitions.Add(new ColumnDefinition());   // 轴名
            line.ColumnDefinitions.Add(new ColumnDefinition());   // 滑块
            line.ColumnDefinitions.Add(new ColumnDefinition());   // 数值
            line.ColumnDefinitions[0].Width = new GridLength(26);
            line.ColumnDefinitions[2].Width = new GridLength(96);
            _labelCols[axis] = line.ColumnDefinitions[0];
            _valueCols[axis] = line.ColumnDefinitions[2];
            Grid.SetRow(line, row);
            _grid.Children.Add(line);
            _sliderRows[axis] = line;

            TextBlock label = new TextBlock();
            label.Foreground = Theme.AxisLabel;
            label.FontFamily = Theme.UiFont;
            label.FontSize = 13.5;
            label.FontWeight = FontWeights.SemiBold;
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 0);
            line.Children.Add(label);
            _labels[axis] = label;

            GradientSlider slider = new GradientSlider();
            Grid.SetColumn(slider, 1);
            line.Children.Add(slider);
            _sliders[axis] = slider;

            ValueBox box = new ValueBox();
            Grid.SetColumn(box, 2);
            line.Children.Add(box);
            _values[axis] = box;

            // 数值框只管"加一档/减一档/敲了什么"，加多少、怎么解析由面板决定
            int which = axis;
            box.StepUp += delegate { StepAxis(which, 1); };
            box.StepDown += delegate { StepAxis(which, -1); };
            box.EditStarted += delegate { BeginValueEdit(box); };
            box.EditFinished += delegate { EndValueEdit(); };
            box.Entered += delegate(object s, ValueEnteredEventArgs e) { ApplyTypedValue(which, e.Text); };

            slider.ValueChanged += delegate { OnSliderChanged(which); };
        }

        // ==================================================================
        // 拖动窗口（WS_EX_NOACTIVATE 下不能用 DragMove）
        // ==================================================================

        private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (BeginPanelResize(e)) return;
            if (IsOnInteractiveControl(e.OriginalSource)) return;
            BeginWindowDrag(e);
        }

        private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 双击标题栏 = 展开 / 折叠（用户要求），别再当成拖动窗口
            if (e.ClickCount == 2 && !IsOnInteractiveControl(e.OriginalSource))
            {
                ToggleCollapse();
                e.Handled = true;
                return;
            }

            if (BeginPanelResize(e)) return;
            if (IsOnInteractiveControl(e.OriginalSource)) return;
            BeginWindowDrag(e);
        }

        // ---- 拖边缘调整面板大小 ----
        // 自己实现而不靠系统边框，因为 WS_EX_NOACTIVATE 的窗口不该被激活。

        /// <summary>鼠标落在面板哪条边缘上（位掩码），不在边缘返回 0。</summary>
        private int GetResizeEdge(Point p)
        {
            int edge = 0;
            if (p.X <= ResizeZone) edge |= EDGE_LEFT;
            else if (p.X >= ActualWidth - ResizeZone) edge |= EDGE_RIGHT;
            // 折叠后高度就剩标题栏那么高，竖着拖没有意义，不给热区
            if (!_collapsed)
            {
                if (p.Y <= ResizeZone) edge |= EDGE_TOP;
                else if (p.Y >= ActualHeight - ResizeZone) edge |= EDGE_BOTTOM;
            }
            return edge;
        }

        private bool BeginPanelResize(MouseButtonEventArgs e)
        {
            int edge = GetResizeEdge(e.GetPosition(this));
            if (edge == 0) return false;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (!GetCursorPos(out _resizeStartCursor)) return false;
            if (!GetWindowRect(hwnd, out _resizeStartRect)) return false;

            _resizeEdge = edge;
            _resizingPanel = true;
            _root.CaptureMouse();
            e.Handled = true;
            return true;
        }

        private void UpdatePanelResize()
        {
            POINT now;
            if (!GetCursorPos(out now)) return;

            int dx = now.X - _resizeStartCursor.X;
            int dy = now.Y - _resizeStartCursor.Y;

            int left = _resizeStartRect.Left;
            int top = _resizeStartRect.Top;
            int right = _resizeStartRect.Right;
            int bottom = _resizeStartRect.Bottom;

            if ((_resizeEdge & EDGE_LEFT) != 0) left += dx;
            if ((_resizeEdge & EDGE_RIGHT) != 0) right += dx;
            if ((_resizeEdge & EDGE_TOP) != 0) top += dy;
            if ((_resizeEdge & EDGE_BOTTOM) != 0) bottom += dy;

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            // 面板比例固定，所以只有"拖的那条边"决定缩放，另一边的尺寸跟着算出来：
            // 横着拖看宽度，竖着拖看高度。这样怎么拖都不会拖出一条空带。
            double s;
            if ((_resizeEdge & (EDGE_LEFT | EDGE_RIGHT)) != 0)
            {
                s = ScaleForWidth((right - left) / sx);
            }
            else
            {
                // 高度 = 34 + 3×滑条行（34 = 上下边框 2 + 下内边距 8 + 标题栏 18 + 三条缝 6），
                // 反解出来（两边都带夹取，极端比例下解出来的和 ContentHeight 会差一点，
                // 以 ContentHeight 为准）
                double hDIP = (bottom - top) / sy;
                s = ClampDouble((hDIP - 34.0) / 102.0,
                                MinPanelWidth / DesignWidth, MaxPanelWidth / DesignWidth);
            }

            int newW = (int)Math.Round(DesignWidth * s * sx);
            int newH = (int)Math.Round((_collapsed ? CollapsedHeight(s) : ContentHeight(s)) * sy);

            // 拖哪条边就让对面那条边钉住不动
            if ((_resizeEdge & EDGE_LEFT) != 0) left = right - newW; else right = left + newW;
            if ((_resizeEdge & EDGE_TOP) != 0) top = bottom - newH; else bottom = top + newH;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
                         SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void UpdateResizeCursor(Point p)
        {
            int edge = GetResizeEdge(p);
            Cursor cursor = null;
            if (edge == (EDGE_LEFT | EDGE_TOP) || edge == (EDGE_RIGHT | EDGE_BOTTOM)) cursor = Cursors.SizeNWSE;
            else if (edge == (EDGE_RIGHT | EDGE_TOP) || edge == (EDGE_LEFT | EDGE_BOTTOM)) cursor = Cursors.SizeNESW;
            else if (edge == EDGE_LEFT || edge == EDGE_RIGHT) cursor = Cursors.SizeWE;
            else if (edge == EDGE_TOP || edge == EDGE_BOTTOM) cursor = Cursors.SizeNS;
            _root.Cursor = cursor;
        }

        /// <summary>
        /// 事件源是不是落在滑块或数值框上。
        /// 这些控件要自己处理鼠标，窗口拖动必须让开，否则会把鼠标捕获抢走，
        /// 控件的 MouseUp 收不到，点击就丢了。
        /// </summary>
        private bool IsOnInteractiveControl(object source)
        {
            DependencyObject src = source as DependencyObject;
            if (src == null) return false;
            if (FindAncestor<GradientSlider>(src) != null) return true;
            Border border = FindAncestor<Border>(src);
            if (border != null && border != _root) return true;
            return false;
        }

        /// <summary>
        /// 开始拖动窗口。
        /// 全程只记 Win32 物理像素坐标，不碰 WPF 的 Left/Top：
        /// WPF 设置 Left/Top 要等下一次布局才真正生效，而鼠标事件里的窗口内坐标
        /// 是按「已经生效」的位置算出来的，两者错一拍就会来回抖。
        /// </summary>
        private void BeginWindowDrag(MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1) return;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (!GetCursorPos(out _dragStartCursor)) return;
            if (!GetWindowRect(hwnd, out _dragStartRect)) return;

            _draggingWindow = true;
            _root.CaptureMouse();
            e.Handled = true;
        }

        private void OnHeaderMouseMove(object sender, MouseEventArgs e)
        {
            if (_resizingPanel)
            {
                UpdatePanelResize();
                return;
            }

            if (_draggingWindow)
            {
                POINT now;
                if (!GetCursorPos(out now)) return;

                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                SetWindowPos(hwnd, IntPtr.Zero,
                             _dragStartRect.Left + (now.X - _dragStartCursor.X),
                             _dragStartRect.Top + (now.Y - _dragStartCursor.Y),
                             0, 0,
                             SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                return;
            }

            // 空着的时候给边缘做光标提示
            UpdateResizeCursor(e.GetPosition(this));
        }

        private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingWindow && !_resizingPanel)
            {
                _skipHeightSync = false;   // 点按钮触发的折叠，不会走到下面，这里顺手清掉
                return;
            }

            _draggingWindow = false;
            _resizingPanel = false;
            _resizeEdge = 0;
            _root.ReleaseMouseCapture();

            SyncRectFromActual(_skipHeightSync);
            _skipHeightSync = false;
            SessionStore.SetWindowRect(Left, Top, Width, Height);
        }

        /// <summary>
        /// 把 WPF 记录的 Left/Top/Width/Height 和窗口实际位置尺寸对齐。
        /// 拖动和缩放全程是用 Win32 物理像素做的，不同步的话关闭时存的还是老值。
        /// </summary>
        private void SyncRectFromActual(bool skipHeight)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return;

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0)
            {
                double newLeft = rect.Left / dpi.DpiScaleX;
                double newWidth = (rect.Right - rect.Left) / dpi.DpiScaleX;
                if (Math.Abs(newLeft - Left) > 1.0) Left = newLeft;
                if (Math.Abs(newWidth - Width) > 1.0) Width = newWidth;
            }
            if (dpi.DpiScaleY > 0)
            {
                double newTop = rect.Top / dpi.DpiScaleY;
                if (Math.Abs(newTop - Top) > 1.0) Top = newTop;
                // 高度是面板自己按宽度算出来的，只有刚切换过折叠时不能碰
                // （那会儿窗口还没重排完，读到的是旧高度）
                if (!skipHeight)
                {
                    double newHeight = (rect.Bottom - rect.Top) / dpi.DpiScaleY;
                    if (Math.Abs(newHeight - Height) > 1.0) Height = newHeight;
                }
            }
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                T hit = d as T;
                if (hit != null) return hit;
                try
                {
                    d = VisualTreeHelper.GetParent(d);
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return null;
        }

        // ==================================================================
        // 颜色逻辑
        // ==================================================================

        /// <summary>某个轴当前的值。</summary>
        private double AxisValue(int axis)
        {
            return _axes[axis];
        }

        /// <summary>拖某一条滑块：改这一条轴，其余两条保持不动。</summary>
        private void OnSliderChanged(int axis)
        {
            if (_suppress) return;

            SetAxisFromT(axis, _sliders[axis].Value);

            // 没锁定任何一条时，颜色要一直留在 sRGB 里：彩度自动收回来。
            // 有锁定的时候不这么做——那条锁住的轴必须原封不动，只能让被拖的条停在边界。
            // 但正在拖的那条如果是彩度轴，也不能去动它：把它拽回边界，
            // 手感就成了「停在色域边界」，而那是锁定之后才该有的行为。
            if (!AnyAxisLocked() && axis != ChromaAxis)
            {
                ConstrainChroma();
            }

            RecomputeRgbFromAxes();
            SaveSelectedSlotColor();
            RebuildGradients();
            RefreshSlotDisplay();
            PushColor();
        }

        // ==================================================================
        // 锁定：双击某条滑块后，拖其它两条不许把颜色拖出 sRGB
        // ==================================================================

        private bool AnyOtherAxisLocked(int axis)
        {
            for (int i = 0; i < 3; i++)
            {
                if (i != axis && _locked[i]) return true;
            }
            return false;
        }

        private void OnLockToggled(int axis)
        {
            if (axis < 0 || axis > 2) return;
            _locked[axis] = !_locked[axis];
            _sliders[axis].Locked = _locked[axis];
            RebuildClampers();
        }

        /// <summary>把三条滑块的拖动夹取回调按当前锁定状态重新装一遍。</summary>
        private void RebuildClampers()
        {
            // 只要还有锁定就装上回调，夹不夹在 ClampRequested 里按模式判断
            if (!AnyAxisLocked())
            {
                for (int i = 0; i < 3; i++) _sliders[i].ClampValue = null;
                return;
            }

            for (int i = 0; i < 3; i++)
            {
                int which = i;
                _sliders[i].ClampValue = delegate(double t) { return ClampRequested(which, t); };
            }
        }

        private bool AnyAxisLocked()
        {
            return _locked[0] || _locked[1] || _locked[2];
        }

        /// <summary>
        /// 把拖动请求的值夹到色域内：从当前值朝请求值二分，取最后一个仍在色域里的点。
        /// 这样滑块会停在色域边界上，而不是跑出去变成灰色。
        /// </summary>
        private double ClampRequested(int axis, double t)
        {
            if (!AnyOtherAxisLocked(axis)) return t;

            GradientSlider slider = _sliders[axis];
            double current = slider.Value;

            if (t == current) return t;
            if (InGamutForAxis(axis, t)) return t;

            // 当前值本身就在色域外，那就不做限制（别把用户卡死在边界外）
            if (!InGamutForAxis(axis, current)) return t;

            double lo = current;
            double hi = t;
            for (int i = 0; i < 18; i++)
            {
                double mid = (lo + hi) / 2.0;
                if (InGamutForAxis(axis, mid)) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// 没有锁定任何一条时用：如果当前颜色越界，就把彩度收到刚好在色域内的最大值。
        /// </summary>
        private void ConstrainChroma()
        {
            if (AxesInGamut()) return;

            double L = _axes[0];
            double hi = _axes[ChromaAxis];
            double lo = 0;
            for (int i = 0; i < 18; i++)
            {
                double mid = (lo + hi) / 2.0;
                if (InGamutOf(L, mid, _axes[2])) lo = mid;
                else hi = mid;
            }
            _axes[ChromaAxis] = lo;
            SetSliderValueQuietly(_sliders[ChromaAxis], AxisToT(ChromaAxis));
        }

        private void SetSliderValueQuietly(GradientSlider slider, double value)
        {
            _suppress = true;
            try { slider.Value = value; }
            finally { _suppress = false; }
        }

        /// <summary>把当前编辑的颜色存回它所属的颜色槽。</summary>
        private void SaveSelectedSlotColor()
        {
            if (_colorIndex == 0)
            {
                _mainR = _rgbR; _mainG = _rgbG; _mainB = _rgbB;
            }
            else
            {
                _subR = _rgbR; _subG = _rgbG; _subB = _rgbB;
            }
        }

        /// <summary>按当前三个轴算出颜色。</summary>
        private void RecomputeRgbFromAxes()
        {
            Color c;
            double r, g, b;
            ColorMath.OklchToRgb(_axes[0], _axes[1], _axes[2], out r, out g, out b);
            c = Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));
            _rgbR = c.R; _rgbG = c.G; _rgbB = c.B;
        }

        /// <summary>颜色来自 CSP 时，三个轴重新算一遍。</summary>
        private void ApplyRgbToAxes()
        {
            double L, C, H;
            ColorMath.RgbToOklch(_rgbR, _rgbG, _rgbB, out L, out C, out H);
            _axes[0] = L; _axes[1] = C; _axes[2] = H;
        }

        /// <summary>把三个轴刷到滑块上，顺带刷轴名和数值框。</summary>
        private void SyncSlidersFromAxes()
        {
            _suppress = true;
            try
            {
                for (int i = 0; i < 3; i++) _sliders[i].Value = AxisToT(i);
            }
            finally
            {
                _suppress = false;
            }

            UpdateAxisTexts();
        }

        /// <summary>第 axis 根滑条的滑块位置（0..1）。</summary>
        private double AxisToT(int axis)
        {
            double shown = AxisValue(axis) * AxisScale[axis];
            return Clamp01((shown - AxisMin[axis]) / (AxisMax[axis] - AxisMin[axis]));
        }

        /// <summary>滑块位置反推这一条轴的值（其余两条保持不动）。</summary>
        private void SetAxisFromT(int axis, double t)
        {
            double shown = AxisMin[axis] + Clamp01(t) * (AxisMax[axis] - AxisMin[axis]);
            _axes[axis] = shown / AxisScale[axis];
        }

        // ==================================================================
        // 数值框：上下箭头微调 + 直接敲数字
        // ==================================================================

        /// <summary>上下箭头一次走一档，档位就是轴显示值的那个刻度。</summary>
        private static double AxisStepT(int axis)
        {
            return AxisStep[axis] / (AxisMax[axis] - AxisMin[axis]);
        }

        /// <summary>点数值框右边的小箭头：上加下减一档。</summary>
        private void StepAxis(int axis, int dir)
        {
            if (axis < 0 || axis > 2) return;
            // 直接改滑块的值，后面交给 OnSliderChanged 走既有的那套（收彩度、推给 CSP）
            GradientSlider slider = _sliders[axis];
            slider.Value = Clamp01(slider.Value + dir * AxisStepT(axis));
        }

        /// <summary>
        /// 用户敲进来的数字，单位跟数值框里显示的一致，换算成滑块位置。跟 AxisToT 互逆。
        /// </summary>
        private static double AxisShownToT(int axis, double shown)
        {
            return Clamp01((shown - AxisMin[axis]) / (AxisMax[axis] - AxisMin[axis]));
        }

        /// <summary>数值框里敲完回车/失焦：能解析成数字就用，否则把原来的读数写回去。</summary>
        private void ApplyTypedValue(int axis, string text)
        {
            // 框里带着单位（L 是 "%"、H 是 "°"），敲进来的也允许带，先把尾巴上的非数字去掉
            string s = text == null ? "" : text.Trim();
            while (s.Length > 0)
            {
                char last = s[s.Length - 1];
                if (char.IsDigit(last) || last == '.') break;
                s = s.Substring(0, s.Length - 1);
            }

            double shown;
            if (axis >= 0 && axis <= 2 && s.Length > 0 &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out shown))
            {
                _sliders[axis].Value = AxisShownToT(axis, shown);
            }
            else
            {
                UpdateAxisTexts();
            }
        }

        // ---- 输入数字时临时把面板变成"可激活" ----
        // 面板平时带 WS_EX_NOACTIVATE（点它不抢 CSP 的前台），代价是 TextBox 收不到键盘。
        // 只有进输入框这一小会儿摘掉这个标记、把前台要过来，输入结束立刻还回去。

        private IntPtr _prevForeground = IntPtr.Zero;
        private bool _valueEditing;
        /// <summary>正在输入的那个数值框。</summary>
        private ValueBox _editingBox;

        private void BeginValueEdit(ValueBox box)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (!_valueEditing)
            {
                IntPtr fg = GetForegroundWindow();
                // 面板自己已经是前台时（从一个框直接点进另一个框）别把它记成"要还的窗口"，
                // 否则输完就把前台还给自己了
                if (fg != hwnd) _prevForeground = fg;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
                _valueEditing = true;
            }
            _editingBox = box;
            Activate();
            SetForegroundWindow(hwnd);
            // Activate() 不是立刻生效的，键盘焦点等窗口真的活了再给
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_valueEditing) box.FocusEditor();
            }), DispatcherPriority.Input);
        }

        private void EndValueEdit()
        {
            if (!_valueEditing) return;
            // 有可能只是"从这个框跳到那个框"，那会儿还有框在输入，就不能把激活状态收掉
            for (int i = 0; i < 3; i++)
            {
                ValueBox b = _values[i];
                if (b != null && b.IsEditing) return;
            }

            _valueEditing = false;
            _editingBox = null;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);

            // 还前台要延后一拍：点开另一个数值框时，这个框是先结束的（新框还没开始输入），
            // 这时候立刻还前台，紧接着打开的新框就又丢了键盘。等这一轮鼠标事件处理完再看。
            IntPtr back = _prevForeground;
            if (back == IntPtr.Zero) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_valueEditing) return;
                // 面板还是前台才还：用户要是已经点到别的窗口上了，别去抢
                if (GetForegroundWindow() == hwnd) SetForegroundWindow(back);
            }), DispatcherPriority.Background);
        }

        /// <summary>点／切到数值框外面就自动确认，不用非得按回车。</summary>
        private void CommitEditIfOutside(object source)
        {
            ValueBox box = _editingBox;
            if (box == null || !box.IsEditing) return;
            if (source != null)
            {
                DependencyObject d = source as DependencyObject;
                if (d != null && FindAncestor<ValueBox>(d) == box) return;
            }
            box.EndEdit(true);
        }

        /// <summary>数值框里的数字，按各轴自己的精度显示。</summary>
        private string AxisValueText(int axis)
        {
            double shown = AxisValue(axis) * AxisScale[axis];
            return shown.ToString(AxisFormat[axis], CultureInfo.InvariantCulture) + AxisSuffix[axis];
        }

        private void UpdateAxisTexts()
        {
            for (int i = 0; i < 3; i++)
            {
                if (_labels[i] != null) _labels[i].Text = AxisLabels[i];
                ValueBox v = _values[i];
                if (v != null && !v.IsEditing) v.Text = AxisValueText(i);
            }
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        private void RebuildGradients()
        {
            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                _sliders[i].ColorAt = delegate(double t) { return ColorForAxis(axis, t); };
                _sliders[i].InGamutAt = delegate(double t) { return InGamutForAxis(axis, t); };
                _sliders[i].InvalidateGradient();
            }
        }

        /// <summary>另外两个轴固定，沿某条轴采样得到的颜色。</summary>
        private Color ColorForAxis(int axis, double t)
        {
            double L = _axes[0], C = _axes[1], H = _axes[2];
            double shown = AxisMin[axis] + Clamp01(t) * (AxisMax[axis] - AxisMin[axis]);
            double v = shown / AxisScale[axis];
            if (axis == 0) L = v; else if (axis == 1) C = v; else H = v;

            double r, g, b;
            ColorMath.OklchToRgb(L, C, H, out r, out g, out b);
            return Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));
        }

        private bool InGamutForAxis(int axis, double t)
        {
            double L = _axes[0], C = _axes[1], H = _axes[2];
            double shown = AxisMin[axis] + Clamp01(t) * (AxisMax[axis] - AxisMin[axis]);
            double v = shown / AxisScale[axis];
            if (axis == 0) L = v; else if (axis == 1) C = v; else H = v;

            return InGamutOf(L, C, H);
        }

        /// <summary>这三个值是不是落在 sRGB 里。</summary>
        private static bool InGamutOf(double L, double C, double H)
        {
            double lr, lg, lb;
            ColorMath.OklchToLinearRgb(L, C, H, out lr, out lg, out lb);
            return ColorMath.InGamut(lr, lg, lb);
        }

        private bool AxesInGamut()
        {
            return InGamutOf(_axes[0], _axes[1], _axes[2]);
        }

        private static byte ToByte(double unit)
        {
            double v = Math.Round(Clamp01(unit) * 255.0, MidpointRounding.AwayFromZero);
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)v;
        }

        private void RefreshSlotDisplay()
        {
            UpdateAxisTexts();
            UpdateTitle();
        }

        /// <summary>
        /// 窗口标题 / 托盘提示：固定以应用名开头，后面跟当前槽和 L/C/H 的读数
        /// （外部工具可以直接读这个标题）。连接状态之类的长文字只留在面板里。
        /// </summary>
        private void UpdateTitle()
        {
            string text = AppShortName + " — " + FormatSlotOneLine();
            Title = text;
            // 托盘图标的悬停提示（系统限制 63 个字符，超了会抛异常）
            if (_tray != null)
            {
                _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            }
        }

        private string FormatSlotOneLine()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(_colorIndex == 0 ? "主 " : "副 ");
            sb.Append(ColorMath.ToHex(_rgbR, _rgbG, _rgbB));
            sb.Append("    ");
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) sb.Append("  ");
                sb.Append(AxisLabels[i]);
                sb.Append("=");
                sb.Append(AxisValueText(i));
            }
            return sb.ToString();
        }

        /// <summary>把当前颜色写给 CSP，并记下这次写出去的值（回读时不拿它覆盖滑块）。</summary>
        private void PushColor()
        {
            if (_client == null) return;
            _hasLastWrite = true;
            _lastWriteIndex = _colorIndex;
            _lastWriteR = _rgbR; _lastWriteG = _rgbG; _lastWriteB = _rgbB;
            _client.QueueColor(_rgbR, _rgbG, _rgbB, _colorIndex);
        }

        // ==================================================================
        // 缩放
        // ==================================================================

        /// <summary>
        /// 一条滑条行的高度（跟着面板缩放）。下限 16：配上面板的边距和 18px 标题栏，
        /// 142 宽时整个面板正好落在 142x82（见 ContentHeight）。
        /// </summary>
        private static double SliderHeight(double s)
        {
            return Math.Round(ClampDouble(34.0 * s, 16, 62));
        }

        /// <summary>
        /// 面板外框的高度 = 上下边框 2 + 标题栏下方的内边距 8 + 标题栏 + 三条滑条
        /// （各自上下留 1px 缝）。**面板高度就是这么多**，多出来的高度不会再变成
        /// 底部的一条空带（用户圈着截图说"红框的位置贴近"）。
        /// 标题栏上方没有内边距，它紧贴窗口顶边——这样折叠后剩下的那一块
        /// 和展开时的标题栏完全重合。
        /// </summary>
        private static double ContentHeight(double s)
        {
            return 10.0 + HeaderBarHeight + 3.0 * (SliderHeight(s) + 2.0);
        }

        /// <summary>
        /// 折叠后的高度：只剩标题栏（图三那个小框）。这时标题栏下方的内边距也收掉了
        /// （见 ApplyScale），所以就是"上边框 + 标题栏 + 下边框"，
        /// 标题栏本身的位置和大小跟展开时一模一样。
        /// </summary>
        private static double CollapsedHeight(double s)
        {
            return 2.0 + HeaderBarHeight;
        }

        /// <summary>该宽度对应的比例（宽度决定一切，高度是算出来的）。</summary>
        private static double ScaleForWidth(double w)
        {
            return ClampDouble(w / DesignWidth, MinPanelWidth / DesignWidth, MaxPanelWidth / DesignWidth);
        }

        private void ApplyScale()
        {
            if (_header == null || _sliders[0] == null) return;
            double w = ActualWidth > 1 ? ActualWidth : Width;
            if (w <= 1) return;

            double s = ScaleForWidth(w);

            double headerH = HeaderBarHeight;
            _grid.RowDefinitions[0].Height = new GridLength(headerH);
            _header.Height = headerH;
            // 两个小按钮比标题栏矮一点，才不会顶满整条（照 CSP 那样留一圈缝）
            double capBtnH = Math.Round(headerH * 0.86);
            _collapseButton.SetSize(capBtnH);
            _hideButton.SetSize(capBtnH);

            // 折叠时三条滑条整行收起来，标题栏**下方**的内边距也收掉。
            // 标题栏自己（位置、大小、上下边框）一个字都不动，所以展开和折叠
            // 两种状态下它看起来完全一样（用户："怎么展开和折叠，标题栏大小不一致"）。
            _root.Padding = _collapsed ? new Thickness(8, 0, 8, 0) : new Thickness(8, 0, 8, 8);

            double slideH = SliderHeight(s);
            for (int i = 0; i < 3; i++)
            {
                _grid.RowDefinitions[i + 1].Height = _collapsed ? new GridLength(0) : new GridLength(slideH);
                _sliderRows[i].Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
            }

            // 轴名（L/C/H）和数值框（数字 + 箭头方块）都跟着面板一起放大。
            // 数值那一列比早先窄：数字改成靠右贴着箭头方块了，留太宽中间就空一条
            double labelW = Math.Round(ClampDouble(26.0 * s, 15, 48));
            // 下限 50：彩度要显示 3 位小数（"0.104"），再窄就被截成"0...."了
            double valueW = Math.Round(ClampDouble(78.0 * s, 50, 140));
            double boxH = Math.Round(ClampDouble(24.0 * s, 14, 44));
            for (int i = 0; i < 3; i++)
            {
                _labelCols[i].Width = new GridLength(labelW);
                _valueCols[i].Width = new GridLength(valueW);
                _values[i].SetBox(boxH);
            }

            double font = ClampDouble(13.5 * s, 10, 26);
            if (Math.Abs(_labels[0].FontSize - font) > 0.05)
            {
                for (int i = 0; i < 3; i++)
                {
                    _labels[i].FontSize = font;
                    _values[i].TextSize = font;
                }
            }

            // 高度贴到内容上。设完会再来一轮（滑条尺寸变了），下一轮算出来一样就不再设了。
            double contentH = _collapsed ? CollapsedHeight(s) : ContentHeight(s);
            if (Math.Abs(Height - contentH) > 0.5) Height = contentH;
        }

        /// <summary>点标题栏上的 —：在"三条滑条"和"只剩标题栏"之间切换。</summary>
        private void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            // 见 _skipHeightSync 的注释：紧接着的 MouseUp 别去同步高度
            _skipHeightSync = true;
            ApplyScale();
        }

        /// <summary>
        /// 点标题栏上的 ✕：把面板收起来（**不退出程序**，用户要求"按x不真关闭，
        /// 通过托盘图标能再次打开"）。真正的退出在托盘图标的右键菜单里。
        /// </summary>
        private void HidePanel()
        {
            // 收起来前把数值框的输入结束掉，免得留着一个抢着键盘焦点的编辑框
            CommitEditIfOutside(null);

            // 提示要记在收起来**之前**：面板一 Hide，位置尺寸就不好拿了
            double hintLeft = Left;
            double hintTop = Top;
            double hintW = Width;
            double hintH = Height;
            Hide();
            ShowTrayHint(hintLeft, hintTop, hintW, hintH);
        }

        /// <summary>
        /// 面板收起来之后弹一句提示，位置就在面板原来待的地方。
        /// **不用系统的气泡通知**（NotifyIcon.ShowBalloonTip）：那玩意儿要先注册
        /// AppUserModelID、还受"通知与操作"开关和专注助手管，实测在这台机器上根本不弹
        /// （用户："收到托盘不弹通知"）。自己画一个小的浮层最稳，2 秒后淡出。
        /// </summary>
        private void ShowTrayHint(double left, double top, double panelW, double panelH)
        {
            double w = Math.Min(Math.Max(panelW, 180.0), 320.0);
            double h = 34.0;

            Window hint = new Window();
            hint.WindowStyle = WindowStyle.None;
            hint.AllowsTransparency = true;
            hint.Background = Brushes.Transparent;
            hint.Topmost = true;
            hint.ShowInTaskbar = false;
            hint.ShowActivated = false;      // 不抢 CSP 的前台
            hint.Title = AppShortName;
            hint.Width = w;
            hint.Height = h;
            hint.Left = left + (panelW - w) / 2.0;
            hint.Top = top + (panelH - h) / 2.0;

            Border box = new Border();
            box.Background = Theme.PanelBackground;
            box.BorderBrush = Theme.PanelBorder;
            box.BorderThickness = new Thickness(1);
            box.CornerRadius = new CornerRadius(8);
            box.Padding = new Thickness(12, 0, 12, 0);

            TextBlock text = new TextBlock();
            text.Text = "已收起到托盘，双击托盘图标可以再打开";
            text.Foreground = Theme.CaptionGlyph;
            text.FontFamily = Theme.UiFont;
            text.FontSize = 11.5;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.VerticalAlignment = VerticalAlignment.Center;
            box.Child = text;

            hint.Content = box;
            hint.Show();

            // 停 2 秒，再用 0.35 秒淡出，然后自己关掉
            DoubleAnimation fade = new DoubleAnimation(1.0, 0.0,
                new Duration(TimeSpan.FromMilliseconds(350)));
            fade.BeginTime = TimeSpan.FromSeconds(2.0);
            fade.Completed += delegate
            {
                try { hint.Close(); }
                catch (Exception) { }
            };
            hint.BeginAnimation(Window.OpacityProperty, fade);
        }

        /// <summary>把面板露出来（托盘图标双击 / 右键菜单）。</summary>
        private void ShowPanel()
        {
            if (!IsVisible) Show();
            if (WindowState == System.Windows.WindowState.Minimized)
            {
                WindowState = System.Windows.WindowState.Normal;
            }
        }

        private static double ClampDouble(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        // ==================================================================
        // CSP 同步
        // ==================================================================

        private void OnColorsRead(CspColors colors)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_suppress) return;
                for (int i = 0; i < 3; i++)
                {
                    if (_sliders[i].IsDragging) return;
                }

                bool selectedChanged = false;
                if (colors.HasMain)
                {
                    _mainR = colors.MainR; _mainG = colors.MainG; _mainB = colors.MainB;
                    if (_colorIndex == 0) selectedChanged = true;
                }
                if (colors.HasSub)
                {
                    _subR = colors.SubR; _subG = colors.SubG; _subB = colors.SubB;
                    if (_colorIndex == 1) selectedChanged = true;
                }

                if (!selectedChanged) return;

                // 只是自己刚写出去的值原路读回来，那就别动滑块：
                // 拖到色域外的请求值要留在原地（渐变上的灰色区段就是给它的）
                byte r = _colorIndex == 0 ? colors.MainR : colors.SubR;
                byte g = _colorIndex == 0 ? colors.MainG : colors.SubG;
                byte b = _colorIndex == 0 ? colors.MainB : colors.SubB;
                if (_hasLastWrite && _lastWriteIndex == _colorIndex &&
                    r == _lastWriteR && g == _lastWriteG && b == _lastWriteB)
                {
                    return;
                }

                // 面板在调的那个槽在 CSP 那边变了（比如用了吸管），同步到滑块上
                _rgbR = _colorIndex == 0 ? _mainR : _subR;
                _rgbG = _colorIndex == 0 ? _mainG : _subG;
                _rgbB = _colorIndex == 0 ? _mainB : _subB;
                ApplyRgbToAxes();
                SyncSlidersFromAxes();
                RebuildGradients();
                RefreshSlotDisplay();
            }));
        }

        // ==================================================================
        // 状态显示
        // ==================================================================

        private void OnClientStateChanged(CspLinkState state, string detail)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (state == CspLinkState.Connected)
                {
                    _autoScanStarted = false;
                    // 连上之后顶栏不留状态文字，整条让给拖动
                    _status.Visibility = Visibility.Collapsed;
                    RefreshSlotDisplay();
                }
                else
                {
                    ShowStatus(detail);
                }

                // CSP 每次重开「连接手机」窗口，端口和密码都会变，旧的配对就失效了。
                // 这种情况下自动重新扫二维码，但只自动发起一次、最多扫 10 次，
                // 免得屏幕上没有二维码时一直占着 CPU 扫下去。
                if ((state == CspLinkState.Disconnected || state == CspLinkState.Unpaired)
                    && _scanner != null && !_scanner.IsRunning && !_autoScanStarted)
                {
                    _autoScanStarted = true;
                    _scanner.Start(PairingScanner.AutoScanAttempts);
                }
            }));
        }

        private void OnScannerStatus(string text)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_client.State != CspLinkState.Connected) ShowStatus(text);
            }));
        }

        /// <summary>
        /// 按客户端当前状态补一次顶栏文字。重连是毫秒级的，状态变化有可能发生在
        /// 窗口订阅事件之前，没有这一步顶栏会一直挂着启动时那句「等待扫描二维码」。
        /// </summary>
        private void SyncStatusFromClient()
        {
            if (_client == null) return;
            if (_client.State == CspLinkState.Connected)
            {
                _status.Visibility = Visibility.Collapsed;
                return;
            }
            string detail = _client.StateDetail;
            if (!string.IsNullOrEmpty(detail)) ShowStatus(detail);
        }

        private void ShowStatus(string text)
        {
            _status.Text = text;
            _status.Visibility = Visibility.Visible;
            UpdateTitle();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ClampToScreen();
            SyncStatusFromClient();
            OnFollowTick(null, EventArgs.Empty);   // 一上来就跟 CSP 对齐一次，别等 600ms
            _followTimer.Start();
            CreateTrayIcon();
            _mainR = _rgbR; _mainG = _rgbG; _mainB = _rgbB;
            _subR = _rgbR; _subG = _rgbG; _subB = _rgbB;
            ApplyRgbToAxes();
            SyncSlidersFromAxes();
            RebuildGradients();
            ApplyScale();
            RefreshSlotDisplay();
        }

        private void ClampToScreen()
        {
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vW = SystemParameters.VirtualScreenWidth;
            double vH = SystemParameters.VirtualScreenHeight;

            if (Left < vLeft) Left = vLeft + 20;
            if (Top < vTop) Top = vTop + 20;
            if (Left > vLeft + vW - 120) Left = vLeft + vW - Width - 20;
            if (Top > vTop + vH - 60) Top = vTop + vH - Height - 20;
            if (Left < vLeft) Left = vLeft;
            if (Top < vTop) Top = vTop;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _followTimer.Stop();
            if (_tray != null)
            {
                // 先把图标摘掉再退出，不然托盘里会留一个点不动的死图标
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            SessionStore.SetWindowRect(Left, Top, Width, Height);
            Application.Current.Shutdown();
        }
    }
}