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
using CspPalette.Input;
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

        /// <summary>
        /// 临时弹出面板的标题栏矮一些（14px）。它上面只有一行连接状态文字，
        /// 没有按钮要放，用 18px 会在最上面空出一条带，把三条滑条挤下去
        /// （用户看截图："上下距离太大了"）。
        /// </summary>
        private const double PopupHeaderHeight = 14.0;

        /// <summary>这个窗口实际用的标题栏高度。</summary>
        private double HeaderHeight
        {
            get { return _isPopup ? PopupHeaderHeight : HeaderBarHeight; }
        }

        // ==================================================================
        // 主面板的样式（用户在 build.bat style 调参台里定的）
        //
        // 这些值是在**基准宽度 340** 下定的；实际宽度不同时按 宽/340 等比缩放，
        // 所以任何宽度下看起来都是同一张脸，只是整体放大/缩小。
        // 标题栏是例外：它固定 18px 不缩（用户明确要求"只需要18像素"）。
        // ==================================================================

        /// <summary>主面板样式参数的标定宽度。</summary>
        private const double MainReferenceWidth = 340.0;

        private const double MainRowHeight = 34.0;
        private const double MainRowGap = 1.0;
        private const double MainPadSide = 6.0;
        private const double MainPadTop = 0.0;
        private const double MainPadBottom = 5.0;
        private const double MainLabelWidth = 26.0;
        private const double MainValueWidth = 100.0;
        private const double MainBoxHeight = 24.0;
        private const double MainFontSize = 24.0;
        private const double MainCornerRadius = 10.0;
        private const double MainBorderWidth = 0.0;
        private const double MainTrackRatio = 0.77;
        private const double MainMarkerRatio = 0.17;
        private const double MainMarkerGap = 0.05;

        /// <summary>主面板尺寸的缩放倍率。1 = 正好是标定宽度 340。</summary>
        private static double MainScaleFor(double s)
        {
            double w = s * DesignWidth;
            if (w <= 1) return 1.0;
            return w / MainReferenceWidth;
        }

        // ==================================================================
        // 临时弹出面板的样式（同样来自调参台，标定宽度 248）
        //
        // 弹窗和主面板是两套独立的观感：弹窗是"看一眼颜色"的小卡片，
        // 信息密度可以更高；主面板是常用的，字要看得清。
        // ==================================================================

        /// <summary>每行滑条的高度（调参台 rowHeight）。</summary>
        private const double PopupRowHeight = 19.0;
        /// <summary>行与行之间的缝，上下各留这么多（调参台 rowGap）。</summary>
        private const double PopupRowGap = 1.0;
        /// <summary>左右内边距（调参台 padLeft / padRight）。</summary>
        private const double PopupPadSide = 5.0;
        /// <summary>上内边距（调参台 padTop）。</summary>
        private const double PopupPadTop = 6.0;
        /// <summary>下内边距（调参台 padBottom）。</summary>
        private const double PopupPadBottom = 0.0;
        /// <summary>轴名那一列的宽度（调参台 labelWidth）。</summary>
        private const double PopupLabelWidth = 15.0;
        /// <summary>数值那一列的宽度（调参台 valueWidth）。</summary>
        private const double PopupValueWidth = 61.0;
        /// <summary>数值框高度（调参台 boxHeight）。</summary>
        private const double PopupBoxHeight = 14.0;
        /// <summary>轴名和数值的字号（调参台 fontSize）。</summary>
        private const double PopupFontSize = 14.6;
        /// <summary>面板圆角（调参台 cornerRadius）。</summary>
        private const double PopupCornerRadius = 10.0;
        /// <summary>边框粗细（调参台 borderWidth）。0 = 不要边框。</summary>
        private const double PopupBorderWidth = 0.0;
        /// <summary>三条色条占行高的比例（调参台 trackRatio）。</summary>
        private const double PopupTrackRatio = 0.95;
        /// <summary>手柄小三角的高度占比（调参台 markerRatio）。</summary>
        private const double PopupMarkerRatio = 0.17;
        /// <summary>轨道与手柄之间的缝（调参台 markerGap）。</summary>
        private const double PopupMarkerGap = 0.05;

        /// <summary>
        /// 上面那组弹窗参数的"标定宽度"：它们是在这个宽度下调出来的。
        /// 实际宽度不同时按 w / 这个值 等比缩放，所以放大缩小和 248 宽时长得一样。
        /// </summary>
        private const double PopupReferenceWidth = 248.0;

        /// <summary>
        /// 弹窗尺寸的缩放倍率。1 = 正好是调参台里的标定宽度（248）。
        /// </summary>
        private static double PopupScaleFor(double s)
        {
            double w = s * DesignWidth;
            if (w <= 1) return 1.0;
            return w / PopupReferenceWidth;
        }

        private readonly CspClient _client;
        private readonly PairingScanner _scanner;

        private Grid _grid;
        private Border _root;
        private Grid _header;
        private TextBlock _status;
        private CaptionButton _collapseButton;
        private CaptionButton _hideButton;
        /// <summary>📌 图钉：锁定面板尺寸（拖边缘不再改变大小）。</summary>
        private CaptionButton _pinButton;
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
        /// <summary>
        /// 托盘图标**真的**注册成功了吗（见 TraySupport）。
        /// 这个窗口带 WS_EX_TOOLWINDOW（不上任务栏、不进 Alt+Tab），所以托盘一旦不可用，
        /// 按 ✕ 收起就等于把面板弄丢了——为 false 时 ✕ 改成退回任务栏最小化。
        /// </summary>
        private bool _trayAvailable;
        /// <summary>编进 exe 的程序图标资源名（见 build.ps1 的 /resource）。</summary>
        private const string IconResourceName = "CspPaletteLCH.App.ico";

        private readonly GradientSlider[] _sliders = new GradientSlider[3];

        /// <summary>
        /// 拖滑条时向上弹出的放大镜。只有主面板有（弹窗太小，会被自己盖住）。
        /// 它是叠加层，不占布局高度，见 BuildUi 里的说明。
        /// </summary>
        private Magnifier _magnifier;
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
        /// <summary>多久看一眼 CSP 窗口的状态。比人眨眼慢点就够，别占 CPU。</summary>
        private const double FollowIntervalMs = 600.0;
        private DispatcherTimer _followTimer;
        /// <summary>记住 CSP 主窗口，省得每轮都重新枚举（窗口没了会自动重新找）。</summary>
        private IntPtr _cspWindow = IntPtr.Zero;
        /// <summary>
        /// 下一次允许去找 CSP 窗口的时间。CSP 没开着的时候，600ms 一轮地枚举进程
        /// （每条轮询都要开一堆进程句柄）纯属白费，隔两秒看一次就够。
        /// </summary>
        private DateTime _nextCspLookup = DateTime.MinValue;

        /// <summary>
        /// 前台窗口变化的钩子。Alt+Tab 不会最小化 CSP（窗口还在，只是失去前台），
        /// 光靠 600ms 轮询看最小化状态是发现不了的（用户："通过 alt+tab 切换到其它应用后
        /// CspPaletteLCH 不会隐藏"），所以前台变化单独用事件盯着。
        /// </summary>
        private IntPtr _foregroundHook = IntPtr.Zero;
        /// <summary>回调得留个引用，不然会被 GC 回收掉。</summary>
        private WinEventProc _foregroundProc;
        /// <summary>
        /// 面板是**自动**藏起来的（用户切到别的程序去了），不是用户按 ✕ 收起的。
        /// 只有这一种藏法会在切回 CSP 时自动露回来——用户自己按 ✕ 收起的
        /// 必须老老实实待着，等他点托盘图标。
        /// </summary>
        private bool _autoHidden;

        /// <summary>
        /// 面板是因为"用户开始在画布上画"而收起的（右键吸色召唤出来的那种面板）。
        ///
        /// 和 <see cref="_autoHidden"/> 的区别很重要，踩过两次坑：
        /// 这个标记**不代表**"现在就该露回来"。用户左手画着、右手随时右键吸色，
        /// 前台一直就是 CSP，如果跟随定时器一看到它就 Show，面板根本收不住。
        ///
        /// 它只在**用户真的离开过 CSP 又回来**之后才让面板露出来
        /// （判据是 <see cref="_leftCspWhileHidden"/>：藏起来之后见过前台不是 CSP）。
        /// </summary>
        private bool _dismissedByDrawing;

        /// <summary>
        /// 面板藏起来之后，有没有见过"前台不是 CSP"。
        /// 只有它为真时，"切回 CSP"才该把面板露回来——
        /// 否则就是用户一直在 CSP 里画画，这时候把面板弹出来纯属捣乱。
        /// </summary>
        private bool _leftCspWhileHidden;

        /// <summary>
        /// 面板是因为用户按 ✕、而托盘又不可用，才"退回任务栏最小化"的（见 HidePanel）。
        ///
        /// 必须单独记一笔：跟随定时器看到"面板最小化着"会以为"CSP 最小化了要跟着"，
        /// 下一轮就把它还原出来——用户按的 ✕ 等于白按。实测踩到过。
        /// </summary>
        private bool _minimizedByClose;

        // ---- 右键吸色：在光标旁弹一块临时面板（见 OnRightClickForColor）----

        /// <summary>全局鼠标观察器（只听不拦，CSP 的右键吸色照常）。</summary>
        private PointerHook _pointerHook;

        /// <summary>
        /// 右键吸色时在光标旁显示的那个**临时**面板（主面板自己不动）。
        /// 同一时刻最多一个，切到别处再右键就复用这一个。
        /// </summary>
        private PaletteWindow _popup;

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

        /// <summary>
        /// 尺寸是否被标题栏上的图钉按钮锁住了。
        ///
        /// 锁住之后拖边缘不再改变大小（面板和弹窗都跟着锁——弹窗的宽度是从主面板
        /// 来的，主面板锁住了弹窗自然也就固定了）。位置拖动不受影响。
        /// 这个状态会写进 session.json，下次打开还是锁着的。
        /// </summary>
        private static bool _sizeLocked;
        /// <summary>锁定状态是否已经从会话里读过一次（见构造函数里的注释）。</summary>
        private static bool _sizeLoaded;

        /// <summary>
        /// 是不是"临时弹出面板"。**必须在 BuildUi 之前就定下来**：标题栏上那两个
        /// 小按钮（— 折叠 / ✕ 收起）只属于主面板，弹窗是"看一眼就走"的，
        /// 有折叠和收起按钮既没用（用户不会去点），点了也只会让弹窗行为古怪。
        /// </summary>
        private readonly bool _isPopup;

        public PaletteWindow(CspClient client, PairingScanner scanner) : this(client, scanner, false)
        {
        }

        private PaletteWindow(CspClient client, PairingScanner scanner, bool isPopup)
        {
            _isPopup = isPopup;
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

            // 注意：滑条的 SizeChanged 订阅**不能**放这儿。三条滑条是 Show 之后才建的
            // （见 BuildSliders），构造函数里 _sliders[0] 还是 null。这个订阅挪到
            // CreateSliderRow 里去了。

            // 跟着 CSP 显示 / 最小化
            _followTimer = new DispatcherTimer();
            _followTimer.Interval = TimeSpan.FromMilliseconds(FollowIntervalMs);
            _followTimer.Tick += OnFollowTick;

            // 右键吸色：盯着鼠标，把面板叫到光标旁
            _pointerHook = new PointerHook();
            _pointerHook.Activity += OnPointerActivity;
        }

        /// <summary>
        /// 建一个临时弹出面板，并**在创建时就把位置定好**。
        /// 读色/写色跟主面板共用同一个 _client，所以在上面拖滑条的效果和在主面板上一样。
        ///
        /// 位置必须在构造阶段就给（而不是 Show 完再 SetWindowPos）：窗口第一次显示时
        /// WPF 会用自己记的 Left/Top 摆放，之后再 SetWindowPos 会被下一次布局覆盖回去
        /// （实测弹窗会卡在构造时的位置不动）。
        /// </summary>
        public static PaletteWindow CreatePopup(CspClient client, PairingScanner scanner,
                                                int cursorX, int cursorY)
        {
            PaletteWindow popup = new PaletteWindow(client, scanner, true);

            // 换掉主面板专属的 Loaded / Closing
            popup.Loaded -= popup.OnLoaded;
            popup.Closing -= popup.OnClosing;
            popup.Loaded += popup.OnPopupLoaded;
            popup.Closing += popup.OnPopupClosing;

            // 关掉"跟着 CSP 显示/最小化"和"全局鼠标钩子"：这两样只能有一份
            popup._followTimer.Stop();
            if (popup._pointerHook != null)
            {
                popup._pointerHook.Activity -= popup.OnPointerActivity;
                popup._pointerHook.Dispose();
                popup._pointerHook = null;
            }

            // 弹出面板绝不出现在任务栏
            popup.ShowInTaskbar = false;

            // 位置：按已知的面板尺寸算好，直接写进 WPF 的 Left/Top。
            // 构造函数里已经从 SessionStore 读到了**主面板**的位置和尺寸，
            // 弹窗跟主面板一样宽，尺寸是可信的。
            double sx = 1.0, sy = 1.0;
            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(popup);
                if (dpi.DpiScaleX > 0) sx = dpi.DpiScaleX;
                if (dpi.DpiScaleY > 0) sy = dpi.DpiScaleY;
            }
            catch (Exception)
            {
            }

            double panelW = popup.Width * sx;
            double panelH = popup.Height * sy;

            double workLeft, workTop, workRight, workBottom;
            GetWorkArea(cursorX, cursorY, out workLeft, out workTop, out workRight, out workBottom);

            double left, top;
            SummonPlacement.Place(cursorX, cursorY, panelW, panelH,
                                  workLeft, workTop, workRight, workBottom,
                                  out left, out top);

            popup.Left = left / sx;
            popup.Top = top / sy;

            return popup;
        }

        private void OnPopupLoaded(object sender, RoutedEventArgs e)
        {
            // 先把三条滑条建出来。主面板是在 OnLoaded 里延迟建的，弹窗不走那条路，
            // 漏了这一步就会只剩一个空背景（踩过：弹窗出来只有一块黑色）。
            // 弹窗是按需创建的，这里同步建掉就行，不用再延迟。
            BuildSliders();

            // 临时面板只负责显示颜色：跟主面板一样把界面刷一遍，但不要任何定时器
            SyncStatusFromClient();
            _mainR = _rgbR; _mainG = _rgbG; _mainB = _rgbB;
            _subR = _rgbR; _subG = _rgbG; _subB = _rgbB;
            ApplyRgbToAxes();
            SyncSlidersFromAxes();
            RefreshGradients(-1);
            ApplyScale();
            RefreshSlotDisplay();
            // 标题得自己刷一次：主面板是在 OnLoaded 的流程里刷的，
            // 临时面板不走那条路，不刷的话窗口标题是空的
            // （任务管理器里也会显示成一个没名字的窗口）。
            UpdateTitle();
        }

        private void OnPopupClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                _client.StateChanged -= OnClientStateChanged;
                _client.ColorsRead -= OnColorsRead;
                _scanner.StatusChanged -= OnScannerStatus;
            }
            catch (Exception)
            {
            }
            // 注意：**不调 Application.Current.Shutdown()**，也不写 SessionStore
        }

        /// <summary>把临时面板挪到光标旁并显示。已经显示着就只挪位置。</summary>
        private void ShowPopupNear(int cursorX, int cursorY)
        {
            if (!IsVisible)
            {
                // 位置在 CreatePopup 里已经定好了，这里只管把它显示出来
                Show();
                FadeIn();
                return;
            }
            PlacePanelNear(cursorX, cursorY);
        }

        // ==================================================================
        // 动效
        //
        // 只做两种，都刻意做得"短、轻、不挡事"：
        //   · 弹窗进出时淡入淡出（90ms / 70ms）
        //   · 状态文字变化时闪一下
        //
        // 不做窗口位移/缩放的动画：主面板的位置和尺寸是通过 Win32 SetWindowPos
        // 直接设的（见 BeginWindowDrag / UpdatePanelResize），WPF 的 Left/Width
        // 跟着动画插值会和它打架，出现"拖完了还在自己飘"。
        // 透明度不影响布局，所以安全。
        // ==================================================================

        /// <summary>淡入。分层窗口（AllowsTransparency）下的 Opacity 动画开销很小。</summary>
        private void FadeIn()
        {
            DoubleAnimation fade = new DoubleAnimation(0.0, 1.0,
                new Duration(TimeSpan.FromMilliseconds(90)));
            fade.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>
        /// 淡出然后关窗。
        ///
        /// 必须防重入：Close() 可能被连着调用两次（用户连点两下画布），
        /// 第二次进来时窗口已经在淡出了，再起一个动画会让它闪。
        /// </summary>
        private void FadeOutAndClose()
        {
            if (_closingFade) return;
            _closingFade = true;

            DoubleAnimation fade = new DoubleAnimation(Opacity, 0.0,
                new Duration(TimeSpan.FromMilliseconds(70)));
            fade.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            fade.Completed += delegate
            {
                try { Close(); }
                catch (Exception) { }
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private bool _closingFade;

        /// <summary>
        /// 召唤进来之后刷新一次显示。临时面板不走 OnLoaded 那一套定时器，
        /// 所以要自己把颜色和读数刷上，不然会显示构造时的占位色。
        /// </summary>
        private void RefreshAfterSummon()
        {
            ApplyRgbToAxes();
            SyncSlidersFromAxes();
            RefreshGradients(-1);
            ApplyScale();
            RefreshSlotDisplay();
        }


        // ==================================================================
        // 跟着 CSP 显示 / 最小化
        // ==================================================================

        /// <summary>
        /// 隔一会儿看一眼 CSP 主窗口：它最小化了我们也最小化，它露出来了我们也还原
        /// （用户："CSP软件最小化我们的软件也最小化"）。认不出 CSP 窗口就什么都不做。
        /// 另外这里还是"切回 CSP 自动显示"的兜底，见下面那段注释。
        /// </summary>
        private void OnFollowTick(object sender, EventArgs e)
        {
            if (_cspWindow != IntPtr.Zero && !IsWindow(_cspWindow)) _cspWindow = IntPtr.Zero;
            if (_cspWindow == IntPtr.Zero && DateTime.Now >= _nextCspLookup)
            {
                _cspWindow = FindCspWindow();
                _nextCspLookup = DateTime.Now.AddSeconds(2.0);
            }
            if (_cspWindow == IntPtr.Zero) return;

            // ---- 兜底：前台已经回到 CSP，可"切回来自己露出来"这一步没生效 ----
            // 用户："用 Alt+Tab 切回 CSP 后直接在画板上画，面板不会显示；点窗口也不会，
            // 要点上面主菜单栏或者窗口里的弹出窗口才显示。"
            // 实测（临时日志）确实如此：切走时报了一条前台事件（于是面板藏起来），
            // 但切回 CSP 时报不报前台事件**不确定**——SetForegroundWindow 回去那一下
            // 就常常没有事件。没有事件，ShowAfterAutoHide 就永远不会被叫到；
            // 而点画板同样不产生前台变化（CSP 本来就是前台），所以也补不回来；
            // 只有点菜单、点 CSP 的浮动面板（都是另一个顶层窗口）才会再报一次前台变化。
            // 所以这里每轮补一次：面板藏着 + 前台已经是 CSP → 露回来。
            //
            // 关键的一条：**只有真的离开过才露回来**（_leftCspWhileHidden）。
            // 用户右键吸色→左键下笔，面板收起时前台本来就是 CSP；
            // 不加这个条件的话，下一轮（600ms 后）就会把它弹出来，等于收不住。
            if (_autoHidden || _dismissedByDrawing)
            {
                uint cspPid, frontPid;
                GetWindowThreadProcessId(_cspWindow, out cspPid);
                GetWindowThreadProcessId(GetForegroundWindow(), out frontPid);

                if (frontPid != cspPid)
                {
                    // 用户在面板收起期间切走了，记下来：等切回来时该还他一个面板
                    _leftCspWhileHidden = true;
                }
                else if (_leftCspWhileHidden)
                {
                    ShowAfterAutoHide();
                }
            }

            if (_minimizedByClose)
            {
                if (WindowState == System.Windows.WindowState.Minimized) return;   // 还收着，别动
                _minimizedByClose = false;   // 用户自己从任务栏还原了，这一笔就算过去了
            }

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
            ReturnFocusToCsp();
        }

        private void ReturnFocusToCsp()
        {
            // 还原有可能顺手把前台抢过来，抢到了就还给 CSP
            if (_cspWindow == IntPtr.Zero) return;
            IntPtr mine = _hwnd;
            if (GetForegroundWindow() == mine) SetForegroundWindow(_cspWindow);
        }

        // ---- 前台窗口换人了（Alt+Tab、点别的程序的任务栏按钮…）----

        private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd,
                                           int idObject, int idChild, uint thread, uint time);

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod,
            WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        /// <summary>
        /// 前台不再是 CSP 就把面板藏起来，切回 CSP 再露回来。
        /// 面板自己带 WS_EX_NOACTIVATE，点它不会成为前台，所以"前台进程不是 CSP"
        /// 就等于"用户跑去别的程序了"，判断很干脆。
        /// 两种前台不参与判断：CSP 没开着（那就维持现状，别把面板藏没了），
        /// 以及前台是自己进程（输入数值时面板会临时变成可激活窗口、把前台要过来，
        /// 这时候把自己藏了就很荒唐）。
        /// </summary>
        private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd,
                                         int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero) return;   // 前台变成"没有窗口"，不理会

            if (_cspWindow != IntPtr.Zero && !IsWindow(_cspWindow)) _cspWindow = IntPtr.Zero;
            if (_cspWindow == IntPtr.Zero) _cspWindow = FindCspWindow();
            if (_cspWindow == IntPtr.Zero) return;

            uint cspPid, frontPid;
            GetWindowThreadProcessId(_cspWindow, out cspPid);
            GetWindowThreadProcessId(hwnd, out frontPid);
            if (frontPid == (uint)Process.GetCurrentProcess().Id) return;

            if (frontPid == cspPid) ShowAfterAutoHide();
            else AutoHide();
        }

        /// <summary>切走了：把面板藏起来（用户按 ✕ 收起的那种藏法不算，两码事）。</summary>
        private void AutoHide()
        {
            if (_autoHidden || !IsVisible) return;
            CommitEditIfOutside(null);      // 正在输入的话先收尾
            _autoHidden = true;
            Hide();
        }

        /// <summary>切回 CSP 了：把自动藏起来的面板露回来。</summary>
        private void ShowAfterAutoHide()
        {
            // 两种"藏着"都该在切回 CSP 时露回来：
            //   _autoHidden          用户切走时自动藏的
            //   _dismissedByDrawing  用户画画时让开的（画完切出去再回来，还希望它在）
            // 用户按 ✕ 收起的、托盘收起的都不在这两个标记里，所以不会被叫回来。
            if (!_autoHidden && !_dismissedByDrawing) return;

            _autoHidden = false;
            _dismissedByDrawing = false;
            _leftCspWhileHidden = false;
            RestorePanel();
            ReturnFocusToCsp();
        }

        /// <summary>把藏起来的面板露出来（可能同时还最小化着）。</summary>
        private void RestorePanel()
        {
            // 不管是托盘、右键召唤还是跟 CSP 同步把它叫回来的，"✕ 最小化"这一笔就算过去了
            _minimizedByClose = false;
            if (!IsVisible) Show();
            if (WindowState == System.Windows.WindowState.Minimized)
            {
                WindowState = System.Windows.WindowState.Normal;
            }
        }

        /// <summary>
        /// CSP 的主窗口：它进程里最大、无主、可见的那个顶层窗口。
        /// 最小化的窗口照样报着正常的还原尺寸，所以按面积挑是对的。
        /// 进程句柄的释放由 ScreenCapture.FindCspProcessIds 负责。
        /// </summary>
        private static IntPtr FindCspWindow()
        {
            List<int> pids = ScreenCapture.FindCspProcessIds();
            if (pids.Count == 0) return IntPtr.Zero;
            HashSet<int> set = new HashSet<int>(pids);

            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!set.Contains((int)pid)) return true;
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
        // 右键吸色：把面板叫到光标旁边
        // ==================================================================
        // 用户的用法：在 CSP 里拿着画笔，**按住右键就是吸色**，这时候正想知道
        // 吸到的这个颜色在 OKLCH 里是多少、顺手调一下。所以：
        //   右键按下 → 面板立刻摆到光标右上边并亮出来（这一步不拦鼠标，
        //              CSP 该吸色照吸；面板是 WS_EX_NOACTIVATE，也不抢前台）；
        //   左键按下（开始画下一笔）→ 面板收回去，不挡画。
        // 只在 CSP 是前台、并且光标确实落在 CSP 窗口上时才管，别的地方右键不碰。

        /// <summary>
        /// 鼠标钩子的回调。<b>在钩子自己的线程上</b>，只做一件事：
        /// 把事件原样丢回 UI 线程。绝不在这个线程上碰 WPF。
        /// </summary>
        private void OnPointerActivity(PointerEvent e)
        {
            PointerEvent copy = e;
            Dispatcher.BeginInvoke(new Action(delegate { HandlePointer(copy); }),
                DispatcherPriority.Input);
        }

        /// <summary>在 UI 线程上判断这一次按键要不要召唤 / 收起面板。</summary>
        private void HandlePointer(PointerEvent e)
        {
            if (e.Down && e.Button == PointerButton.Right)
            {
                if (!IsCspForeground()) return;
                if (!IsOverCspWindow(e.X, e.Y)) return;

                // 正在数值框里打字：先把输入收尾，别让人正输一半的面板飞走
                CommitEditIfOutside(null);
                OnRightClickForColor(e.X, e.Y);
                return;
            }

            // 左键按下 = 这一笔要开始落在画布上了，临时面板让开
            if (e.Down && e.Button == PointerButton.Left)
            {
                // 临时面板：一点画布就收掉（它本来就是"看一眼"的性质）。
                // 点在面板自己身上不算"开始画"——那是用户在拖滑条调色。
                if (_popup != null && !IsOverWindow(_popup._hwnd, e.X, e.Y))
                {
                    ClosePopup();
                }
            }
        }

        /// <summary>
        /// 右键吸色时显示什么：**永远**只在光标旁弹那块临时小面板。
        ///
        /// 不管主面板是显示着还是被 ✕ 收进了托盘，右键都只弹这一个。
        /// 主面板有自己的位置、尺寸和开关，右键跑去动它会让用户莫名其妙
        /// （用户："在收入托盘后，右键会把收入的主界面召唤出来，而不是弹出面板"）。
        /// 主面板只能从托盘图标叫回来。
        /// </summary>
        private void OnRightClickForColor(int cursorX, int cursorY)
        {
            if (_popup == null)
            {
                _popup = CreatePopup(_client, _scanner, cursorX, cursorY);
                // 注意：**不要**给弹窗设 Owner。设了之后 WPF 会把它的位置和显示状态
                // 跟主窗口绑定，我们用 SetWindowPos 摆到光标旁的动作会被盖回去
                // （实测：弹窗一直卡在 -32000,-32000 的屏幕外，怎么都挪不过来）。
                // 弹窗的生命周期由 _popup 字段自己管，主面板关闭时会显式 ClosePopup()。
            }

            // 把主面板当前的颜色/槽位带过去，否则弹窗会显示构造时的占位色
            _popup._colorIndex = _colorIndex;
            _popup._rgbR = _rgbR; _popup._rgbG = _rgbG; _popup._rgbB = _rgbB;
            _popup._mainR = _mainR; _popup._mainG = _mainG; _popup._mainB = _mainB;
            _popup._subR = _subR; _popup._subG = _subG; _popup._subB = _subB;

            _popup.ShowPopupNear(cursorX, cursorY);
            _popup.RefreshAfterSummon();

            if (_client != null && _client.State == CspLinkState.Connected)
            {
                _client.RequestColorSync();
            }
        }

        /// <summary>把一点是不是落在指定窗口（含子窗口）上。</summary>
        private bool IsOverWindow(IntPtr hwnd, int x, int y)
        {
            if (hwnd == IntPtr.Zero) return false;
            POINT p;
            p.X = x;
            p.Y = y;
            IntPtr h = WindowFromPoint(p);
            if (h == IntPtr.Zero) return false;
            if (h == hwnd) return true;
            return GetAncestor(h, GA_ROOT) == hwnd;
        }

        /// <summary>关掉临时弹出面板。</summary>
        private void ClosePopup()
        {
            if (_popup == null) return;
            PaletteWindow popup = _popup;
            _popup = null;
            // 让它自己淡出再关。这里不能直接 Close()：那样是硬消失，
            // 和"弹出来时有淡入"不对称，看起来像闪了一下。
            popup.FadeOutAndClose();
        }

        /// <summary>这一点是不是落在面板自己（含它的子窗口、以及临时弹出面板）上。</summary>
        private bool IsOverOwnWindow(int x, int y)
        {
            if (IsOverWindow(_hwnd, x, y)) return true;
            return _popup != null && IsOverWindow(_popup._hwnd, x, y);
        }

        /// <summary>前台是不是 CSP。认不出 CSP 就当不是，宁可不动。</summary>
        private bool IsCspForeground()
        {
            if (_cspWindow != IntPtr.Zero && !IsWindow(_cspWindow)) _cspWindow = IntPtr.Zero;
            if (_cspWindow == IntPtr.Zero) _cspWindow = FindCspWindow();
            if (_cspWindow == IntPtr.Zero) return false;

            uint cspPid, frontPid;
            GetWindowThreadProcessId(_cspWindow, out cspPid);
            GetWindowThreadProcessId(GetForegroundWindow(), out frontPid);
            return frontPid == cspPid;
        }

        /// <summary>
        /// 这一点是不是落在 CSP 的某个窗口上。菜单、子面板、画布都算——
        /// 只要那个窗口属于 CSP 进程就行，用户在哪块上右键吸色都该有得看。
        /// 落在**我们自己**的窗口上不算：那是用户在点面板（比如切颜色槽），
        /// 这时候还把它挪走就成了"点一下就飞"。
        /// </summary>
        private bool IsOverCspWindow(int x, int y)
        {
            POINT p;
            p.X = x;
            p.Y = y;

            IntPtr h = WindowFromPoint(p);
            if (h == IntPtr.Zero) return false;
            if (h == _hwnd) return false;

            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == (uint)Process.GetCurrentProcess().Id) return false;

            List<int> pids = ScreenCapture.FindCspProcessIds();
            return pids.Count > 0 && pids.Contains((int)pid);
        }

        /// <summary>按光标位置算出该摆哪，并真正挪过去。</summary>
        private void PlacePanelNear(int cursorX, int cursorY)
        {
            if (_hwnd == IntPtr.Zero) return;

            // 面板尺寸用**实际**的窗口矩形：WPF 的 Width/Height 是 DIP，
            // 这里的坐标全是物理像素，高分屏下差一个缩放比例。
            RECT own;
            if (!GetWindowRect(_hwnd, out own)) return;
            double panelW = own.Right - own.Left;
            double panelH = own.Bottom - own.Top;

            double workLeft, workTop, workRight, workBottom;
            GetWorkArea(cursorX, cursorY, out workLeft, out workTop, out workRight, out workBottom);

            double left, top;
            SummonPlacement.Place(cursorX, cursorY, panelW, panelH,
                                  workLeft, workTop, workRight, workBottom,
                                  out left, out top);

            SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top),
                         0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

            // 必须同步回 WPF 的 Left/Top。只管 SetWindowPos 的话，WPF 那边记的还是
            // 旧值，下一次布局（尺寸变化、内容刷新…）就按旧值把窗口摆回去。
            // 主面板当初没暴露这个问题，是因为它从来不需要被"预先挪到屏幕外"；
            // 临时弹出面板需要，于是 WPF 每轮布局都把它拉回 -32000。
            double sx = 1.0, sy = 1.0;
            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0) sx = dpi.DpiScaleX;
                if (dpi.DpiScaleY > 0) sy = dpi.DpiScaleY;
            }
            catch (Exception)
            {
            }
            Left = left / sx;
            Top = top / sy;
        }

        /// <summary>光标所在那块显示器的工作区（已经扣掉任务栏）。拿不到就退回整个虚拟屏幕。</summary>
        private static void GetWorkArea(int x, int y,
                                        out double left, out double top,
                                        out double right, out double bottom)
        {
            POINT p;
            p.X = x;
            p.Y = y;

            MONITORINFO info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            IntPtr monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                left = info.rcWork.Left;
                top = info.rcWork.Top;
                right = info.rcWork.Right;
                bottom = info.rcWork.Bottom;
                return;
            }

            left = SystemParameters.VirtualScreenLeft;
            top = SystemParameters.VirtualScreenTop;
            right = left + SystemParameters.VirtualScreenWidth;
            bottom = top + SystemParameters.VirtualScreenHeight;
        }

        // ==================================================================
        // 托盘图标（右下角通知区域）
        // ==================================================================

        /// <summary>托盘图标挂上去，右键菜单里是退出。</summary>
        private void CreateTrayIcon()
        {
            try
            {
                _tray = new System.Windows.Forms.NotifyIcon();
                _tray.Icon = LoadAppIcon(16);
                _tray.Text = TrayText;
                _tray.ContextMenuStrip = BuildTrayMenu();
                // 双击 = 把面板叫回来：标题栏上的 ✕ 只是收起来，这是唯一的入口
                _tray.DoubleClick += delegate { ShowPanel(); };
                _tray.Visible = true;
            }
            catch (Exception)
            {
                if (_tray != null)
                {
                    try { _tray.Dispose(); } catch (Exception) { }
                    _tray = null;
                }
            }

            // NotifyIcon 把 Shell_NotifyIcon 的返回值吞掉了，注册失败也是静默的。
            // 这里做一次**只读**环境检查（任务栏在不在、完整性级别够不够），
            // 结论只用来决定 ✕ 怎么收——**绝不动已注册好的图标**
            // （详见 TraySupport 的注释：碰通知区域的版本把好好的图标弄没过）。
            string detail;
            bool trayUsable = TraySupport.IsTrayUsable(out detail);
            _trayAvailable = TraySupport.ResolveCloseMode(_tray != null, trayUsable)
                             == PanelCloseMode.HideToTray;
        }


        // ---- Explorer 重启后把图标重新挂上去 ----

        /// <summary>
        /// 后缀。Explorer 重启（或崩溃后自动拉起）会广播 "TaskbarCreated"：
        /// 那个消息一到，之前用 Shell_NotifyIcon 注册的图标**全部作废**，
        /// 必须重新 Add 一遍，否则程序还活着、托盘里却永远没有它了。
        /// 这是"图标不见了"最常见的第二种成因，必须处理。
        /// </summary>
        private uint _taskbarCreatedMessage;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    // 让外壳先把旧图标清干净，再重新 Add
                    if (_tray != null)
                    {
                        try { _tray.Dispose(); } catch (Exception) { }
                        _tray = null;
                    }
                    CreateTrayIcon();
                }), DispatcherPriority.Background);
            }
            return IntPtr.Zero;
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
        /// <summary>整块窗口对鼠标透明：点击穿透到下层窗口（托盘提示浮层用）。</summary>
        private const int WS_EX_TRANSPARENT = 0x00000020;
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

        // ---- 右键召唤：光标在哪块显示器、那块显示器的工作区（不含任务栏）----

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        /// <summary>屏幕这一点上是哪个窗口——用来判断右键是不是点在 CSP 上。</summary>
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        private const uint GA_ROOT = 2;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>本窗口的 HWND。在 OnSourceInitialized 里记下来，省得各处反复 new 一个 helper。</summary>
        private IntPtr _hwnd;

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

            // 只有三条滑条，面板天生就是横的一条：宽度是唯一的尺寸参数，高度按它算。
            // 初始宽度用主面板的标定宽度 340 —— 那是用户在调参台里定这套观感时的尺寸，
            // 首次启动就是这个大小。之后尺寸从 session.json 读。
            Width = _isPopup ? DesignWidth : MainReferenceWidth;
            Height = ContentHeight(ScaleForWidth(Width));
            MinWidth = MinPanelWidth;
            MinHeight = CollapsedHeight(MinPanelWidth / DesignWidth);

            double left, top, w, h;
            SessionStore.GetWindowRect(out left, out top, out w, out h);
            // 图钉的锁定状态是全局的（主面板锁了，弹窗也跟着固定），而且 _sizeLocked
            // 是静态字段——**只在第一次建窗口时从会话里读**。
            // 每次构造都重读会出事：用户点了图钉锁上之后，右键一弹临时面板，
            // 弹窗的构造函数就把这个状态按磁盘上的旧值覆盖回去了
            // （实测：锁定看起来生效了，但紧接着的拖动还是把面板放大了）。
            if (!_sizeLoaded)
            {
                _sizeLocked = SessionStore.GetSizeLocked();
                _sizeLoaded = true;
            }
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
            _hwnd = hwnd;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            // NOACTIVATE：点面板不抢 CSP 的前台。
            // TOOLWINDOW：不上任务栏、也不进 Alt+Tab——退出改走右下角托盘图标的右键菜单
            // （用户："把任务栏改到放置到如图，右边的图标上，这样TAB里就不会显示了"）
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            // 临时弹出面板不装前台钩子：那是主面板的活，装两份会互相打架
            // （弹窗一出现，主面板的前台钩子看到"前台不是 CSP"就会把自己藏起来）。
            if (_isPopup) return;

            // 盯着前台窗口的变化：切到别的程序就把面板藏起来
            _foregroundProc = OnForegroundChanged;
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Explorer 重启会广播 TaskbarCreated，届时得把托盘图标重新挂一遍
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null) source.AddHook(OnWindowMessage);
        }

        // ==================================================================
        // 界面
        // ==================================================================

        /// <summary>
        /// 三条滑条建好没有。
        ///
        /// 启动时窗口先只建"外壳"（外框 + 标题栏）就 Show 出来，滑条在**第一帧画完
        /// 之后**才建——不然用户要干等一秒钟看着什么都没有。这期间客户端事件可能
        /// 已经进来了（连上 CSP 是毫秒级的事），所以凡是碰滑条的地方都要先问一句。
        /// </summary>
        private bool SlidersReady
        {
            get { return _sliders[0] != null; }
        }

        /// <summary>
        /// 建界面外壳：外框、四行网格、标题栏。
        /// 这三样决定了窗口的**尺寸和外观轮廓**，必须在 Show 之前完成，
        /// 否则窗口会以错误的尺寸闪一下。三条滑条不在这一步（见 <see cref="BuildSliders"/>）。
        /// </summary>
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

            // 放大镜是个**独立窗口**（见 Magnifier 里的说明：面板太矮，
            // 滑条上方那块地方在窗口外面，挂在布局里会被裁掉）。
            // 主面板和临时取色面板各有一个，互不影响——弹窗很小，
            // 但它自己的滑条也要能放大看。
            if (_magnifier == null)
            {
                _magnifier = new Magnifier();
                _magnifier.BoxWidth = 120;
                _magnifier.BodyHeight = 28;
                _magnifier.TailHeight = 8;
            }

            // ---- 标题栏：左边连接状态，右边 — 和 ✕ ----
            // 整条也是拖动把手（双击 = 展开/折叠）；退出在托盘图标的右键菜单里
            Grid header = new Grid();
            _header = header;
            header.Background = Brushes.Transparent;
            header.Height = HeaderHeight;
            header.ColumnDefinitions.Add(new ColumnDefinition());   // 状态（占剩下的）
            header.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            if (!_isPopup)
            {
                // 临时弹出面板不要这三个按钮：它只是"看一眼吸到的颜色"，
                // 锁定 / 折叠 / 收起对它都没有意义（收起它 = 直接消失，用户也没处点回来）。
                header.ColumnDefinitions.Add(new ColumnDefinition());   // 📌
                header.ColumnDefinitions.Add(new ColumnDefinition());   // —
                header.ColumnDefinitions.Add(new ColumnDefinition());   // ✕
                header.ColumnDefinitions[1].Width = GridLength.Auto;
                header.ColumnDefinitions[2].Width = GridLength.Auto;
                header.ColumnDefinitions[3].Width = GridLength.Auto;
            }
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

            // 临时弹出面板不需要标题栏：上面既没有按钮，也不该显示连接状态
            // （右键吸色的前提就是已经连上 CSP 了）。留着它只会白占一条 14px 的带子，
            // 把三条滑条往下挤（用户："上下距离太大了"）。
            // 位置照旧留着（Grid 第 0 行高度在 ApplyScale 里设成 0），
            // 这样代码里其它地方引用 _header 都不用判空。
            if (_isPopup) header.Visibility = Visibility.Collapsed;

            if (!_isPopup)
            {
                // 📌 锁定尺寸：按下后拖边缘不再改变大小（面板和弹窗一起锁）。
                // 放在 — 左边，三个按钮从右往左是 ✕ / — / 📌。
                _pinButton = new CaptionButton(CaptionButton.GlyphKind.Pin);
                _pinButton.VerticalAlignment = VerticalAlignment.Center;
                _pinButton.IsActive = _sizeLocked;
                _pinButton.Click += delegate { ToggleSizeLock(); };
                UpdatePinToolTip();
                Grid.SetColumn(_pinButton, 1);
                header.Children.Add(_pinButton);

                // — 折叠成只剩标题栏（再点一次展开，或者双击标题栏）
                _collapseButton = new CaptionButton("\u2014");
                _collapseButton.VerticalAlignment = VerticalAlignment.Center;
                _collapseButton.ToolTip = "折叠成只剩标题栏（再点一次展开，双击标题栏也行）";
                _collapseButton.Click += delegate { ToggleCollapse(); };
                Grid.SetColumn(_collapseButton, 2);
                header.Children.Add(_collapseButton);

                // ✕ 不退出程序，只是把面板收起来；从托盘图标能再打开
                _hideButton = new CaptionButton("\u2715");
                _hideButton.VerticalAlignment = VerticalAlignment.Center;
                _hideButton.ToolTip = "收起面板（点托盘图标可以再打开）";
                _hideButton.Click += delegate { HidePanel(); };
                Grid.SetColumn(_hideButton, 3);
                header.Children.Add(_hideButton);
            }

            header.MouseLeftButtonDown += OnHeaderMouseDown;
            header.MouseMove += OnHeaderMouseMove;
            header.MouseLeftButtonUp += OnHeaderMouseUp;

            // 面板空白处也能拖动窗口或拖边缘改大小
            _root.PreviewMouseLeftButtonDown += OnRootMouseDown;
            _root.PreviewMouseMove += OnHeaderMouseMove;
            _root.PreviewMouseLeftButtonUp += OnHeaderMouseUp;

            // 外壳已就位：先把行高/列宽按当前宽度算好，窗口 Show 出来才是对的尺寸，
            // 而不是等滑条建完才撑开。
            ApplyScale();
        }

        /// <summary>
        /// 建三条滑条（轴名 + 渐变 + 数值）并接好它们的事件。
        ///
        /// 单独拆出来是为了"先让窗口出现在屏幕上，再把内容填进去"：
        /// 这三个控件第一次创建时要 JIT 一大堆代码（实测第 0 条 380ms、
        /// 后两条各几毫秒），全部前置会让启动多等一秒多。
        /// </summary>
        private void BuildSliders()
        {
            if (SlidersReady) return;

            for (int axis = 0; axis < 3; axis++)
            {
                CreateSliderRow(axis + 1, axis);
            }

            // 双击滑块 = 锁定，锁定后不许把颜色拖出 sRGB
            for (int axis = 0; axis < 3; axis++)
            {
                int which = axis;
                _sliders[axis].LockToggled += delegate { OnLockToggled(which); };
            }

            RebuildClampers();
            SyncSlidersFromAxes();

            // 这里**不能**直接 ShowStatus("扫描二维码")。
            // 滑条是延迟到第一帧之后才建的（见 OnLoaded 里的注释），等执行到这儿时
            // 连接状态可能早就定了：历史配对重连很快，OnClientStateChanged(Connected)
            // 已经把状态栏藏起来了；这一句硬写回去，顶栏就会一直挂着"扫描二维码"，
            // 明明已经连上了（用户报的 bug）。
            // 交给 SyncStatusFromClient 按当前真实状态决定。
            SyncStatusFromClient();

            // 滑条刚建出来，尺寸/比例都要再算一遍
            ApplyScale();
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

            // 挂在滑块上而不是窗口上：等滑块自己排完版再缩放，位置和尺寸才是新的。
            // 只在第 0 条上挂一次就够（三条一起变）。
            if (axis == 0) slider.SizeChanged += delegate { ApplyScale(); };

            // 三条色条的粗细比例来自调参台：主面板 0.77、弹窗 0.95。
            // 两个面板各给一份，互不影响（这里用控件级覆盖，不是全局静态）。
            if (_isPopup)
            {
                slider.TrackRatioOverride = PopupTrackRatio;
                slider.MarkerHeightRatioOverride = PopupMarkerRatio;
                slider.MarkerGapRatioOverride = PopupMarkerGap;
            }
            else
            {
                slider.TrackRatioOverride = MainTrackRatio;
                slider.MarkerHeightRatioOverride = MainMarkerRatio;
                slider.MarkerGapRatioOverride = MainMarkerGap;
            }

            // 渐变内容由面板提供：这两个回调只是读当前值来算，装一次就够
            int gradientAxis = axis;
            slider.ColorAt = delegate(double t) { return ColorForAxis(gradientAxis, t); };
            slider.InGamutAt = delegate(double t) { return InGamutForAxis(gradientAxis, t); };

            // 拖这条滑条时，把焦点喂给放大镜。主面板和弹窗都接——
            // 弹窗虽小，但它自己的滑条同样需要放大看（用户要求）。
            slider.MagnifierRef = _magnifier;

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
            // 双击标题栏 = 展开 / 折叠（用户要求），别再当成拖动窗口。
            // 临时弹出面板不参与折叠：它没有—按钮，折叠起来就再也展开不了。
            if (e.ClickCount == 2 && !_isPopup && !IsOnInteractiveControl(e.OriginalSource))
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
            // 尺寸被图钉锁住了：整个边缘热区不响应，光标也不会变成缩放箭头
            if (_sizeLocked) return false;

            int edge = GetResizeEdge(e.GetPosition(this));
            if (edge == 0) return false;

            if (!GetCursorPos(out _resizeStartCursor)) return false;
            if (!GetWindowRect(_hwnd, out _resizeStartRect)) return false;

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

            SetWindowPos(_hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
                         SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void UpdateResizeCursor(Point p)
        {
            // 锁住尺寸时不给缩放箭头，否则光标一直提示"能拖"但拖不动，很误导
            if (_sizeLocked)
            {
                Cursor = null;
                return;
            }

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

            if (!GetCursorPos(out _dragStartCursor)) return;
            if (!GetWindowRect(_hwnd, out _dragStartRect)) return;

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

                SetWindowPos(_hwnd, IntPtr.Zero,
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

            bool moved = SyncRectFromActual(_skipHeightSync);
            _skipHeightSync = false;

            // 位置尺寸真的变了才落盘（点一下标题栏也会走到这儿，没必要每次写文件）
            if (moved) SessionStore.SetWindowRect(Left, Top, Width, Height);
        }

        /// <summary>
        /// 把 WPF 记录的 Left/Top/Width/Height 和窗口实际位置尺寸对齐。
        /// 拖动和缩放全程是用 Win32 物理像素做的，不同步的话关闭时存的还是老值。
        /// 返回是否真的动过（用来决定要不要写盘）。
        /// </summary>
        private bool SyncRectFromActual(bool skipHeight)
        {
            RECT rect;
            if (!GetWindowRect(_hwnd, out rect)) return false;

            bool changed = false;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0)
            {
                double newLeft = rect.Left / dpi.DpiScaleX;
                double newWidth = (rect.Right - rect.Left) / dpi.DpiScaleX;
                if (Math.Abs(newLeft - Left) > 1.0) { Left = newLeft; changed = true; }
                if (Math.Abs(newWidth - Width) > 1.0) { Width = newWidth; changed = true; }
            }
            if (dpi.DpiScaleY > 0)
            {
                double newTop = rect.Top / dpi.DpiScaleY;
                if (Math.Abs(newTop - Top) > 1.0) { Top = newTop; changed = true; }
                // 高度是面板自己按宽度算出来的，只有刚切换过折叠时不能碰
                // （那会儿窗口还没重排完，读到的是旧高度）
                if (!skipHeight)
                {
                    double newHeight = (rect.Bottom - rect.Top) / dpi.DpiScaleY;
                    if (Math.Abs(newHeight - Height) > 1.0) { Height = newHeight; changed = true; }
                }
            }
            return changed;
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
            bool chromaPulled = false;
            if (!AnyAxisLocked() && axis != ChromaAxis)
            {
                chromaPulled = ConstrainChroma();
            }

            RecomputeRgbFromAxes();
            SaveSelectedSlotColor();
            // 正在拖的那条自己的渐变不用重建（它只沿本轴取样，另外两个轴才是它的输入），
            // 除非上面把彩度收回来了——那会连带改掉它。
            RefreshGradients(chromaPulled ? -1 : axis);
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
            // 启动早期滑条还没建（见 BuildSliders）；建好后会再调一次
            if (!SlidersReady) return;
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
        /// 返回是否真的动过彩度（调用方据此判断别的那条滑块的渐变要不要重画）。
        /// </summary>
        private bool ConstrainChroma()
        {
            if (AxesInGamut()) return false;

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
            return true;
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
            // 启动早期滑条还没建（见 BuildSliders）
            if (!SlidersReady) return;
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
            return AxisShownToT(axis, AxisValue(axis) * AxisScale[axis]);
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
        /// 显示值 → 滑块位置（0..1）。AxisToT 就是拿当前值套用它；
        /// 用户在数值框里敲进来的也是这个单位，直接传进来就行。
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
            IntPtr hwnd = _hwnd;
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
            IntPtr hwnd = _hwnd;
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

        /// <summary>
        /// 让会变的那些滑条重画渐变。unchangedAxis 那条跳过——渐变的内容是"沿本轴取样、
        /// 另外两个轴固定"，所以拖动某一条时正好只有**另外两条**的内容会变
        /// （每画一次要算 129 个色标，少画一条就少三分之一的活）。
        /// 回调本身在 CreateSliderRow 里装过一次，它是读当前值算的，不用重装。
        /// </summary>
        private void RefreshGradients(int unchangedAxis)
        {
            // 启动早期滑条还没建（见 BuildSliders）
            if (!SlidersReady) return;
            for (int i = 0; i < 3; i++)
            {
                if (i == unchangedAxis) continue;
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
            Title = AppShortName + " — " + FormatSlotOneLine();
            if (_tray != null)
            {
                // NotifyIcon.Text 超过 63 字符会直接抛异常（TrayText 已经截断了，
                // 这里再兜一层，免得哪天标题格式一变就把整个刷新流程带崩）
                try { _tray.Text = TrayText; }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// 托盘图标的悬停提示。系统限制 63 个字符，超了 SetText 会抛异常
        /// （托盘图标可能还没建好，那会儿这个属性没人用）。
        /// </summary>
        private string TrayText
        {
            get
            {
                string text = Title;
                return text.Length > 63 ? text.Substring(0, 63) : text;
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
        /// 面板外框的高度。
        ///
        /// 统一成一条公式，两个面板都适用：
        ///   2×边框 + 上内边距 + 下内边距 + 标题栏 + 3×(行高 + 2×行缝)
        /// 其中除标题栏外都乘了各自的缩放倍率。
        ///
        /// **面板高度就是这么多**，多出来的高度不会再变成底部的一条空带
        /// （用户圈着截图说"红框的位置贴近"）。标题栏上方没有内边距，
        /// 它紧贴窗口顶边——这样折叠后剩下的那一块和展开时的标题栏完全重合。
        /// </summary>
        private double ContentHeight(double s)
        {
            if (_isPopup)
            {
                // 弹窗：没有标题栏。248 宽（k=1）时 = 0 + 0 + 6 + 3*(19+2) = 69
                double k = PopupScaleFor(s);
                return 2.0 * PopupBorderWidth * k
                     + (PopupPadTop + PopupPadBottom) * k
                     + 3.0 * (PopupRowHeight + 2.0 * PopupRowGap) * k;
            }

            // 主面板：340 宽（k=1）时 = 0 + 0 + 5 + 18 + 3*(34+2) = 131
            double m = MainScaleFor(s);
            return 2.0 * MainBorderWidth * m
                 + (MainPadTop + MainPadBottom) * m
                 + HeaderHeight
                 + 3.0 * (MainRowHeight + 2.0 * MainRowGap) * m;
        }

        /// <summary>
        /// 折叠后的高度：只剩标题栏。这时标题栏下方的内边距也收掉了（见 ApplyScale），
        /// 所以就是"上边框 + 标题栏 + 下边框"，标题栏本身的位置和大小跟展开时一模一样。
        /// </summary>
        private double CollapsedHeight(double s)
        {
            double m = _isPopup ? PopupScaleFor(s) : MainScaleFor(s);
            double border = (_isPopup ? PopupBorderWidth : MainBorderWidth) * m;
            return 2.0 * border + (_isPopup ? 0.0 : HeaderHeight);
        }

        /// <summary>该宽度对应的比例（宽度决定一切，高度是算出来的）。</summary>
        private static double ScaleForWidth(double w)
        {
            return ClampDouble(w / DesignWidth, MinPanelWidth / DesignWidth, MaxPanelWidth / DesignWidth);
        }

        private void ApplyScale()
        {
            // 只要求外壳存在。滑条可能还没建（启动时窗口先显示、内容后填），
            // 下面凡是碰滑条的地方都逐个判空。
            if (_header == null) return;
            double w = ActualWidth > 1 ? ActualWidth : Width;
            if (w <= 1) return;

            double s = ScaleForWidth(w);

            // 两套样式各自有标定宽度：主面板 340、弹窗 248。
            // 都按 实际宽度/标定宽度 等比缩放，所以任何宽度下都只是整体放大缩小，
            // 不会出现"面板变宽了但字还是原来那么小"这种锁死感。
            double k = _isPopup ? PopupScaleFor(s) : MainScaleFor(s);

            // 临时弹出面板没有标题栏（见 BuildUi 里那句 header.Visibility），
            // 第 0 行直接给 0，把空间全让给三条滑条。
            // 主面板标题栏固定 18，不跟着缩放（用户："只需要18像素"）。
            double headerH = _isPopup ? 0.0 : HeaderHeight;
            _grid.RowDefinitions[0].Height = new GridLength(headerH);
            _header.Height = headerH;
            // 两个小按钮比标题栏矮一点，才不会顶满整条（照 CSP 那样留一圈缝）
            // 按钮高度直接顶满标题栏：图标里要塞下"上盘+腰+下盘+针"四段，
            // 15px 高的按钮每段分不到 3px，怎么画都糊。
            double capBtnH = Math.Round(headerH);
            if (_pinButton != null) _pinButton.SetSize(capBtnH);
            if (_collapseButton != null) _collapseButton.SetSize(capBtnH);
            if (_hideButton != null) _hideButton.SetSize(capBtnH);

            // 内边距 / 圆角 / 边框
            double padSide, padTop, padBottom, corner, borderTh;
            if (_isPopup)
            {
                padSide = PopupPadSide * k;
                padTop = PopupPadTop * k;
                padBottom = PopupPadBottom * k;
                corner = PopupCornerRadius * k;
                borderTh = PopupBorderWidth * k;
            }
            else
            {
                padSide = MainPadSide * k;
                padTop = MainPadTop * k;
                // 折叠时下方内边距收掉，标题栏位置在两种状态下完全重合
                padBottom = _collapsed ? 0.0 : MainPadBottom * k;
                corner = MainCornerRadius * k;
                borderTh = MainBorderWidth * k;
            }
            _root.Padding = new Thickness(padSide, padTop, padSide, padBottom);
            _root.CornerRadius = new CornerRadius(corner);
            _root.BorderThickness = new Thickness(borderTh);

            double slideH = (_isPopup ? PopupRowHeight : MainRowHeight) * k;
            double rowGap = (_isPopup ? PopupRowGap : MainRowGap) * k;
            for (int i = 0; i < 3; i++)
            {
                _grid.RowDefinitions[i + 1].Height = _collapsed ? new GridLength(0) : new GridLength(slideH);
                if (_sliderRows[i] == null) continue;      // 滑条还没建（启动早期）
                _sliderRows[i].Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
                _sliderRows[i].Margin = new Thickness(0, rowGap, 0, rowGap);
            }

            // 滑条还没建就不用往下算了：行高已经设好，窗口尺寸也就定了
            if (!SlidersReady) return;

            // 轴名（L/C/H）和数值框（数字 + 箭头方块）同样按倍率走
            double labelW = (_isPopup ? PopupLabelWidth : MainLabelWidth) * k;
            double valueW = (_isPopup ? PopupValueWidth : MainValueWidth) * k;
            double boxH = (_isPopup ? PopupBoxHeight : MainBoxHeight) * k;
            double font = (_isPopup ? PopupFontSize : MainFontSize) * k;

            for (int i = 0; i < 3; i++)
            {
                _labelCols[i].Width = new GridLength(labelW);
                _valueCols[i].Width = new GridLength(valueW);
                _values[i].SetBox(boxH);
            }

            if (Math.Abs(_labels[0].FontSize - font) > 0.05)
            {
                for (int i = 0; i < 3; i++)
                {
                    _labels[i].FontSize = font;
                    _values[i].TextSize = font;
                }
            }

            // 放大镜：跟着面板缩放改尺寸。位置在它自己那边按屏幕坐标算，
            // 这里只给尺寸（见 Magnifier.Reposition）。
            if (_magnifier != null)
            {
                _magnifier.BoxWidth = Math.Max(70.0, Math.Min(Width * Theme.MagnifierWidthRatio, 240.0 * k));
                _magnifier.BodyHeight = Math.Max(16.0, Theme.MagnifierHeight * k);
                _magnifier.TailHeight = Math.Max(5.0, Theme.MagnifierTailHeight * k);
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
        /// 点标题栏上的 📌：锁定 / 解锁面板尺寸。
        ///
        /// 锁定之后拖边缘不再改变大小（位置照旧可以拖）。弹窗的宽度是从主面板来的，
        /// 所以主面板一锁，弹窗自然也就固定了。状态会写进 session.json。
        /// </summary>
        private void ToggleSizeLock()
        {
            _sizeLocked = !_sizeLocked;
            if (_pinButton != null) _pinButton.IsActive = _sizeLocked;
            UpdatePinToolTip();
            SessionStore.SetSizeLocked(_sizeLocked);
        }

        private void UpdatePinToolTip()
        {
            if (_pinButton == null) return;
            _pinButton.ToolTip = _sizeLocked
                ? "尺寸已锁定：拖边缘不会再改变大小（点一下解锁）"
                : "锁定面板与弹出面板的尺寸（锁住后拖边缘也不会变大小）";
        }

        /// <summary>
        /// 点标题栏上的 ✕：把面板收起来（**不退出程序**，用户要求"按x不真关闭，
        /// 通过托盘图标能再次打开"）。真正的退出在托盘图标的右键菜单里。
        /// </summary>
        private void HidePanel()
        {
            // 收起来前把数值框的输入结束掉，免得留着一个抢着键盘焦点的编辑框
            CommitEditIfOutside(null);

            // 用户明确按 ✕ 收起：两个"自动露回来"的标记都要清掉，
            // 否则切回 CSP 时它自己又跳出来了（用户要的是"只有托盘能叫回来"）。
            _autoHidden = false;
            _dismissedByDrawing = false;
            _leftCspWhileHidden = false;

            // 托盘指望不上就不能 Hide()：这个窗口是 WS_EX_TOOLWINDOW，任务栏和 Alt+Tab
            // 里都没有它，Hide() 之后是真的找不回来了（用户报的"点了 ✕ 就不见了"）。
            // 退回最小化到任务栏——难看一点，但至少点得到。
            if (!_trayAvailable)
            {
                _minimizedByClose = true;
                FallBackToTaskbar();
                WindowState = System.Windows.WindowState.Minimized;
                return;
            }

            // 提示要记在收起来**之前**：面板一 Hide，位置尺寸就不好拿了
            double hintLeft = Left;
            double hintTop = Top;
            double hintW = Width;
            double hintH = Height;
            Hide();

            // 只有"真的收进托盘了"才弹这个提示。托盘不可用时面板是退回任务栏
            // 最小化的，这时候再说"已收起到托盘"就是在骗人；而且这个浮层是个
            // 独立顶层窗口，会盖在屏幕上挡住鼠标（冒烟测试里它就是这么干扰的）。
            if (_trayAvailable) ShowTrayHint(hintLeft, hintTop, hintW, hintH);
        }

        /// <summary>
        /// 托盘不可用时的退路：把 WS_EX_TOOLWINDOW 摘掉，让窗口重新出现在任务栏上，
        /// 这样最小化之后用户还点得到它（Normal 状态下它照旧浮在 CSP 上面）。
        /// </summary>
        private void FallBackToTaskbar()
        {
            try
            {
                if (_hwnd == IntPtr.Zero) return;
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) == 0) return;
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_TOOLWINDOW);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 面板收起来之后弹一句提示，位置就在面板原来待的地方。
        /// **不用系统的气泡通知**（NotifyIcon.ShowBalloonTip）：那玩意儿要先注册
        /// AppUserModelID、还受"通知与操作"开关和专注助手管，实测在这台机器上根本不弹
        /// （用户："收到托盘不弹通知"）。自己画一个小的浮层最稳，2 秒后淡出。
        /// </summary>
        private void ShowTrayHint(double left, double top, double panelW, double panelH)
        {
            // 文字只有"已收起至托盘"六个字，浮层跟着收窄，别留一大片空。
            // 下限 110 保证不把字挤掉，上限 200 免得面板很宽时浮层也拉得很宽。
            double w = Math.Min(Math.Max(panelW * 0.55, 110.0), 200.0);
            double h = 30.0;

            Window hint = new Window();
            hint.WindowStyle = WindowStyle.None;
            hint.AllowsTransparency = true;
            hint.Background = Brushes.Transparent;
            hint.Topmost = true;
            hint.ShowInTaskbar = false;
            hint.ShowActivated = false;      // 不抢 CSP 的前台
            hint.IsHitTestVisible = false;   // 纯提示，别吃鼠标
            hint.Title = AppShortName;
            hint.Width = w;
            hint.Height = h;
            hint.Left = left + (panelW - w) / 2.0;
            hint.Top = top + (panelH - h) / 2.0;

            // 光设 IsHitTestVisible 只能挡住 WPF 内部的命中测试：这个窗口在系统看来
            // 仍然是一块真正的窗口，鼠标点上去会被它吃掉（冒烟测试里就是这么
            // 干扰了后续操作的）。再补一个 WS_EX_TRANSPARENT，让它**整块对鼠标透明**，
            // 点击直接穿透到下层的 CSP 画布。
            hint.SourceInitialized += delegate
            {
                try
                {
                    IntPtr hintHwnd = new WindowInteropHelper(hint).Handle;
                    if (hintHwnd == IntPtr.Zero) return;
                    int ex = GetWindowLong(hintHwnd, GWL_EXSTYLE);
                    SetWindowLong(hintHwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
                }
                catch (Exception)
                {
                }
            };

            Border box = new Border();
            box.Background = Theme.PanelBackground;
            box.BorderBrush = Theme.PanelBorder;
            box.BorderThickness = new Thickness(1);
            box.CornerRadius = new CornerRadius(8);
            box.Padding = new Thickness(12, 0, 12, 0);

            TextBlock text = new TextBlock();
            text.Text = "已收起至托盘";
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
            // 用户明确要它出来，自动隐藏的标记就作废（不然下一次前台变化会把它再藏回去）
            _autoHidden = false;
            _dismissedByDrawing = false;
            _leftCspWhileHidden = false;

            RestorePanel();
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
                // 启动早期滑条还没建（见 BuildSliders）：没有控件可更新，
                // 颜色等建好之后 OnLoaded 里那次 RefreshSlotDisplay 会补上。
                if (!SlidersReady) return;
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
                RefreshGradients(-1);
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
                // 不信传进来的 state：它是事件入队那一刻的快照。
                // UI 线程忙的时候（比如延迟建滑条那 300ms）队列里会堆好几条，
                // 执行顺序和真实状态可能对不上，结果是"已经连上了，顶栏还挂着
                // 等待扫描二维码"（用户报的 bug）。
                // 一律按客户端**此刻**的真实状态决定顶栏怎么写。
                SyncStatusFromClient();

                if (_client != null && _client.State == CspLinkState.Connected)
                {
                    _autoScanStarted = false;
                    RefreshSlotDisplay();
                }

                // CSP 每次重开「连接手机」窗口，端口和密码都会变，旧的配对就失效了。
                // 这种情况下自动重新扫二维码，但只自动发起一次、最多扫 10 次，
                // 免得屏幕上没有二维码时一直占着 CPU 扫下去。
                CspLinkState now = _client == null ? state : _client.State;
                if ((now == CspLinkState.Disconnected || now == CspLinkState.Unpaired)
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
                // 扫描器的状态文字只在"还没连上"时才有意义。
                // 判断用客户端此刻的真实状态，不是入队时的快照——
                // 否则连上之后扫描器最后那几条回调还会把顶栏改回"等待扫描二维码"。
                if (_client != null && _client.State == CspLinkState.Connected) return;
                ShowStatus(text);
            }));
        }

        /// <summary>
        /// 按客户端当前状态补一次顶栏文字。重连是毫秒级的，状态变化有可能发生在
        /// 窗口订阅事件之前，没有这一步顶栏会一直挂着启动时那句「等待扫描二维码」。
        ///
        /// 三种情况都要处理：
        ///   · 已连接      → 藏起来，整条让给拖动
        ///   · 有说明文字  → 显示它
        ///   · 没有说明    → 按状态给一句兜底，别留空白
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
            if (!string.IsNullOrEmpty(detail))
            {
                ShowStatus(detail);
                return;
            }

            // StateDetail 是空的：给一句兜底，否则顶栏会是一片空白，
            // 用户看不出程序到底在等什么。
            ShowStatus(_client.State == CspLinkState.Unpaired ? "扫描二维码" : "连接中…");
        }

        private void ShowStatus(string text)
        {
            // 文字真变了才闪一下。这方法被 CSP 的轮询频繁调到（每次状态更新都走这里），
            // 无条件起动画会让状态栏一直抖，反而更不流畅。
            bool changed = _status.Text != text;

            _status.Text = text;
            _status.Visibility = Visibility.Visible;

            if (changed)
            {
                DoubleAnimation fade = new DoubleAnimation(0.35, 1.0,
                    new Duration(TimeSpan.FromMilliseconds(140)));
                fade.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                _status.BeginAnimation(OpacityProperty, fade);
            }

            UpdateTitle();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 这里只做"不影响面板长什么样、又必须马上做"的事。
            // 面板此刻只有外壳（外框 + 标题栏），尺寸已经在构造时定好了。
            ClampToScreen();
            SyncStatusFromClient();
            _mainR = _rgbR; _mainG = _rgbG; _mainB = _rgbB;
            _subR = _rgbR; _subG = _rgbG; _subB = _rgbB;
            ApplyScale();
            _followTimer.Start();

            // 剩下的都挪到第一帧画完之后。三件事都要花时间：
            //   · 建三条滑条：第一次创建时 GradientSlider / ValueBox 要 JIT 一大堆
            //     代码（实测第 0 条 380ms，后两条各几毫秒）
            //   · 建托盘图标：会把 System.Windows.Forms 整个拉起来
            //   · 找 CSP 窗口：要枚举进程
            // 先让窗口出现在屏幕上，用户就不会对着空白干等。
            // 对齐 CSP 仍然只比 600ms 那趟轮询早，不会漏掉「一上来 CSP 就是最小化的」。
            Dispatcher.BeginInvoke(new Action(delegate
            {
                BuildSliders();
                ApplyRgbToAxes();
                SyncSlidersFromAxes();
                RefreshGradients(-1);
                ApplyScale();
                RefreshSlotDisplay();

                CreateTrayIcon();
                OnFollowTick(null, EventArgs.Empty);
                if (_pointerHook != null) _pointerHook.Start();
            }), DispatcherPriority.Background);
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
            if (_pointerHook != null)
            {
                _pointerHook.Activity -= OnPointerActivity;
                _pointerHook.Dispose();
                _pointerHook = null;
            }
            if (_foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
                _foregroundProc = null;
            }
            if (_tray != null)
            {
                // 先把图标摘掉再退出，不然托盘里会留一个点不动的死图标
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            // 临时弹出面板跟着一起收掉，别留个孤儿窗口
            ClosePopup();

            // 放大镜是独立窗口，不关掉会在屏幕上留一块孤零零的色条
            if (_magnifier != null)
            {
                try
                {
                    _magnifier.Disarm();
                    _magnifier.Close();
                }
                catch (Exception)
                {
                }
                _magnifier = null;
            }

            SessionStore.SetWindowRect(Left, Top, Width, Height);
            Application.Current.Shutdown();
        }
    }
}